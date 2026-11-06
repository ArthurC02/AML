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
    // Lower for Chinese, so a two- or three-character name with one differing character still passes.
    public const double NGramFloorCjk = 0.3;
    private const double LatinJaccardFloor = 0.3;
    public const double NGramFloorLatin = 2 * LatinJaccardFloor / (1 + LatinJaccardFloor);

    private const int CjkGramLength = 2;
    private const int LatinGramLength = 3;
    private const int MaxCjkNameLengthWithUnigrams = 3;
    private const string StartMark = "^";
    private const string EndMark = "$";

    private sealed record Alias(string ProfileKey, string Name, string Key, string[] Tokens, HashSet<string> Grams, bool Cjk, HashSet<string> ReadingKeys);

    private readonly List<Alias> _aliases = [];
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private readonly Dictionary<bool, double> _averageTokenCountByIsCjk = [];
    // Every route scores 0 on an alias that shares no term with the query, so scoring only the posted aliases loses nothing.
    // ponytail: a very common term (a frequent surname, the "^ch" gram) still reaches a large share of the list; add top-k
    // pruning (WAND / MaxScore) or a stop-term cut-off when list-scale latency demands it.
    private readonly Dictionary<string, List<int>> _aliasPositionsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _aliasPositionsByToken = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _aliasPositionsByGram = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _aliasPositionsByReading = new(StringComparer.Ordinal);

    /// <param name="profiles">A ProfileKey given twice keeps its last Profile only, aliases and term counts included.</param>
    public ProfileIndex(IEnumerable<SourceProfile> profiles)
    {
        foreach (var p in profiles.GroupBy(p => p.ProfileKey, StringComparer.Ordinal).Select(g => g.Last()))
            foreach (var name in p.Names.Distinct())
                AddAlias(p.ProfileKey, name);
        foreach (var cjk in new[] { false, true })
            _averageTokenCountByIsCjk[cjk] = _aliases.Where(a => a.Cjk == cjk).Select(a => (double)a.Tokens.Length).DefaultIfEmpty(1).Average();
    }

    private void AddAlias(string profileKey, string name)
    {
        var key = NameComparison.NameKey(name, true);
        var punctuationOnly = key.Length == 0;
        if (punctuationOnly) return;
        var alias = new Alias(profileKey, name, key, Tokens(name), Grams(key), IsCjk(key), ReadingKeys(name));
        var at = _aliases.Count;
        _aliases.Add(alias);
        Post(_aliasPositionsByKey, [key], at);
        Post(_aliasPositionsByToken, alias.Tokens.Distinct(), at);
        Post(_aliasPositionsByGram, alias.Grams, at);
        Post(_aliasPositionsByReading, alias.ReadingKeys, at);
        foreach (var t in alias.Tokens.Distinct()) _documentFrequency[t] = _documentFrequency.GetValueOrDefault(t) + 1;
    }

    /// <summary>
    /// Every exact alias match first (never cut, ordered by alias text), then the remaining Profiles by reciprocal rank
    /// fusion of the scored routes, up to <paramref name="top"/> in total. An empty normalized query returns nothing.
    /// </summary>
    public IReadOnlyList<RetrievedCandidate> Search(string name, int top = 10)
    {
        var keys = NameComparison.NameKeys(name, true).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (keys.Count == 0) return [];

        var exact = ExactAliasTier(keys);
        var exactProfiles = exact.Select(c => c.ProfileKey).ToHashSet(StringComparer.Ordinal);
        var fused = FuseRouteRanks(name, exactProfiles);
        var rest = fused
            .OrderByDescending(f => f.Value.Rrf).ThenBy(f => f.Value.Name, StringComparer.Ordinal).ThenBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => new RetrievedCandidate(ProfileKeys.IdOf(f.Key), f.Key, f.Value.Name, f.Value.Route, f.Value.Rrf, RetrievalMode.IndexSearch));
        var truncated = exact.Count + fused.Count > top;
        return exact.Concat(rest.Take(Math.Max(0, top - exact.Count)))
            .Select(c => truncated ? c with { Truncated = true } : c)
            .ToList();
    }

    private List<RetrievedCandidate> ExactAliasTier(HashSet<string> keys) =>
        Lookup(_aliasPositionsByKey, keys).Select(i => _aliases[i])
            .GroupBy(a => a.ProfileKey)
            .Select(g => g.MinBy(a => a.Name, StringComparer.Ordinal)!)
            .OrderBy(a => a.Name, StringComparer.Ordinal).ThenBy(a => a.ProfileKey, StringComparer.Ordinal)
            .Select(a => new RetrievedCandidate(ProfileKeys.IdOf(a.ProfileKey), a.ProfileKey, a.Name, ExactAlias, 1, RetrievalMode.IndexSearch))
            .ToList();

    private Dictionary<string, (double Rrf, int BestRank, string Route, string Name)> FuseRouteRanks(string name, HashSet<string> exactProfiles)
    {
        var tokens = Tokens(name);
        var queryKey = NameComparison.NameKey(name, true);
        var grams = Grams(queryKey);
        var readings = ReadingKeys(name);
        (string Route, Func<Alias, double> Score, double Floor)[] routes =
        [
            (Bm25, a => Bm25Score(tokens, a), 0),
            (NGram, a => Dice(grams, a.Grams), IsCjk(queryKey) ? NGramFloorCjk : NGramFloorLatin),
            (Reading, a => a.ReadingKeys.Count(readings.Contains), 0),
        ];

        var reached = Lookup(_aliasPositionsByToken, tokens).Concat(Lookup(_aliasPositionsByGram, grams)).Concat(Lookup(_aliasPositionsByReading, readings))
            .Distinct().Select(i => _aliases[i]).Where(a => !exactProfiles.Contains(a.ProfileKey)).ToList();
        var fused = new Dictionary<string, (double Rrf, int BestRank, string Route, string Name)>(StringComparer.Ordinal);
        foreach (var (route, score, floor) in routes)
        {
            var ranked = BestAliasPerProfileByScore(reached, score, floor);
            for (var i = 0; i < ranked.Count; i++)
            {
                var (a, rank) = (ranked[i], i + 1);
                var f = fused.GetValueOrDefault(a.ProfileKey, (0, int.MaxValue, route, a.Name));
                var best = rank < f.BestRank;
                fused[a.ProfileKey] = (f.Rrf + 1 / (RrfK + rank), best ? rank : f.BestRank, best ? route : f.Route, best ? a.Name : f.Name);
            }
        }
        return fused;
    }

    private static List<Alias> BestAliasPerProfileByScore(List<Alias> reached, Func<Alias, double> score, double floor) =>
        reached
            .Select(a => (Alias: a, Score: score(a)))
            .Where(x => x.Score > 0 && x.Score >= floor)
            .GroupBy(x => x.Alias.ProfileKey)
            .Select(g => g.OrderByDescending(x => x.Score).ThenBy(x => x.Alias.Name, StringComparer.Ordinal).First())
            .OrderByDescending(x => x.Score).ThenBy(x => x.Alias.Name, StringComparer.Ordinal).ThenBy(x => x.Alias.ProfileKey, StringComparer.Ordinal)
            .Select(x => x.Alias)
            .ToList();

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
    public static double NGramScore(string a, string b) => Dice(Grams(NameComparison.NameKey(a, true)), Grams(NameComparison.NameKey(b, true)));

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
            score += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * a.Tokens.Length / _averageTokenCountByIsCjk[a.Cjk]));
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

    private static string[] Tokens(string name)
    {
        var words = name.Split([' ', '-', ',', '.', '·'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => NameComparison.NameKey(w, true)).Where(w => w.Length > 0).ToList();
        return words.SelectMany(w => IsCjk(w) ? CodePoints(w) : [w]).ToArray();
    }

    private static HashSet<string> Grams(string key)
    {
        var chars = CodePoints(key);
        var grams = new HashSet<string>(StringComparer.Ordinal);
        if (chars.Length == 0) return grams;
        var cjk = IsCjk(key);
        string[] padded = [StartMark, .. chars, EndMark];
        var n = cjk ? CjkGramLength : LatinGramLength;
        for (var i = 0; i + n <= padded.Length; i++) grams.Add(string.Concat(padded[i..(i + n)]));
        if (cjk && chars.Length <= MaxCjkNameLengthWithUnigrams) grams.UnionWith(chars);
        return grams;
    }

    private static string[] CodePoints(string key) => key.EnumerateRunes().Select(r => r.ToString()).ToArray();

    private static bool IsCjk(string key) => key.EnumerateRunes().Any(IsCjk);

    private const int CjkExtensionAStart = 0x3400;
    private const int CjkUnifiedEnd = 0x9FFF;
    private const int PrivateUseStart = 0xE000;
    private const int CjkCompatibilityEnd = 0xFAFF;
    private const int CjkExtensionBStart = 0x20000;
    private const int CjkExtensionsEnd = 0x323AF;

    // The private use area holds 造字, bank-defined characters for names.
    private static bool IsCjk(Rune r) => r.Value is >= CjkExtensionAStart and <= CjkUnifiedEnd
        or >= PrivateUseStart and <= CjkCompatibilityEnd
        or >= CjkExtensionBStart and <= CjkExtensionsEnd;
}
