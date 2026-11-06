namespace Aml.Core.Retrieval;

/// <summary>
/// Okapi BM25 over alias tokens, with document frequency from the token postings and a separate average length for
/// Chinese and Latin aliases (a Chinese name has one token per character). Immutable once built.
/// </summary>
internal sealed class Bm25Scorer
{
    // ponytail: k1 is the literature default; b 0.3 suits short name fields. Tune both from Recall@K on labelled data.
    private const double K1 = 1.2;
    private const double B = 0.3;

    private readonly TermPostings _tokens;
    private readonly int _documents;
    private readonly double _averageLengthLatin;
    private readonly double _averageLengthCjk;

    public Bm25Scorer(TermPostings tokens, int documents, double averageLengthLatin, double averageLengthCjk)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(documents);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(averageLengthLatin);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(averageLengthCjk);
        (_tokens, _documents, _averageLengthLatin, _averageLengthCjk) = (tokens, documents, averageLengthLatin, averageLengthCjk);
    }

    /// <summary>
    /// A scorer for one query: each distinct query token's inverse document frequency, worked out once, not once per alias.
    /// A token no alias holds is left out; it would score 0 on every alias.
    /// </summary>
    public Query For(IEnumerable<string> distinctQueryTokens)
    {
        var ids = _tokens.KnownIds(distinctQueryTokens);
        var idf = Array.ConvertAll(ids, id =>
        {
            var df = _tokens.DocumentFrequency(id);
            return Math.Log(1 + (_documents - df + 0.5) / (df + 0.5));
        });
        return new Query(this, ids, idf);
    }

    internal sealed class Query(Bm25Scorer scorer, int[] ids, double[] idf)
    {
        /// <summary>The numbers of the query tokens the index holds, in query order.</summary>
        public IReadOnlyList<int> TokenIds => ids;

        /// <param name="tokenIds">The alias's tokens as numbers, repeats kept: a repeat is term frequency.</param>
        public double Score(ReadOnlySpan<int> tokenIds, bool cjk)
        {
            var averageLength = cjk ? scorer._averageLengthCjk : scorer._averageLengthLatin;
            var score = 0.0;
            for (var q = 0; q < ids.Length; q++)
            {
                var tf = 0;
                foreach (var x in tokenIds) if (x == ids[q]) tf++;
                if (tf == 0) continue;
                score += idf[q] * tf * (K1 + 1) / (tf + K1 * (1 - B + B * tokenIds.Length / averageLength));
            }
            return score;
        }
    }
}
