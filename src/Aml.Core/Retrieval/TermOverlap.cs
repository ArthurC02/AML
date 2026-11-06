namespace Aml.Core.Retrieval;

/// <summary>Set overlap between a query's distinct terms and an alias's distinct terms.</summary>
internal static class TermOverlap
{
    /// <summary>Dice coefficient: twice the shared terms over both sizes; 0 when both are empty.</summary>
    public static double Dice(HashSet<string> query, string[] alias) =>
        query.Count + alias.Length == 0 ? 0 : 2.0 * SharedCount(query, alias) / (query.Count + alias.Length);

    // Both sides hold distinct terms, so counting the alias's terms found in the query is the size of the intersection.
    public static int SharedCount(HashSet<string> query, string[] alias)
    {
        var shared = 0;
        foreach (var x in alias) if (query.Contains(x)) shared++;
        return shared;
    }
}
