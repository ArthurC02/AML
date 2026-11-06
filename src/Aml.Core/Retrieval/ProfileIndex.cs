using System.Buffers;
using System.Collections.Frozen;
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
    private const int MaxStackallocInts = 256;

    // Below this many reached aliases, parallel scoring costs more than it saves.
    private const int MinAliasesForParallelScoring = 4096;

    private static readonly string[] RouteNames = [Bm25, NGram, Reading];

    // Grams and reading keys are a handful of distinct strings each: an array is smaller than a set and as fast to scan.
    private sealed record Alias(string ProfileKey, string Name, string Key, string[] Tokens, string[] Grams, bool Cjk, string[] ReadingKeys);

    // Built once and never changed: concurrent searches share it without locks. Aliases are addressed by position.
    private readonly Alias[] _aliases;
    private readonly int[] _profileOf;
    private readonly int _profileCount;
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
        ShareEqualTerms(_aliases);
        (_profileOf, _profileCount) = ProfileNumbers(_aliases);
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
            : new Alias(profileKey, name, key, Tokens(name), [.. Grams(key)], IsCjk(key), [.. ReadingKeys(name)]);
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

    /// <summary>
    /// The <paramref name="take"/> best Profiles by reciprocal rank fusion, and how many Profiles any route ranked. Every
    /// buffer sized by the list is rented and returned, so a search leaves no list-sized garbage behind.
    /// </summary>
    private (List<RetrievedCandidate> Top, int FusedCount) TopFusedProfiles(string name, HashSet<int> exactProfiles, int take)
    {
        var tokens = Tokens(name).Distinct().ToArray();
        var queryKey = NameComparison.NameKey(name, true);
        var grams = Grams(queryKey);
        var readings = ReadingKeys(name);
        var floors = new[] { 0, IsCjk(queryKey) ? NGramFloorCjk : NGramFloorLatin, 0 };

        var reached = ArrayPool<int>.Shared.Rent(_aliases.Length);
        var scores = RouteNames.Select(_ => ArrayPool<double>.Shared.Rent(_aliases.Length)).ToArray();
        var rankings = RouteNames.Select(_ => ArrayPool<int>.Shared.Rent(_profileCount)).ToArray();
        var rankingLengths = new int[RouteNames.Length];
        var fusion = new FusionBuffers(_profileCount);
        try
        {
            var reachedCount = CollectReached(tokens, grams, readings, exactProfiles, reached);
            // Every score is a pure function of the query and one immutable alias, and every ranking a pure function of the
            // scores, so both run in parallel into their own slots; rankings order by score, then name order.
            var parallel = reachedCount >= MinAliasesForParallelScoring;
            ForEachIndex(reachedCount, parallel, i =>
            {
                var a = _aliases[reached[i]];
                scores[0][i] = Bm25Score(tokens, a);
                scores[1][i] = Dice(grams, a.Grams);
                scores[2][i] = SharedCount(readings, a.ReadingKeys);
            });
            ForEachIndex(RouteNames.Length, parallel, r =>
                rankingLengths[r] = BestAliasPerProfileByScore(reached, reachedCount, scores[r], floors[r], rankings[r]));

            // Fused in route order: on equal rank the earlier route names the match.
            for (var r = 0; r < RouteNames.Length; r++)
                for (var i = 0; i < rankingLengths[r]; i++)
                    fusion.Add(_profileOf[rankings[r][i]], rankings[r][i], rank: i + 1, route: r);
            var top = fusion.Best(take, _nameOrder).Select(p => Candidate(fusion, p)).ToList();
            return (top, fusion.Count);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(reached);
            foreach (var s in scores) ArrayPool<double>.Shared.Return(s);
            foreach (var r in rankings) ArrayPool<int>.Shared.Return(r);
            fusion.Return();
        }
    }

    private RetrievedCandidate Candidate(FusionBuffers fusion, int profile)
    {
        var a = _aliases[fusion.MatchedAlias[profile]];
        return new RetrievedCandidate(ProfileKeys.IdOf(a.ProfileKey), a.ProfileKey, a.Name, RouteNames[fusion.Route[profile]], fusion.Rrf[profile], RetrievalMode.IndexSearch);
    }

    /// <summary>Distinct aliases posted under any query term, outside the exact tier, written to the front of <paramref name="reached"/>.</summary>
    private int CollectReached(string[] tokens, HashSet<string> grams, HashSet<string> readings, HashSet<int> exactProfiles, int[] reached)
    {
        var seen = ArrayPool<bool>.Shared.Rent(_aliases.Length);
        Array.Clear(seen, 0, _aliases.Length);
        var count = 0;
        try
        {
            void Collect(FrozenDictionary<string, int[]> postings, IEnumerable<string> terms)
            {
                foreach (var t in terms)
                    if (postings.TryGetValue(t, out var positions))
                        foreach (var at in positions)
                            if (!seen[at] && !exactProfiles.Contains(_profileOf[at]))
                            {
                                seen[at] = true;
                                reached[count++] = at;
                            }
            }
            Collect(_aliasPositionsByToken, tokens);
            Collect(_aliasPositionsByGram, grams);
            Collect(_aliasPositionsByReading, readings);
            return count;
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(seen);
        }
    }

    private static void ForEachIndex(int count, bool parallel, Action<int> body)
    {
        if (parallel) Parallel.For(0, count, body);
        else for (var i = 0; i < count; i++) body(i);
    }

    /// <summary>
    /// One alias position per Profile (its best-scoring alias), by score and then name order, written to the front of
    /// <paramref name="ranked"/>; returns how many.
    /// </summary>
    private int BestAliasPerProfileByScore(int[] reached, int reachedCount, double[] scores, double floor, int[] ranked)
    {
        var bestOf = ArrayPool<int>.Shared.Rent(_profileCount);
        var winners = ArrayPool<int>.Shared.Rent(_profileCount);
        Array.Fill(bestOf, -1, 0, _profileCount);
        try
        {
            var count = 0;
            for (var i = 0; i < reachedCount; i++)
            {
                if (!(scores[i] > 0 && scores[i] >= floor)) continue;
                var p = _profileOf[reached[i]];
                if (bestOf[p] < 0) winners[count++] = p;
                if (bestOf[p] < 0 || Before(scores, reached, i, bestOf[p])) bestOf[p] = i;
            }
            var order = winners.AsSpan(0, count);
            for (var w = 0; w < order.Length; w++) order[w] = bestOf[order[w]];
            order.Sort((i, j) => Before(scores, reached, i, j) ? -1 : Before(scores, reached, j, i) ? 1 : 0);
            for (var w = 0; w < order.Length; w++) ranked[w] = reached[order[w]];
            return count;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(bestOf);
            ArrayPool<int>.Shared.Return(winners);
        }
    }

    private bool Before(double[] scores, int[] reached, int i, int j) =>
        scores[i] > scores[j] || (scores[i] == scores[j] && _nameOrder[reached[i]] < _nameOrder[reached[j]]);

    /// <summary>Per-Profile fusion state in rented arrays; a Profile is present once a route ranked it (BestRank above 0).</summary>
    private sealed class FusionBuffers
    {
        public readonly double[] Rrf;
        public readonly int[] BestRank;
        public readonly int[] Route;
        public readonly int[] MatchedAlias;
        private readonly int[] _present;

        public int Count { get; private set; }

        public FusionBuffers(int profiles)
        {
            Rrf = ArrayPool<double>.Shared.Rent(profiles);
            BestRank = ArrayPool<int>.Shared.Rent(profiles);
            Route = ArrayPool<int>.Shared.Rent(profiles);
            MatchedAlias = ArrayPool<int>.Shared.Rent(profiles);
            _present = ArrayPool<int>.Shared.Rent(profiles);
            Array.Clear(BestRank, 0, profiles);
        }

        public void Add(int profile, int alias, int rank, int route)
        {
            if (BestRank[profile] == 0)
            {
                _present[Count++] = profile;
                Rrf[profile] = 0;
                BestRank[profile] = int.MaxValue;
            }
            Rrf[profile] += 1 / (RrfK + rank);
            if (rank < BestRank[profile])
            {
                BestRank[profile] = rank;
                Route[profile] = route;
                MatchedAlias[profile] = alias;
            }
        }

        /// <summary>The <paramref name="take"/> best Profiles, best first: highest RRF, then name order of the matched alias.</summary>
        public List<int> Best(int take, int[] nameOrder)
        {
            if (take == 0) return [];
            // A bounded heap whose root is the worst kept Profile: O(n log take), not a sort of every fused Profile.
            var worstFirst = Comparer<int>.Create((x, y) => Rrf[x] != Rrf[y] ? Rrf[x].CompareTo(Rrf[y]) : nameOrder[MatchedAlias[y]].CompareTo(nameOrder[MatchedAlias[x]]));
            var kept = new PriorityQueue<int, int>(Math.Min(take, Count) + 1, worstFirst);
            foreach (var p in _present.AsSpan(0, Count))
            {
                if (kept.Count < take) kept.Enqueue(p, p);
                else if (worstFirst.Compare(p, kept.Peek()) > 0) kept.EnqueueDequeue(p, p);
            }
            var best = new List<int>(kept.Count);
            while (kept.Count > 0) best.Add(kept.Dequeue());
            best.Reverse();
            return best;
        }

        public void Return()
        {
            ArrayPool<double>.Shared.Return(Rrf);
            ArrayPool<int>.Shared.Return(BestRank);
            ArrayPool<int>.Shared.Return(Route);
            ArrayPool<int>.Shared.Return(MatchedAlias);
            ArrayPool<int>.Shared.Return(_present);
        }
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
    public static double NGramScore(string a, string b) => Dice(Grams(NameComparison.NameKey(a, true)), [.. Grams(NameComparison.NameKey(b, true))]);

    private double Bm25Score(string[] distinctQueryTokens, Alias a)
    {
        var n = _aliases.Length;
        var averageTokenCount = a.Cjk ? _averageTokenCountCjk : _averageTokenCountLatin;
        var score = 0.0;
        foreach (var t in distinctQueryTokens)
        {
            var tf = 0;
            foreach (var x in a.Tokens) if (x == t) tf++;
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

    // Both sides hold distinct grams, so counting one side's grams in the other is the size of the intersection.
    private static double Dice(HashSet<string> query, string[] alias) =>
        query.Count + alias.Length == 0 ? 0 : 2.0 * SharedCount(query, alias) / (query.Count + alias.Length);

    private static int SharedCount(HashSet<string> query, string[] alias)
    {
        var shared = 0;
        foreach (var x in alias) if (query.Contains(x)) shared++;
        return shared;
    }

    private static string[] Tokens(string name)
    {
        var words = name.Split([' ', '-', ',', '.', '·'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => NameComparison.NameKey(w, true)).Where(w => w.Length > 0).ToList();
        return words.SelectMany(w => IsCjk(w) ? CodePoints(w) : [w]).ToArray();
    }

    /// <summary>
    /// Code point n-grams of the key between start and end marks, plus single characters for a short Chinese name. Each gram
    /// is one slice of the marked key, cut at code point boundaries, so a gram costs one string and nothing else.
    /// </summary>
    private static HashSet<string> Grams(string key)
    {
        var grams = new HashSet<string>(StringComparer.Ordinal);
        if (key.Length == 0) return grams;
        var marked = StartMark + key + EndMark;
        Span<int> starts = marked.Length < MaxStackallocInts ? stackalloc int[marked.Length + 1] : new int[marked.Length + 1];
        var codePoints = 0;
        for (var i = 0; i < marked.Length; i += Rune.GetRuneAt(marked, i).Utf16SequenceLength) starts[codePoints++] = i;
        starts[codePoints] = marked.Length;

        var cjk = IsCjk(key);
        var n = cjk ? CjkGramLength : LatinGramLength;
        for (var i = 0; i + n <= codePoints; i++) grams.Add(marked[starts[i]..starts[i + n]]);
        var keyCodePoints = codePoints - 2;
        if (cjk && keyCodePoints <= MaxCjkNameLengthWithUnigrams)
            for (var i = 1; i <= keyCodePoints; i++) grams.Add(marked[starts[i]..starts[i + 1]]);
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
