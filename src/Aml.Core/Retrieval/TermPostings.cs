using System.Collections.Frozen;

namespace Aml.Core.Retrieval;

/// <summary>
/// An inverted index over one kind of term (words, n-grams or reading keys): each distinct term gets a number, and each
/// number lists the aliases holding that term. Scoring then compares numbers, not strings, so no term is hashed per alias.
/// Immutable once built; shared by concurrent searches.
/// </summary>
internal sealed class TermPostings
{
    private readonly FrozenDictionary<string, int> _ids;
    private readonly int[][] _aliasesWith;

    /// <param name="termsPerAlias">Each alias's terms, by alias position; a term an alias holds twice is posted once.</param>
    /// <param name="idsPerAlias">Each alias's terms as numbers, in the order given, repeats kept.</param>
    public TermPostings(IReadOnlyList<string[]> termsPerAlias, out int[][] idsPerAlias)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var aliasesWith = new List<List<int>>();
        idsPerAlias = new int[termsPerAlias.Count][];
        for (var alias = 0; alias < termsPerAlias.Count; alias++)
        {
            var terms = termsPerAlias[alias];
            var aliasIds = idsPerAlias[alias] = new int[terms.Length];
            for (var t = 0; t < terms.Length; t++)
            {
                if (!ids.TryGetValue(terms[t], out var id))
                {
                    ids[terms[t]] = id = ids.Count;
                    aliasesWith.Add([]);
                }
                aliasIds[t] = id;
                var posted = aliasesWith[id];
                if (posted.Count == 0 || posted[^1] != alias) posted.Add(alias);
            }
        }
        _ids = ids.ToFrozenDictionary(StringComparer.Ordinal);
        _aliasesWith = [.. aliasesWith.Select(a => a.ToArray())];
    }

    /// <summary>How many distinct terms the index holds; term numbers run from 0 to one less.</summary>
    public int TermCount => _aliasesWith.Length;

    /// <summary>The numbers of the query terms the index holds, in the order given; a term no alias holds is left out.</summary>
    public int[] KnownIds(IEnumerable<string> terms) => [.. terms.Select(t => _ids.GetValueOrDefault(t, -1)).Where(id => id >= 0)];

    /// <summary>The aliases holding the term, ascending, each once.</summary>
    public ReadOnlySpan<int> AliasesWith(int id) => _aliasesWith[id];

    /// <summary>How many aliases hold the term.</summary>
    public int DocumentFrequency(int id) => _aliasesWith[id].Length;
}
