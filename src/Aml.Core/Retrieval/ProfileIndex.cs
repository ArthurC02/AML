using System.Collections.Frozen;
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

    // ponytail: the n-gram floors are starting values; tune from Recall@K on labelled data.
    // Lower for Chinese, so a two- or three-character name with one differing character still passes.
    public const double NGramFloorCjk = 0.3;
    private const double LatinJaccardFloor = 0.3;
    public const double NGramFloorLatin = 2 * LatinJaccardFloor / (1 + LatinJaccardFloor);

    // Below this many reached aliases, parallel scoring costs more than it saves.
    private const int MinAliasesForParallelScoring = 4096;

    // Fusion order: on equal rank the earlier route names the match.
    private static readonly string[] RouteNames = [Bm25, NGram, Reading];

    // Grams and reading keys are a handful of distinct strings each: an array is smaller than a set and as fast to scan.
    private sealed record Alias(string ProfileKey, string Name, string Key, string[] Tokens, string[] Grams, bool Cjk, string[] ReadingKeys);

    // Built once and never changed: concurrent searches share it without locks. Aliases are addressed by position.
    private readonly Alias[] _aliases;
    private readonly AliasProfiles _aliasProfiles;
    private readonly Bm25Scorer _bm25;
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
        ShareEqualTerms(_aliases);
        var (profileOf, profileCount) = ProfileNumbers(_aliases);
        _aliasProfiles = new AliasProfiles(profileOf, profileCount, NameOrder(_aliases));
        _aliasPositionsByKey = Postings(_aliases, a => [a.Key]);
        _aliasPositionsByToken = Postings(_aliases, a => a.Tokens.Distinct());
        _aliasPositionsByGram = Postings(_aliases, a => a.Grams);
        _aliasPositionsByReading = Postings(_aliases, a => a.ReadingKeys);
        _bm25 = new Bm25Scorer(_aliasPositionsByToken, _aliases.Length, AverageTokenCount(cjk: false), AverageTokenCount(cjk: true));
    }

    private static Alias? ToAlias(string profileKey, string name)
    {
        var key = NameTerms.Key(name);
        var punctuationOnly = key.Length == 0;
        return punctuationOnly ? null
            : new Alias(profileKey, name, key, NameTerms.Tokens(name), [.. NameTerms.Grams(key)], NameTerms.IsCjk(key), [.. NameTerms.ReadingKeys(name)]);
    }

    /// <summary>
    /// Points every equal token, gram and reading key at one string, so a common gram such as "^CH" is kept once, not once per
    /// alias. A pool local to this index, not string.Intern: the intern pool is never freed, and the list is rebuilt per version.
    /// </summary>
    private static void ShareEqualTerms(Alias[] aliases)
    {
        var pool = new HashSet<string>(StringComparer.Ordinal);
        void Share(string[] terms)
        {
            for (var i = 0; i < terms.Length; i++)
                if (!pool.TryGetValue(terms[i], out var shared)) pool.Add(terms[i]);
                else terms[i] = shared;
        }
        foreach (var a in aliases)
        {
            Share(a.Tokens);
            Share(a.Grams);
            Share(a.ReadingKeys);
        }
    }

    private static (int[] ProfileOf, int Count) ProfileNumbers(Alias[] aliases)
    {
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        var profileOf = aliases.Select(a => numbers.TryGetValue(a.ProfileKey, out var n) ? n : numbers[a.ProfileKey] = numbers.Count).ToArray();
        return (profileOf, numbers.Count);
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

    /// <summary>
    /// Every exact alias match first (never cut, ordered by alias text), then the remaining Profiles by reciprocal rank
    /// fusion of the scored routes, up to <paramref name="top"/> in total. An empty normalized query returns nothing.
    /// </summary>
    public IReadOnlyList<RetrievedCandidate> Search(string name, int top = 10)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfNegative(top);
        var keys = NameComparison.NameKeys(name, true).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (keys.Count == 0) return [];

        var exactAliases = keys.SelectMany(k => _aliasPositionsByKey.GetValueOrDefault(k) ?? []).ToArray();
        var exact = ExactAliasTier(exactAliases);
        var exactProfiles = exactAliases.Select(_aliasProfiles.ProfileOf).ToHashSet();
        var (rest, fusedCount) = TopFusedProfiles(name, exactProfiles, Math.Max(0, top - exact.Count));
        var truncated = exact.Count + fusedCount > top;
        return exact.Concat(rest)
            .Select(c => truncated ? c with { Truncated = true } : c)
            .ToList();
    }

    private List<RetrievedCandidate> ExactAliasTier(int[] positions) =>
        positions.Select(at => _aliases[at])
            .GroupBy(a => a.ProfileKey)
            .Select(g => g.MinBy(a => a.Name, StringComparer.Ordinal)!)
            .OrderBy(a => a.Name, StringComparer.Ordinal).ThenBy(a => a.ProfileKey, StringComparer.Ordinal)
            .Select(a => new RetrievedCandidate(ProfileKeys.IdOf(a.ProfileKey), a.ProfileKey, a.Name, ExactAlias, 1, RetrievalMode.IndexSearch))
            .ToList();

    /// <summary>The <paramref name="take"/> best Profiles by reciprocal rank fusion of the routes, and how many Profiles any route ranked.</summary>
    private (List<RetrievedCandidate> Top, int FusedCount) TopFusedProfiles(string name, HashSet<int> exactProfiles, int take)
    {
        var query = new Query(name);
        using var reached = new PooledArray<int>(_aliases.Length);
        var reachedCount = CollectReached(query, exactProfiles, reached);
        var parallel = reachedCount >= MinAliasesForParallelScoring;

        using var bm25 = new PooledArray<double>(reachedCount);
        using var nGram = new PooledArray<double>(reachedCount);
        using var reading = new PooledArray<double>(reachedCount);
        // Every score is a pure function of the query and one immutable alias, so aliases score in parallel into their own slots.
        ForEachIndex(reachedCount, parallel, i =>
        {
            var a = _aliases[reached[i]];
            bm25[i] = _bm25.Score(query.Tokens, a.Tokens, a.Cjk);
            nGram[i] = TermOverlap.Dice(query.Grams, a.Grams);
            reading[i] = TermOverlap.SharedCount(query.ReadingKeys, a.ReadingKeys);
        });

        using var bm25Ranked = new PooledArray<int>(_aliasProfiles.ProfileCount);
        using var nGramRanked = new PooledArray<int>(_aliasProfiles.ProfileCount);
        using var readingRanked = new PooledArray<int>(_aliasProfiles.ProfileCount);
        (PooledArray<double> Scores, double Floor, PooledArray<int> Ranked)[] routes =
            [(bm25, 0, bm25Ranked), (nGram, query.NGramFloor, nGramRanked), (reading, 0, readingRanked)];
        var rankedCount = new int[routes.Length];
        ForEachIndex(routes.Length, parallel, r =>
            rankedCount[r] = _aliasProfiles.BestAliasPerProfile(reached, reachedCount, routes[r].Scores, routes[r].Floor, routes[r].Ranked.Span));

        using var fusion = new ReciprocalRankFusion(_aliasProfiles);
        for (var r = 0; r < routes.Length; r++) fusion.AddRanking(routes[r].Ranked.Span[..rankedCount[r]], route: r);
        return (fusion.Best(take).Select(Candidate).ToList(), fusion.Count);
    }

    /// <summary>A query's terms per route, computed once per search.</summary>
    private sealed class Query
    {
        public string[] Tokens { get; }
        public HashSet<string> Grams { get; }
        public HashSet<string> ReadingKeys { get; }
        public double NGramFloor { get; }

        public Query(string name)
        {
            var key = NameTerms.Key(name);
            Tokens = NameTerms.Tokens(name).Distinct().ToArray();
            Grams = NameTerms.Grams(key);
            ReadingKeys = NameTerms.ReadingKeys(name);
            NGramFloor = NameTerms.IsCjk(key) ? NGramFloorCjk : NGramFloorLatin;
        }
    }

    private RetrievedCandidate Candidate(FusedProfile fused)
    {
        var a = _aliases[fused.Alias];
        return new RetrievedCandidate(ProfileKeys.IdOf(a.ProfileKey), a.ProfileKey, a.Name, RouteNames[fused.Route], fused.Score, RetrievalMode.IndexSearch);
    }

    /// <summary>Distinct aliases posted under any query term, outside the exact tier, written to the front of <paramref name="reached"/>.</summary>
    private int CollectReached(Query query, HashSet<int> exactProfiles, PooledArray<int> reached)
    {
        using var seen = new PooledArray<bool>(_aliases.Length, clear: true);
        var count = 0;
        Collect(_aliasPositionsByToken, query.Tokens, seen.Span, reached.Span, ref count);
        Collect(_aliasPositionsByGram, query.Grams, seen.Span, reached.Span, ref count);
        Collect(_aliasPositionsByReading, query.ReadingKeys, seen.Span, reached.Span, ref count);
        return count;

        void Collect(FrozenDictionary<string, int[]> postings, IEnumerable<string> terms, Span<bool> seenAt, Span<int> reachedAt, ref int count)
        {
            foreach (var t in terms)
                if (postings.TryGetValue(t, out var positions))
                    foreach (var at in positions)
                        if (!seenAt[at] && !exactProfiles.Contains(_aliasProfiles.ProfileOf(at)))
                        {
                            seenAt[at] = true;
                            reachedAt[count++] = at;
                        }
        }
    }

    private static void ForEachIndex(int count, bool parallel, Action<int> body)
    {
        if (parallel) Parallel.For(0, count, body);
        else for (var i = 0; i < count; i++) body(i);
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
    public static double NGramScore(string a, string b) =>
        TermOverlap.Dice(NameTerms.Grams(NameTerms.Key(a)), [.. NameTerms.Grams(NameTerms.Key(b))]);
}
