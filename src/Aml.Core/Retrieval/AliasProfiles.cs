namespace Aml.Core.Retrieval;

/// <summary>
/// The index's fixed alias table: which Profile each alias position belongs to and the aliases' name order, and the ranking
/// of Profiles by their best-scoring alias built on it. Never changed after construction; shared by concurrent searches.
/// </summary>
internal sealed class AliasProfiles
{
    private readonly int[] _profileOf;
    private readonly int[] _nameOrder;

    /// <param name="profileOf">The Profile number (0 to <paramref name="profileCount"/> - 1) of each alias position; copied.</param>
    /// <param name="nameOrder">Each alias's rank by (name, Profile key), the ordinal tie-break of every ranking; copied.</param>
    public AliasProfiles(IReadOnlyList<int> profileOf, int profileCount, IReadOnlyList<int> nameOrder)
    {
        if (nameOrder.Count != profileOf.Count) throw new ArgumentException("One name order rank per alias is required.", nameof(nameOrder));
        _profileOf = [.. profileOf];
        _nameOrder = [.. nameOrder];
        ProfileCount = profileCount;
        if (_profileOf.Any(p => p < 0 || p >= profileCount)) throw new ArgumentOutOfRangeException(nameof(profileOf));
    }

    public int AliasCount => _profileOf.Length;

    public int ProfileCount { get; }

    public int ProfileOf(int alias) => _profileOf[alias];

    /// <summary>Whether alias <paramref name="x"/> comes before alias <paramref name="y"/> in name order.</summary>
    public bool NamedBefore(int x, int y) => _nameOrder[x] < _nameOrder[y];

    /// <summary>
    /// Of the first <paramref name="reachedCount"/> aliases in <paramref name="reached"/>, those scoring above 0 and at least
    /// <paramref name="floor"/>, one per Profile (its best), by score and then name order, written to the front of
    /// <paramref name="ranked"/>; returns how many. <paramref name="scores"/> is indexed like <paramref name="reached"/>.
    /// </summary>
    public int BestAliasPerProfile(PooledArray<int> reached, int reachedCount, PooledArray<double> scores, double floor, Span<int> ranked)
    {
        using var bestOf = new PooledArray<int>(ProfileCount);
        using var winners = new PooledArray<int>(ProfileCount);
        var best = bestOf.Span;
        var winner = winners.Span;
        var reachedSpan = reached.Span[..reachedCount];
        var scoreSpan = scores.Span[..reachedCount];
        best.Fill(-1);
        var count = 0;
        for (var i = 0; i < reachedSpan.Length; i++)
        {
            var score = scoreSpan[i];
            if (!(score > 0 && score >= floor)) continue;
            var p = _profileOf[reachedSpan[i]];
            if (best[p] < 0) winner[count++] = p;
            if (best[p] < 0 || Before(scoreSpan, reachedSpan, i, best[p])) best[p] = i;
        }
        var order = winner[..count];
        for (var w = 0; w < order.Length; w++) order[w] = best[order[w]];
        SortByScoreThenName(order, scoreSpan, reachedSpan);
        for (var w = 0; w < order.Length; w++) ranked[w] = reachedSpan[order[w]];
        return count;
    }

    private bool Before(ReadOnlySpan<double> scores, ReadOnlySpan<int> reached, int i, int j) =>
        scores[i] > scores[j] || (scores[i] == scores[j] && NamedBefore(reached[i], reached[j]));

    /// <summary>
    /// Orders positions of reached aliases by score, highest first, then name order: two primitive key sorts (by score,
    /// then by name order within each run of equal scores) instead of one sort through a comparer. Name order is distinct
    /// per alias, so the order is total and the same as <see cref="Before"/>.
    /// </summary>
    private void SortByScoreThenName(Span<int> order, ReadOnlySpan<double> scores, ReadOnlySpan<int> reached)
    {
        using var keyBuffer = new PooledArray<double>(order.Length);
        using var nameBuffer = new PooledArray<int>(order.Length);
        var byScore = keyBuffer.Span;
        for (var w = 0; w < order.Length; w++) byScore[w] = -scores[order[w]];
        byScore.Sort(order);
        var byName = nameBuffer.Span;
        for (var start = 0; start < order.Length;)
        {
            var end = start + 1;
            while (end < order.Length && byScore[end] == byScore[start]) end++;
            if (end - start > 1)
            {
                for (var w = start; w < end; w++) byName[w] = _nameOrder[reached[order[w]]];
                byName[start..end].Sort(order[start..end]);
            }
            start = end;
        }
    }
}
