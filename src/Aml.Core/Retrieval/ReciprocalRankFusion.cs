namespace Aml.Core.Retrieval;

/// <summary>
/// Reciprocal rank fusion of several Profile rankings for one search: a Profile scores the sum of 1 / (k + rank) over the
/// routes that ranked it, and is named by the route and alias of its best rank (the earlier route on a tie, so add routes in
/// order). State lives in pooled arrays sized by the Profile count; dispose to return them.
/// </summary>
internal sealed class ReciprocalRankFusion : IDisposable
{
    // ponytail: the literature default; tune from Recall@K on labelled data.
    private const double K = 60;

    private readonly AliasProfiles _aliasProfiles;
    private readonly PooledArray<double> _score;
    private readonly PooledArray<int> _bestRank;
    private readonly PooledArray<int> _route;
    private readonly PooledArray<int> _matchedAlias;
    private readonly PooledArray<int> _present;

    /// <summary>How many Profiles any route ranked.</summary>
    public int Count { get; private set; }

    public ReciprocalRankFusion(AliasProfiles aliasProfiles)
    {
        _aliasProfiles = aliasProfiles;
        var profiles = aliasProfiles.ProfileCount;
        // 0 marks a Profile no route has ranked yet.
        _bestRank = new PooledArray<int>(profiles, clear: true);
        _score = new PooledArray<double>(profiles);
        _route = new PooledArray<int>(profiles);
        _matchedAlias = new PooledArray<int>(profiles);
        _present = new PooledArray<int>(profiles);
    }

    /// <summary>Adds the ranking of one route: <paramref name="aliases"/> best first, one alias per Profile.</summary>
    public void AddRanking(ReadOnlySpan<int> aliases, int route)
    {
        for (var i = 0; i < aliases.Length; i++) Add(aliases[i], rank: i + 1, route);
    }

    private void Add(int alias, int rank, int route)
    {
        var p = _aliasProfiles.ProfileOf(alias);
        var bestRank = _bestRank.Array;
        if (bestRank[p] == 0)
        {
            _present.Array[Count++] = p;
            _score.Array[p] = 0;
            bestRank[p] = int.MaxValue;
        }
        _score.Array[p] += 1 / (K + rank);
        if (rank < bestRank[p])
        {
            bestRank[p] = rank;
            _route.Array[p] = route;
            _matchedAlias.Array[p] = alias;
        }
    }

    public double Score(int profile) => _score.Array[profile];

    public int Route(int profile) => _route.Array[profile];

    public int MatchedAlias(int profile) => _matchedAlias.Array[profile];

    /// <summary>The <paramref name="take"/> best Profiles, best first: highest fused score, then name order of the matched alias.</summary>
    public List<int> Best(int take)
    {
        if (take == 0) return [];
        var score = _score.Array;
        var alias = _matchedAlias.Array;
        // A bounded heap whose root is the worst kept Profile: O(n log take), not a sort of every fused Profile.
        var worstFirst = Comparer<int>.Create((x, y) => score[x] != score[y] ? score[x].CompareTo(score[y])
            : _aliasProfiles.NamedBefore(alias[x], alias[y]) ? 1 : _aliasProfiles.NamedBefore(alias[y], alias[x]) ? -1 : 0);
        var kept = new PriorityQueue<int, int>(Math.Min(take, Count) + 1, worstFirst);
        foreach (var p in _present.Array.AsSpan(0, Count))
        {
            if (kept.Count < take) kept.Enqueue(p, p);
            else if (worstFirst.Compare(p, kept.Peek()) > 0) kept.EnqueueDequeue(p, p);
        }
        var best = new List<int>(kept.Count);
        while (kept.Count > 0) best.Add(kept.Dequeue());
        best.Reverse();
        return best;
    }

    public void Dispose()
    {
        _score.Dispose();
        _bestRank.Dispose();
        _route.Dispose();
        _matchedAlias.Dispose();
        _present.Dispose();
    }
}
