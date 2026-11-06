namespace Aml.Core.Retrieval;

/// <summary>Set overlap between a query's distinct terms and an alias's distinct terms, as term numbers.</summary>
internal static class TermOverlap
{
    /// <summary>Dice coefficient: twice the shared terms over both set sizes; 0 when both are empty.</summary>
    public static double Dice(int queryCount, int aliasCount, int shared) =>
        queryCount + aliasCount == 0 ? 0 : 2.0 * shared / (queryCount + aliasCount);

    /// <summary>
    /// How many of the alias's distinct terms the query holds, with the query as one flag per term number: one load per
    /// alias term, no hashing and no branchy merge.
    /// </summary>
    public static int SharedCount(ReadOnlySpan<bool> isQueryTerm, ReadOnlySpan<int> aliasTerms)
    {
        var shared = 0;
        foreach (var id in aliasTerms) if (isQueryTerm[id]) shared++;
        return shared;
    }
}
