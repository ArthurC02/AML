using Aml.Core.Normalization;
using Aml.Core.Subjects;

namespace Aml.Core.Retrieval;

/// <summary>How a candidate reached the Case (identity-adjudication.md#retrieval-modes).</summary>
public enum RetrievalMode { DirectReference, ControlledSet, IndexSearch, PreviousLink }

/// <summary>
/// One retrieved Profile. The score only ranks candidates for inspection: it is never an identity probability and never
/// becomes evidence (ADR-0001).
/// </summary>
public sealed record RetrievedCandidate(string ProfileKey, string MatchedName, string Route, double Score, RetrievalMode Mode);

/// <summary>
/// Search index over Profile names and aliases: exact alias (after normalization and controlled variants), word BM25
/// and character n-gram overlap. Results are counted by Profile, never by alias row.
/// </summary>
public sealed class ProfileIndex
{
    public const string ExactAlias = "EXACT_ALIAS";
    public const string Bm25 = "BM25";
    public const string NGram = "NGRAM";

    // ponytail: BM25 constants and the n-gram floor are textbook defaults; tune from Recall@K on labelled data.
    private const double K1 = 1.2;
    private const double B = 0.75;
    public const double NGramFloor = 0.3;

    private sealed record Alias(string ProfileKey, string Name, string Key, string[] Tokens, HashSet<string> Grams);

    private readonly List<Alias> _aliases = [];
    private readonly Dictionary<string, SourceProfile> _profiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private readonly double _averageLength;

    public ProfileIndex(IEnumerable<SourceProfile> profiles)
    {
        foreach (var p in profiles)
        {
            _profiles[p.ProfileKey] = p;
            foreach (var name in p.Names.Distinct())
            {
                var tokens = Tokens(name);
                _aliases.Add(new Alias(p.ProfileKey, name, Normalize.NameKey(name, true), tokens, Grams(name)));
                foreach (var t in tokens.Distinct()) _documentFrequency[t] = _documentFrequency.GetValueOrDefault(t) + 1;
            }
        }
        _averageLength = _aliases.Count == 0 ? 1 : _aliases.Average(a => a.Tokens.Length);
    }

    public int Count => _profiles.Count;

    public SourceProfile? Get(string profileKey) => _profiles.GetValueOrDefault(profileKey);

    /// <summary>Top N Profiles for a name, best route first: exact alias, then BM25, then n-gram overlap.</summary>
    public IReadOnlyList<RetrievedCandidate> Search(string name, int top = 10)
    {
        var key = Normalize.NameKey(name, true);
        var keys = Normalize.NameKeys(name, true).ToHashSet();
        var tokens = Tokens(name);
        var grams = Grams(name);
        var hits = new List<RetrievedCandidate>();
        foreach (var a in _aliases)
        {
            if (keys.Contains(a.Key) || a.Key == key)
            {
                hits.Add(new(a.ProfileKey, a.Name, ExactAlias, 1, RetrievalMode.IndexSearch));
                continue;
            }
            var bm25 = Score(tokens, a);
            if (bm25 > 0) hits.Add(new(a.ProfileKey, a.Name, Bm25, bm25, RetrievalMode.IndexSearch));
            var overlap = grams.Count == 0 ? 0 : (double)grams.Intersect(a.Grams).Count() / grams.Union(a.Grams).Count();
            if (overlap >= NGramFloor) hits.Add(new(a.ProfileKey, a.Name, NGram, overlap, RetrievalMode.IndexSearch));
        }
        return hits
            .GroupBy(h => h.ProfileKey)
            .Select(g => g.OrderBy(h => Rank(h.Route)).ThenByDescending(h => h.Score).First())
            .OrderBy(h => Rank(h.Route)).ThenByDescending(h => h.Score).ThenBy(h => h.ProfileKey, StringComparer.Ordinal)
            .Take(top)
            .ToList();
    }

    private static int Rank(string route) => route switch { ExactAlias => 0, Bm25 => 1, _ => 2 };

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
            score += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * a.Tokens.Length / _averageLength));
        }
        return score;
    }

    // Latin words, or single CJK characters (a Chinese name has no word breaks).
    private static string[] Tokens(string name)
    {
        var words = name.Split([' ', '-', ',', '.', '·'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Normalize.NameKey(w, true)).Where(w => w.Length > 0).ToList();
        return words.SelectMany(w => w.Any(IsCjk) ? w.Select(c => c.ToString()) : [w]).ToArray();
    }

    // Character bigrams for CJK, trigrams for Latin, over the normalized key.
    private static HashSet<string> Grams(string name)
    {
        var key = Normalize.NameKey(name, true);
        var n = key.Any(IsCjk) ? 2 : 3;
        var grams = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + n <= key.Length; i++) grams.Add(key.Substring(i, n));
        if (grams.Count == 0 && key.Length > 0) grams.Add(key);
        return grams;
    }

    private static bool IsCjk(char c) => c is >= '㐀' and <= '鿿';
}

/// <summary>A set of Profiles describing the same person. Each keeps its own ID and sources.</summary>
public sealed record MergedProfile(IReadOnlyList<string> ProfileKeys, IReadOnlyList<string> Basis);

/// <summary>
/// Merges Profiles across IDs or sources when the list database is built, never per Case. A merge needs a shared direct
/// identifier (comparable document key) or a recorded source link; name similarity alone never merges.
/// </summary>
public static class ProfileMerge
{
    public const string SharedDocument = "SHARED_DOCUMENT_KEY";
    public const string SourceLink = "RECORDED_SOURCE_LINK";

    // ponytail: relation labels that record "this is the same person" in the exports seen so far; extend with new sources.
    private static readonly HashSet<string> SameAs = new(StringComparer.OrdinalIgnoreCase) { "Same As", "Duplicate", "Duplicate Of", "Alias Profile", "同一人" };

    public static IReadOnlyList<MergedProfile> Group(IEnumerable<SourceProfile> profiles)
    {
        var list = profiles.ToList();
        var parent = list.ToDictionary(p => p.ProfileKey, p => p.ProfileKey, StringComparer.Ordinal);
        var basis = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        string Find(string k) => parent[k] == k ? k : parent[k] = Find(parent[k]);
        void Union(string a, string b, string why)
        {
            if (!parent.ContainsKey(b)) return;
            var (ra, rb) = (Find(a), Find(b));
            if (ra != rb) parent[rb] = ra;
            foreach (var k in new[] { a, b }) (basis.TryGetValue(k, out var s) ? s : basis[k] = []).Add(why);
        }

        foreach (var g in list.SelectMany(p => (p.Documents ?? []).Where(d => d.Country is not null)
                         .Select(d => (Key: $"{d.Type}|{d.Country}|{Normalize.NameKey(Normalize.HalfWidth(d.Number))}", p.ProfileKey)))
                     .GroupBy(x => x.Key))
            foreach (var other in g.Skip(1)) Union(g.First().ProfileKey, other.ProfileKey, SharedDocument);
        foreach (var p in list)
            foreach (var r in p.Relationships.Where(r => SameAs.Contains(r.Relation.Trim())))
                Union(p.ProfileKey, r.ProfileId.StartsWith("DJ:", StringComparison.Ordinal) ? r.ProfileId : "DJ:" + r.ProfileId, SourceLink);

        return list.GroupBy(p => Find(p.ProfileKey))
            .Where(g => g.Count() > 1)
            .Select(g => new MergedProfile(g.Select(p => p.ProfileKey).Order(StringComparer.Ordinal).ToList(),
                g.SelectMany(p => basis.GetValueOrDefault(p.ProfileKey) ?? []).Distinct().Order(StringComparer.Ordinal).ToList()))
            .ToList();
    }
}
