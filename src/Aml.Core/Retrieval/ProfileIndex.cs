using System.Collections.Frozen;
using System.Runtime.InteropServices;
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

    // Below this many reached aliases, parallel scoring costs more than it saves.
    private const int MinAliasesForParallelScoring = 4096;

    private sealed record Alias(string ProfileKey, string Name, string Key, string[] Tokens, FrozenSet<string> Grams, bool Cjk, FrozenSet<string> ReadingKeys);

    private sealed record FusedProfile(double Rrf, string Route, int MatchedAlias);

    private struct Fusion
    {
        public double Rrf;
        public int BestRank;
        public string? Route;
        public int MatchedAlias;
    }

    // Built once and never changed: concurrent searches share it without locks. Aliases are addressed by position.
    private readonly Alias[] _aliases;
    private readonly int[] _profileOf;
    // Each alias's rank by (name, Profile key): the ordinal tie-break of every ranking, as one integer comparison.
    private readonly int[] _nameOrder;
    private readonly FrozenDictionary<string, int> _documentFrequency;
    private readonly double _averageTokenCountLatin;
    private readonly double _averageTokenCountCjk;
    // Every route scores 0 on an alias that shares no term with the query, so scoring only the posted aliases loses nothing.
    // ponytail: a very common term (a frequent surname, the "^ch" gram) still reaches a large share of the list; add top-k
    // pruning (WAND / MaxScore) or a stop-term cut-off when list-scale latency demands it.
    private readonly FrozenDictionary<string, int[]> _aliasPositionsByKey;
    private readonly FrozenDictionary<string, int[]> _aliasPositionsByToken;
    private readonly FrozenDictionary<string, int[]> _aliasPositionsByGram;
    private readonly FrozenDictionary<string, int[]> _aliasPositionsByReading;

    /// <param name="profiles">A ProfileKey given twice keeps its last Profile only, aliases and term counts included.</param>
    public ProfileIndex(IEnumerable<SourceProfile> profiles)
    {
        _aliases = profiles.GroupBy(p => p.ProfileKey, StringComparer.Ordinal).Select(g => g.Last())
            .SelectMany(p => p.Names.Distinct().Select(name => (p.ProfileKey, Name: name)))
            .AsParallel().AsOrdered()
            .Select(x => ToAlias(x.ProfileKey, x.Name))
            .OfType<Alias>()
            .ToArray();
        _profileOf = ProfileNumbers(_aliases);
        _nameOrder = NameOrder(_aliases);
        _aliasPositionsByKey = Postings(_aliases, a => [a.Key]);
        _aliasPositionsByToken = Postings(_aliases, a => a.Tokens.Distinct());
        _aliasPositionsByGram = Postings(_aliases, a => a.Grams);
        _aliasPositionsByReading = Postings(_aliases, a => a.ReadingKeys);
        _documentFrequency = _aliasPositionsByToken.ToFrozenDictionary(t => t.Key, t => t.Value.Length, StringComparer.Ordinal);
        _averageTokenCountLatin = AverageTokenCount(cjk: false);
        _averageTokenCountCjk = AverageTokenCount(cjk: true);
    }

    private static Alias? ToAlias(string profileKey, string name)
    {
        var key = NameComparison.NameKey(name, true);
        var punctuationOnly = key.Length == 0;
        return punctuationOnly ? null
            : new Alias(profileKey, name, key, Tokens(name), Grams(key).ToFrozenSet(StringComparer.Ordinal), IsCjk(key), ReadingKeys(name).ToFrozenSet(StringComparer.Ordinal));
    }

    private static int[] ProfileNumbers(Alias[] aliases)
    {
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        return aliases.Select(a => numbers.TryGetValue(a.ProfileKey, out var n) ? n : numbers[a.ProfileKey] = numbers.Count).ToArray();
    }

    private static int[] NameOrder(Alias[] aliases)
    {
        var byName = Enumerable.Range(0, aliases.Length)
            .OrderBy(i => aliases[i].Name, StringComparer.Ordinal).ThenBy(i => aliases[i].ProfileKey, StringComparer.Ordinal).ToArray();
        var order = new int[aliases.Length];
        for (var rank = 0; rank < byName.Length; rank++) order[byName[rank]] = rank;
        return order;
    }

    private double AverageTokenCount(bool cjk) =>
        _aliases.Where(a => a.Cjk == cjk).Select(a => (double)a.Tokens.Length).DefaultIfEmpty(1).Average();

    /// <summary>
    /// Every exact alias match first (never cut, ordered by alias text), then the remaining Profiles by reciprocal rank
    /// fusion of the scored routes, up to <paramref name="top"/> in total. An empty normalized query returns nothing.
    /// </summary>
    public IReadOnlyList<RetrievedCandidate> Search(string name, int top = 10)
    {
        var keys = NameComparison.NameKeys(name, true).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (keys.Count == 0) return [];

        var exactAliases = Lookup(_aliasPositionsByKey, keys).ToArray();
        var exact = ExactAliasTier(exactAliases);
        var exactProfiles = exactAliases.Select(at => _profileOf[at]).ToHashSet();
        var fused = FuseRouteRanks(name, exactProfiles);
        var truncated = exact.Count + fused.Count > top;
        var rest = fused
            .OrderByDescending(f => f.Rrf).ThenBy(f => _nameOrder[f.MatchedAlias])
            .Take(Math.Max(0, top - exact.Count))
            .Select(ToCandidate);
        return exact.Concat(rest)
            .Select(c => truncated ? c with { Truncated = true } : c)
            .ToList();
    }

    private RetrievedCandidate ToCandidate(FusedProfile f)
    {
        var a = _aliases[f.MatchedAlias];
        return new RetrievedCandidate(ProfileKeys.IdOf(a.ProfileKey), a.ProfileKey, a.Name, f.Route, f.Rrf, RetrievalMode.IndexSearch);
    }

    private List<RetrievedCandidate> ExactAliasTier(int[] positions) =>
        positions.Select(at => _aliases[at])
            .GroupBy(a => a.ProfileKey)
            .Select(g => g.MinBy(a => a.Name, StringComparer.Ordinal)!)
            .OrderBy(a => a.Name, StringComparer.Ordinal).ThenBy(a => a.ProfileKey, StringComparer.Ordinal)
            .Select(a => new RetrievedCandidate(ProfileKeys.IdOf(a.ProfileKey), a.ProfileKey, a.Name, ExactAlias, 1, RetrievalMode.IndexSearch))
            .ToList();

    private List<FusedProfile> FuseRouteRanks(string name, HashSet<int> exactProfiles)
    {
        var tokens = Tokens(name).Distinct().ToArray();
        var queryKey = NameComparison.NameKey(name, true);
        var grams = Grams(queryKey);
        var readings = ReadingKeys(name);
        var reached = Lookup(_aliasPositionsByToken, tokens).Concat(Lookup(_aliasPositionsByGram, grams)).Concat(Lookup(_aliasPositionsByReading, readings))
            .Distinct().Where(at => !exactProfiles.Contains(_profileOf[at])).ToArray();

        // Every score is a pure function of the query and one immutable alias, and every ranking a pure function of the
        // scores, so both run in parallel; rankings order by score and then name order, so the result does not depend on it.
        var parallel = reached.Length >= MinAliasesForParallelScoring;
        (string Route, double[] Scores, double Floor)[] routes =
        [
            (Bm25, new double[reached.Length], 0),
            (NGram, new double[reached.Length], IsCjk(queryKey) ? NGramFloorCjk : NGramFloorLatin),
            (Reading, new double[reached.Length], 0),
        ];
        ForEachIndex(reached.Length, parallel, i =>
        {
            var a = _aliases[reached[i]];
            routes[0].Scores[i] = Bm25Score(tokens, a);
            routes[1].Scores[i] = Dice(grams, a.Grams);
            routes[2].Scores[i] = a.ReadingKeys.Count(readings.Contains);
        });
        var rankings = new int[routes.Length][];
        ForEachIndex(routes.Length, parallel, r => rankings[r] = BestAliasPerProfileByScore(reached, routes[r].Scores, routes[r].Floor));

        // Fused in route order: on equal rank the earlier route names the match.
        var fused = new Dictionary<int, Fusion>();
        for (var r = 0; r < routes.Length; r++)
            for (var i = 0; i < rankings[r].Length; i++)
            {
                var (at, rank) = (rankings[r][i], i + 1);
                ref var f = ref CollectionsMarshal.GetValueRefOrAddDefault(fused, _profileOf[at], out var seen);
                if (!seen) f.BestRank = int.MaxValue;
                f.Rrf += 1 / (RrfK + rank);
                if (rank < f.BestRank) (f.BestRank, f.Route, f.MatchedAlias) = (rank, routes[r].Route, at);
            }
        return fused.Values.Select(f => new FusedProfile(f.Rrf, f.Route!, f.MatchedAlias)).ToList();
    }

    private static void ForEachIndex(int count, bool parallel, Action<int> body)
    {
        if (parallel) Parallel.For(0, count, body);
        else for (var i = 0; i < count; i++) body(i);
    }

    /// <summary>Alias positions, one per Profile (its best-scoring alias), by score and then name order.</summary>
    private int[] BestAliasPerProfileByScore(int[] reached, double[] scores, double floor)
    {
        bool Before(int i, int j) => scores[i] > scores[j] || (scores[i] == scores[j] && _nameOrder[reached[i]] < _nameOrder[reached[j]]);

        var bestByProfile = new Dictionary<int, int>();
        for (var i = 0; i < reached.Length; i++)
        {
            if (!(scores[i] > 0 && scores[i] >= floor)) continue;
            ref var best = ref CollectionsMarshal.GetValueRefOrAddDefault(bestByProfile, _profileOf[reached[i]], out var seen);
            if (!seen || Before(i, best)) best = i;
        }
        var ranked = bestByProfile.Values.ToArray();
        Array.Sort(ranked, (i, j) => Before(i, j) ? -1 : Before(j, i) ? 1 : 0);
        return ranked.Select(i => reached[i]).ToArray();
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
    public static double NGramScore(string a, string b) => Dice(Grams(NameComparison.NameKey(a, true)), Grams(NameComparison.NameKey(b, true)));

    private double Bm25Score(string[] distinctQueryTokens, Alias a)
    {
        var n = _aliases.Length;
        var averageTokenCount = a.Cjk ? _averageTokenCountCjk : _averageTokenCountLatin;
        var score = 0.0;
        foreach (var t in distinctQueryTokens)
        {
            var tf = a.Tokens.Count(x => x == t);
            if (tf == 0) continue;
            var df = _documentFrequency.GetValueOrDefault(t);
            var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
            score += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * a.Tokens.Length / averageTokenCount));
        }
        return score;
    }

    private static FrozenDictionary<string, int[]> Postings(Alias[] aliases, Func<Alias, IEnumerable<string>> terms)
    {
        var postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var at = 0; at < aliases.Length; at++)
            foreach (var t in terms(aliases[at]))
            {
                if (!postings.TryGetValue(t, out var list)) postings[t] = list = [];
                list.Add(at);
            }
        return postings.ToFrozenDictionary(t => t.Key, t => t.Value.ToArray(), StringComparer.Ordinal);
    }

    private static IEnumerable<int> Lookup(FrozenDictionary<string, int[]> postings, IEnumerable<string> terms) =>
        terms.Distinct().SelectMany(t => postings.GetValueOrDefault(t) ?? []);

    private static HashSet<string> ReadingKeys(string name) => Romanization.ReadingKeys(name).ToHashSet(StringComparer.Ordinal);

    private static double Dice(HashSet<string> a, IReadOnlySet<string> b) =>
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
