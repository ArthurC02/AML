namespace Aml.Core.Retrieval;

/// <summary>
/// The index's fixed alias table: which Profile each alias position belongs to and the aliases' name order, and the ranking
/// of Profiles by their best-scoring alias built on it. Never changed after construction; shared by concurrent searches.
/// </summary>
/// <param name="profileOf">The Profile number of each alias position.</param>
/// <param name="nameOrder">Each alias's rank by (name, Profile key): the ordinal tie-break of every ranking, as one integer comparison.</param>
internal sealed class AliasProfiles(int[] profileOf, int profileCount, int[] nameOrder)
{
    public int ProfileCount => profileCount;

    public int ProfileOf(int alias) => profileOf[alias];

    /// <summary>Whether alias <paramref name="x"/> comes before alias <paramref name="y"/> in name order.</summary>
    public bool NamedBefore(int x, int y) => nameOrder[x] < nameOrder[y];

    /// <summary>
    /// Of the first <paramref name="reachedCount"/> aliases in <paramref name="reached"/>, those scoring above 0 and at least
    /// <paramref name="floor"/>, one per Profile (its best), by score and then name order, written to the front of
    /// <paramref name="ranked"/>; returns how many. <paramref name="scores"/> is indexed like <paramref name="reached"/>.
    /// </summary>
    public int BestAliasPerProfile(int[] reached, int reachedCount, double[] scores, double floor, int[] ranked)
    {
        using var bestOf = new PooledArray<int>(profileCount);
        using var winners = new PooledArray<int>(profileCount);
        bestOf.Span.Fill(-1);
        var best = bestOf.Array;
        var count = 0;
        for (var i = 0; i < reachedCount; i++)
        {
            if (!(scores[i] > 0 && scores[i] >= floor)) continue;
            var p = profileOf[reached[i]];
            if (best[p] < 0) winners.Array[count++] = p;
            if (best[p] < 0 || Before(scores, reached, i, best[p])) best[p] = i;
        }
        var order = winners.Array.AsSpan(0, count);
        for (var w = 0; w < order.Length; w++) order[w] = best[order[w]];
        order.Sort((i, j) => Before(scores, reached, i, j) ? -1 : Before(scores, reached, j, i) ? 1 : 0);
        for (var w = 0; w < order.Length; w++) ranked[w] = reached[order[w]];
        return count;
    }

    private bool Before(double[] scores, int[] reached, int i, int j) =>
        scores[i] > scores[j] || (scores[i] == scores[j] && NamedBefore(reached[i], reached[j]));
}
