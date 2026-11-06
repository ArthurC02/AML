using System.Text;
using Aml.Core.Identity;
using Aml.Core.Normalization;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Retrieval;

/// <summary>How a candidate reached the Case (identity-adjudication.md#retrieval-modes).</summary>
public enum RetrievalMode { DirectReference, ControlledSet, IndexSearch, PreviousLink }

/// <summary>
/// One retrieved Profile. The score only ranks candidates for inspection: it is never an identity probability and never
/// becomes evidence (ADR-0001). <paramref name="Truncated"/> marks a result list that holds fewer Profiles than matched,
/// or more than asked for because exact alias matches are never cut.
/// </summary>
public sealed record RetrievedCandidate(string ProfileId, string ProfileKey, string MatchedName, string Route, double Score, RetrievalMode Mode, bool Truncated = false);

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
    // Dice floors by query script. Latin is the Dice equivalent of Jaccard 0.3 (Dice = 2J / (1 + J)); Chinese is lower so a two- or
    // three-character name with one differing character still passes.
    public const double NGramFloorCjk = 0.3;
    public const double NGramFloorLatin = 2 * 0.3 / 1.3;
    private const int ShortCjkName = 3;

    private sealed record Alias(string ProfileKey, string Name, string Key, string[] Tokens, HashSet<string> Grams, bool Cjk, HashSet<string> ReadingKeys);

    private readonly List<Alias> _aliases = [];
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
            .Select(a => new RetrievedCandidate(ProfileKeys.IdOf(a.ProfileKey), a.ProfileKey, a.Name, ExactAlias, 1, RetrievalMode.IndexSearch))
            .ToList();
        var exactProfiles = exact.Select(c => c.ProfileKey).ToHashSet(StringComparer.Ordinal);

        var tokens = Tokens(name);
        var queryKey = Normalize.NameKey(name, true);
        var grams = Grams(queryKey);
        var readings = ReadingKeys(name);
        // Each route ranks Profiles on its own scale and only the ranks are fused, so a further route is one more entry.
        (string Route, Func<Alias, double> Score, double Floor)[] routes =
        [
            (Bm25, a => Bm25Score(tokens, a), 0),
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
            .Select(f => new RetrievedCandidate(ProfileKeys.IdOf(f.Key), f.Key, f.Value.Name, f.Value.Route, f.Value.Rrf, RetrievalMode.IndexSearch));
        var truncated = exact.Count + fused.Count > top;
        return exact.Concat(rest.Take(Math.Max(0, top - exact.Count)))
            .Select(c => truncated ? c with { Truncated = true } : c)
            .ToList();
    }

    /// <summary>
    /// Recall@K over labelled queries: the share whose expected Profile is among the first <paramref name="k"/> candidates
    /// <see cref="Search"/> returns for the name. The exact alias tier counts toward K like any other candidate.
    /// </summary>
    public double RecallAtK(IReadOnlyList<(string Name, string ExpectedProfileId)> queries, int k)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);
        if (queries.Count == 0) return 0;
        return (double)queries.Count(q => Search(q.Name, k).Take(k).Any(c => c.ProfileId == q.ExpectedProfileId)) / queries.Count;
    }

    /// <summary>Character n-gram Dice coefficient between two names: the score of the NGRAM route.</summary>
    public static double NGramScore(string a, string b) => Dice(Grams(Normalize.NameKey(a, true)), Grams(Normalize.NameKey(b, true)));

    private double Bm25Score(string[] query, Alias a)
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
