namespace Aml.Core.Retrieval;

/// <summary>One fused Profile: the alias and route of its best rank, and its fused score.</summary>
internal readonly record struct FusedProfile(int Alias, int Route, double Score);

/// <summary>
/// Reciprocal rank fusion of several Profile rankings for one search: a Profile scores the sum of 1 / (k + rank) over the
/// routes that ranked it, and is named by the route and alias of its best rank (the earlier route on a tie, so add routes in
/// order). State lives in pooled arrays sized by the Profile count and is only read back through <see cref="Best"/>;
/// dispose to return the arrays.
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

    /// <summary>Adds the ranking of one route: alias positions best first, one alias per Profile.</summary>
    public void AddRanking(ReadOnlySpan<int> aliases, int route)
    {
        for (var i = 0; i < aliases.Length; i++) Add(aliases[i], rank: i + 1, route);
    }

    private void Add(int alias, int rank, int route)
    {
        var p = _aliasProfiles.ProfileOf(alias);
        if (_bestRank[p] == 0)
        {
            _present[Count++] = p;
            _score[p] = 0;
            _bestRank[p] = int.MaxValue;
        }
        _score[p] += 1 / (K + rank);
        if (rank < _bestRank[p])
        {
            _bestRank[p] = rank;
            _route[p] = route;
            _matchedAlias[p] = alias;
        }
    }

    /// <summary>The <paramref name="take"/> best Profiles, best first: highest fused score, then name order of the matched alias.</summary>
    public List<FusedProfile> Best(int take)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(take);
        if (take == 0) return [];
        // A bounded heap whose root is the worst kept Profile: O(n log take), not a sort of every fused Profile.
        var worstFirst = Comparer<int>.Create((x, y) => _score[x] != _score[y] ? _score[x].CompareTo(_score[y])
            : _aliasProfiles.NamedBefore(_matchedAlias[x], _matchedAlias[y]) ? 1
            : _aliasProfiles.NamedBefore(_matchedAlias[y], _matchedAlias[x]) ? -1 : 0);
        var kept = new PriorityQueue<int, int>(Math.Min(take, Count) + 1, worstFirst);
        foreach (var p in _present.Span[..Count])
        {
            if (kept.Count < take) kept.Enqueue(p, p);
            else if (worstFirst.Compare(p, kept.Peek()) > 0) kept.EnqueueDequeue(p, p);
        }
        var best = new List<FusedProfile>(kept.Count);
        while (kept.Count > 0)
        {
            var p = kept.Dequeue();
            best.Add(new FusedProfile(_matchedAlias[p], _route[p], _score[p]));
        }
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
