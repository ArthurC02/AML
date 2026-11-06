using System.Numerics;

namespace Aml.Core.Retrieval;

/// <summary>Set overlap between a query's distinct terms and an alias's distinct terms, as term numbers.</summary>
internal static class TermOverlap
{
    /// <summary>Dice coefficient: twice the shared terms over both set sizes; 0 when both are empty.</summary>
    public static double Dice(int queryCount, int aliasCount, int shared) =>
        queryCount + aliasCount == 0 ? 0 : 2.0 * shared / (queryCount + aliasCount);

    /// <summary>
    /// A 64-bit signature of a set of term numbers: bit (number mod 64) per term. Two sets' signatures bound their overlap
    /// without comparing terms (<see cref="MaxSharedCount"/>).
    /// </summary>
    public static ulong Signature(ReadOnlySpan<int> terms)
    {
        var signature = 0UL;
        foreach (var id in terms) signature |= 1UL << (id & 63);
        return signature;
    }

    /// <summary>
    /// An upper bound on the shared terms of two sets, from their sizes and signatures: the bitmap filter of Sandes et al.
    /// (2017), "Bitmap Filter: Speeding up Exact Set Similarity Joins with Bitwise Operations", with the length filter of
    /// Bayardo et al. (2007). A bit set in one signature only stands for at least one term in that set alone, so the
    /// popcount of the XOR is at most the symmetric difference, size(query) + size(alias) - 2 * shared. A query term no
    /// alias holds has no number and no bit; it counts in <paramref name="queryCount"/>, which only loosens the bound.
    /// </summary>
    public static int MaxSharedCount(int queryCount, ulong querySignature, int aliasCount, ulong aliasSignature) =>
        Math.Min(Math.Min(queryCount, aliasCount), (queryCount + aliasCount - BitOperations.PopCount(querySignature ^ aliasSignature)) / 2);

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
