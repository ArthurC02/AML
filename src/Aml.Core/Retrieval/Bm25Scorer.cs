using System.Collections.Frozen;

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

    private readonly FrozenDictionary<string, int[]> _postingsByToken;
    private readonly int _documents;
    private readonly double _averageLengthLatin;
    private readonly double _averageLengthCjk;

    /// <param name="postingsByToken">The aliases holding each token, each alias once; read, never changed.</param>
    public Bm25Scorer(FrozenDictionary<string, int[]> postingsByToken, int documents, double averageLengthLatin, double averageLengthCjk)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(documents);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(averageLengthLatin);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(averageLengthCjk);
        (_postingsByToken, _documents, _averageLengthLatin, _averageLengthCjk) = (postingsByToken, documents, averageLengthLatin, averageLengthCjk);
    }

    public double Score(string[] distinctQueryTokens, string[] tokens, bool cjk)
    {
        var averageLength = cjk ? _averageLengthCjk : _averageLengthLatin;
        var score = 0.0;
        foreach (var t in distinctQueryTokens)
        {
            var tf = 0;
            foreach (var x in tokens) if (x == t) tf++;
            if (tf == 0) continue;
            var df = _postingsByToken.TryGetValue(t, out var posted) ? posted.Length : 0;
            var idf = Math.Log(1 + (_documents - df + 0.5) / (df + 0.5));
            score += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * tokens.Length / averageLength));
        }
        return score;
    }
}
