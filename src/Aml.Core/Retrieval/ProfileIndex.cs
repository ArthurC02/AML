using System.Text;
using Aml.Core.Normalization;
using Aml.Core.Subjects;

namespace Aml.Core.Retrieval;

/// <summary>How a candidate reached the Case (identity-adjudication.md#retrieval-modes).</summary>
public enum RetrievalMode { DirectReference, ControlledSet, IndexSearch, PreviousLink }

/// <summary>
/// One retrieved Profile. The score only ranks candidates for inspection: it is never an identity probability and never
/// becomes evidence (ADR-0001). <paramref name="Truncated"/> marks a result list that holds fewer Profiles than matched,
/// or more than asked for because exact alias matches are never cut.
/// </summary>
public sealed record RetrievedCandidate(string ProfileKey, string MatchedName, string Route, double Score, RetrievalMode Mode, bool Truncated = false);

/// <summary>
/// Search index over Profile names and aliases. Exact alias matches (after normalization and controlled variants) form
/// their own tier, listed first and never cut. The other routes (word BM25, character n-gram Dice, shared Pinyin reading
/// key) each rank Profiles and are fused by reciprocal rank. A shared reading key links Chinese characters to their
/// romanizations (and one romanization system to another): it is a retrieval route only, never an exact match. Results are counted by Profile, never by alias row.
/// </summary>
public sealed class ProfileIndex
{
    public const string ExactAlias = "EXACT_ALIAS";
    public const string Bm25 = "BM25";
    public const string NGram = "NGRAM";
    public const string Reading = "READING";

    // ponytail: k1 and the RRF k are literature defaults; b 0.3 (short name fields) and the n-gram floors are starting values. Tune all from Recall@K on labelled data.
    private const double K1 = 1.2;
    private const double B = 0.3;
    private const double RrfK = 60;
    // Dice floors by query script. Latin keeps the former Jaccard 0.3 (Dice = 2J / (1 + J)); Chinese is lower so a two- or
    // three-character name with one differing character still passes.
    public const double NGramFloorCjk = 0.3;
    public const double NGramFloorLatin = 2 * 0.3 / 1.3;
    private const int ShortCjkName = 3;

    private sealed record Alias(string ProfileKey, string Name, string Key, string[] Tokens, HashSet<string> Grams, bool Cjk, HashSet<string> ReadingKeys);

    private readonly List<Alias> _aliases = [];
    private readonly Dictionary<string, SourceProfile> _profiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private readonly Dictionary<bool, double> _averageLength = [];
    // Postings (alias positions) by normalized key and by each route's terms. Every route scores 0 on an alias that shares
    // no term with the query, so a search scores only the aliases its terms reach, never the whole list.
    // ponytail: a very common term (a frequent surname, the "^ch" gram) still reaches a large share of the list; add top-k
    // pruning (WAND / MaxScore) or a stop-term cut-off when list-scale latency demands it.
    private readonly Dictionary<string, List<int>> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _byToken = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _byGram = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _byReading = new(StringComparer.Ordinal);

    /// <param name="profiles">A ProfileKey given twice keeps its last Profile only, aliases and term counts included.</param>
    public ProfileIndex(IEnumerable<SourceProfile> profiles)
    {
        foreach (var p in profiles.GroupBy(p => p.ProfileKey, StringComparer.Ordinal).Select(g => g.Last()))
        {
            _profiles[p.ProfileKey] = p;
            foreach (var name in p.Names.Distinct())
            {
                var key = Normalize.NameKey(name, true);
                if (key.Length == 0) continue; // 「-」 or punctuation only: it would match every other empty key
                var alias = new Alias(p.ProfileKey, name, key, Tokens(name), Grams(key), IsCjk(key), ReadingKeys(name));
                var at = _aliases.Count;
                _aliases.Add(alias);
                Post(_byKey, [key], at);
                Post(_byToken, alias.Tokens.Distinct(), at);
                Post(_byGram, alias.Grams, at);
                Post(_byReading, alias.ReadingKeys, at);
                foreach (var t in alias.Tokens.Distinct()) _documentFrequency[t] = _documentFrequency.GetValueOrDefault(t) + 1;
            }
        }
        // Chinese aliases are counted in characters, Latin ones in words: one shared average would skew both.
        foreach (var cjk in new[] { false, true })
            _averageLength[cjk] = _aliases.Where(a => a.Cjk == cjk).Select(a => (double)a.Tokens.Length).DefaultIfEmpty(1).Average();
    }

    public int Count => _profiles.Count;

    public SourceProfile? Get(string profileKey) => _profiles.GetValueOrDefault(profileKey);

    /// <summary>
    /// Every exact alias match first (never cut, ordered by alias text), then the remaining Profiles by reciprocal rank
    /// fusion of the scored routes, up to <paramref name="top"/> in total. An empty normalized query returns nothing.
    /// </summary>
    public IReadOnlyList<RetrievedCandidate> Search(string name, int top = 10)
    {
        var keys = Normalize.NameKeys(name, true).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (keys.Count == 0) return [];

        var exact = Lookup(_byKey, keys).Select(i => _aliases[i])
            .GroupBy(a => a.ProfileKey)
            .Select(g => g.MinBy(a => a.Name, StringComparer.Ordinal)!)
            .OrderBy(a => a.Name, StringComparer.Ordinal).ThenBy(a => a.ProfileKey, StringComparer.Ordinal)
            .Select(a => new RetrievedCandidate(a.ProfileKey, a.Name, ExactAlias, 1, RetrievalMode.IndexSearch))
            .ToList();
        var exactProfiles = exact.Select(c => c.ProfileKey).ToHashSet(StringComparer.Ordinal);

        var tokens = Tokens(name);
        var queryKey = Normalize.NameKey(name, true);
        var grams = Grams(queryKey);
        var readings = ReadingKeys(name);
        // Each route ranks Profiles on its own scale and only the ranks are fused, so a further route is one more entry.
        (string Route, Func<Alias, double> Score, double Floor)[] routes =
        [
            (Bm25, a => Score(tokens, a), 0),
            (NGram, a => Dice(grams, a.Grams), IsCjk(queryKey) ? NGramFloorCjk : NGramFloorLatin),
            (Reading, a => a.ReadingKeys.Count(readings.Contains), 0),
        ];

        // Every ordering below ends in a total order (score, alias, Profile key), so the order postings are read in does not matter.
        var reached = Lookup(_byToken, tokens).Concat(Lookup(_byGram, grams)).Concat(Lookup(_byReading, readings))
            .Distinct().Select(i => _aliases[i]).Where(a => !exactProfiles.Contains(a.ProfileKey)).ToList();
        var fused = new Dictionary<string, (double Rrf, int BestRank, string Route, string Name)>(StringComparer.Ordinal);
        foreach (var (route, score, floor) in routes)
        {
            var ranked = reached
                .Select(a => (Alias: a, Score: score(a)))
                .Where(x => x.Score > 0 && x.Score >= floor)
                .GroupBy(x => x.Alias.ProfileKey)
                .Select(g => g.OrderByDescending(x => x.Score).ThenBy(x => x.Alias.Name, StringComparer.Ordinal).First())
                .OrderByDescending(x => x.Score).ThenBy(x => x.Alias.Name, StringComparer.Ordinal).ThenBy(x => x.Alias.ProfileKey, StringComparer.Ordinal)
                .ToList();
            for (var i = 0; i < ranked.Count; i++)
            {
                var (a, rank) = (ranked[i].Alias, i + 1);
                var f = fused.GetValueOrDefault(a.ProfileKey, (0, int.MaxValue, route, a.Name));
                var best = rank < f.BestRank;
                fused[a.ProfileKey] = (f.Rrf + 1 / (RrfK + rank), best ? rank : f.BestRank, best ? route : f.Route, best ? a.Name : f.Name);
            }
        }

        var rest = fused
            .OrderByDescending(f => f.Value.Rrf).ThenBy(f => f.Value.Name, StringComparer.Ordinal).ThenBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => new RetrievedCandidate(f.Key, f.Value.Name, f.Value.Route, f.Value.Rrf, RetrievalMode.IndexSearch));
        var truncated = exact.Count + fused.Count > top;
        return exact.Concat(rest.Take(Math.Max(0, top - exact.Count)))
            .Select(c => truncated ? c with { Truncated = true } : c)
            .ToList();
    }

    /// <summary>Character n-gram Dice coefficient between two names: the score of the NGRAM route.</summary>
    public static double NGramScore(string a, string b) => Dice(Grams(Normalize.NameKey(a, true)), Grams(Normalize.NameKey(b, true)));

    private double Score(string[] query, Alias a)
    {
        var n = _aliases.Count;
        var score = 0.0;
        foreach (var t in query.Distinct())
        {
            var tf = a.Tokens.Count(x => x == t);
            if (tf == 0) continue;
            var df = _documentFrequency.GetValueOrDefault(t);
            var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
            score += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * a.Tokens.Length / _averageLength[a.Cjk]));
        }
        return score;
    }

    private static void Post(Dictionary<string, List<int>> postings, IEnumerable<string> terms, int at)
    {
        foreach (var t in terms)
        {
            if (!postings.TryGetValue(t, out var list)) postings[t] = list = [];
            list.Add(at);
        }
    }

    private static IEnumerable<int> Lookup(Dictionary<string, List<int>> postings, IEnumerable<string> terms) =>
        terms.Distinct().SelectMany(t => postings.GetValueOrDefault(t) ?? []);

    private static HashSet<string> ReadingKeys(string name) => Romanization.ReadingKeys(name).ToHashSet(StringComparer.Ordinal);

    private static double Dice(HashSet<string> a, HashSet<string> b) =>
        a.Count + b.Count == 0 ? 0 : 2.0 * a.Count(b.Contains) / (a.Count + b.Count);

    // Latin words, or single CJK characters (a Chinese name has no word breaks).
    private static string[] Tokens(string name)
    {
        var words = name.Split([' ', '-', ',', '.', '·'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Normalize.NameKey(w, true)).Where(w => w.Length > 0).ToList();
        return words.SelectMany(w => IsCjk(w) ? Chars(w) : [w]).ToArray();
    }

    // Over the normalized key with boundary marks: CJK bigrams, plus unigrams for names of up to three characters (where
    // one differing character leaves almost no bigram in common), or Latin trigrams.
    private static HashSet<string> Grams(string key)
    {
        var chars = Chars(key);
        var grams = new HashSet<string>(StringComparer.Ordinal);
        if (chars.Length == 0) return grams;
        var cjk = IsCjk(key);
        string[] padded = ["^", .. chars, "$"];
        var n = cjk ? 2 : 3;
        for (var i = 0; i + n <= padded.Length; i++) grams.Add(string.Concat(padded[i..(i + n)]));
        if (cjk && chars.Length <= ShortCjkName) grams.UnionWith(chars);
        return grams;
    }

    // By code point, so a supplementary-plane character stays one character.
    private static string[] Chars(string key) => key.EnumerateRunes().Select(r => r.ToString()).ToArray();

    private static bool IsCjk(string key) => key.EnumerateRunes().Any(IsCjk);

    // Unified ideographs (extension A, compatibility, extensions B and later) and the private use area (造字).
    private static bool IsCjk(Rune r) => r.Value is >= 0x3400 and <= 0x9FFF or >= 0xE000 and <= 0xFAFF or >= 0x20000 and <= 0x323AF;
}

/// <summary>One merge decision: the Profile pair, its basis, the document key behind it and, when refused, why.</summary>
public sealed record MergeEdge(string A, string B, string Basis, string? DocumentKey = null, string? Conflict = null);

/// <summary>A set of Profiles describing the same person. Each keeps its own ID and sources; every merge edge is kept.</summary>
public sealed record MergedProfile(IReadOnlyList<string> ProfileKeys, IReadOnlyList<MergeEdge> Edges, IReadOnlyList<string> Findings)
{
    public IReadOnlyList<string> Basis => Edges.Select(e => e.Basis).Distinct().Order(StringComparer.Ordinal).ToList();
}

/// <summary>Merged groups, and the merges refused because they would put conflicting Profiles in one group.</summary>
public sealed record MergeResult(IReadOnlyList<MergedProfile> Groups, IReadOnlyList<MergeEdge> Rejected);

/// <summary>
/// Merges Profiles across IDs or sources when the list database is built, never per Case. A merge needs a shared direct
/// identifier (comparable document key) or a recorded source link; name similarity alone never merges. A merge that would
/// put Profiles with contradicting DOBs or opposite genders in one group is refused and recorded.
/// </summary>
public static class ProfileMerge
{
    public const string SharedDocument = "SHARED_DOCUMENT_KEY";
    public const string SourceLink = "RECORDED_SOURCE_LINK";
    public const string OversizedGroup = "OVERSIZED_GROUP";
    public const string GenderConflict = "GENDER_CONFLICT";
    public const string DobConflict = "DOB_CONFLICT";

    // ponytail: group size and number length limits are judgement calls; revisit with real list volumes.
    private const int MaxGroupSize = 10;
    private const int MinDocumentLength = 4;

    // ponytail: relation labels that record "this is the same person" in the exports seen so far; extend with new sources.
    private static readonly HashSet<string> SameAs = new(StringComparer.OrdinalIgnoreCase) { "Same As", "Duplicate", "Duplicate Of", "Alias Profile", "同一人" };

    public static IReadOnlyList<MergedProfile> Group(IEnumerable<SourceProfile> profiles) => Resolve(profiles).Groups;

    public static MergeResult Resolve(IEnumerable<SourceProfile> profiles)
    {
        var list = profiles.ToList();
        var byKey = list.ToDictionary(p => p.ProfileKey, StringComparer.Ordinal);
        var parent = list.ToDictionary(p => p.ProfileKey, p => p.ProfileKey, StringComparer.Ordinal);
        var members = list.ToDictionary(p => p.ProfileKey, p => new List<string> { p.ProfileKey }, StringComparer.Ordinal);
        var (edges, rejected) = (new List<MergeEdge>(), new List<MergeEdge>());

        string Find(string k)
        {
            var root = k;
            while (parent[root] != root) root = parent[root];
            while (parent[k] != root)
            {
                var next = parent[k];
                parent[k] = root;
                k = next;
            }
            return root;
        }

        // Union by size; refused when any member of one group conflicts with any member of the other.
        void Union(string a, string b, string why, string? documentKey = null)
        {
            if (a == b || !parent.ContainsKey(b)) return;
            var (ra, rb) = (Find(a), Find(b));
            var edge = new MergeEdge(a, b, why, documentKey);
            if (ra != rb)
            {
                var conflict = members[ra].SelectMany(x => members[rb].Select(y => Conflict(byKey[x], byKey[y]) is { } c ? $"{c}({x},{y})" : null))
                    .FirstOrDefault(c => c is not null);
                if (conflict is not null)
                {
                    rejected.Add(edge with { Conflict = conflict });
                    return;
                }
                if (members[ra].Count < members[rb].Count) (ra, rb) = (rb, ra);
                parent[rb] = ra;
                members[ra].AddRange(members[rb]);
                members.Remove(rb);
            }
            edges.Add(edge);
        }

        foreach (var g in list.SelectMany(p => (p.Documents ?? []).Select(d => (Key: DocumentKey(d), p.ProfileKey)))
                     .Where(x => x.Key is not null).Distinct().GroupBy(x => x.Key!))
            foreach (var other in g.Skip(1)) Union(g.First().ProfileKey, other.ProfileKey, SharedDocument, g.Key);
        foreach (var p in list)
            foreach (var r in p.Relationships.Where(r => SameAs.Contains(r.Relation.Trim())))
                Union(p.ProfileKey, r.ProfileId.StartsWith("DJ:", StringComparison.Ordinal) ? r.ProfileId : "DJ:" + r.ProfileId, SourceLink);

        var groups = members.Values.Where(m => m.Count > 1)
            .Select(m => m.Order(StringComparer.Ordinal).ToList())
            .OrderBy(m => m[0], StringComparer.Ordinal)
            .Select(m => new MergedProfile(m, edges.Where(e => m.Contains(e.A)).ToList(), m.Count > MaxGroupSize ? [OversizedGroup] : []))
            .ToList();
        return new MergeResult(groups, rejected);
    }

    /// <summary>
    /// Comparable document key, or null when the number is blank, a placeholder, shorter than four characters or one
    /// repeated character, or the type or issuing country is unknown.
    /// </summary>
    private static string? DocumentKey(Document d)
    {
        var (type, country) = (Normalize.NullIfPlaceholder(d.Type), Normalize.NullIfPlaceholder(d.Country));
        if (type is null || country is null || Normalize.IsPlaceholder(d.Number)) return null;
        var number = Normalize.NameKey(Normalize.HalfWidth(d.Number));
        return number.Length < MinDocumentLength || number.Distinct().Count() == 1 ? null : $"{type}|{country}|{number}";
    }

    private static string? Conflict(SourceProfile x, SourceProfile y) =>
        x.Gender is { } g && y.Gender is { } h && g != h ? GenderConflict
        : x.DatesOfBirth.Count > 0 && y.DatesOfBirth.Count > 0 && !x.DatesOfBirth.Any(d => y.DatesOfBirth.Any(d.AgreesWith)) ? DobConflict
        : null;
}
