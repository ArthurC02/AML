using System.Text;
using Aml.Core.Normalization;

namespace Aml.Tests.Unit.Names;

/// <summary>
/// The bit-parallel similarity must equal the textbook algorithms exactly, bit for bit, on short and long strings alike.
/// The textbook versions live here as the oracle.
/// </summary>
public class SimilarityTests
{
    private static int[] CodePoints(string s) => s.EnumerateRunes().Select(r => r.Value).ToArray();

    private static int TextbookLevenshtein(string x, string y)
    {
        var (a, b) = (CodePoints(x), CodePoints(y));
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var cur = new int[b.Length + 1];
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1], prev[j]) + 1, prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            prev = cur;
        }
        return prev[b.Length];
    }

    private static double TextbookJaroWinkler(string x, string y)
    {
        if (x == y) return 1;
        var (a, b) = (CodePoints(x), CodePoints(y));
        if (a.Length == 0 || b.Length == 0) return 0;
        var window = Math.Max(0, Math.Max(a.Length, b.Length) / 2 - 1);
        var ma = new bool[a.Length];
        var mb = new bool[b.Length];
        var matches = 0;
        for (var i = 0; i < a.Length; i++)
            for (var j = Math.Max(0, i - window); j < Math.Min(b.Length, i + window + 1); j++)
                if (!mb[j] && a[i] == b[j]) { ma[i] = mb[j] = true; matches++; break; }
        if (matches == 0) return 0;
        var transpositions = 0;
        for (int i = 0, k = 0; i < a.Length; i++)
        {
            if (!ma[i]) continue;
            while (!mb[k]) k++;
            if (a[i] != b[k++]) transpositions++;
        }
        double m = matches;
        var jaro = (m / a.Length + m / b.Length + (m - transpositions / 2.0) / m) / 3;
        if (jaro <= Similarity.JaroWinklerBoostThreshold) return jaro;
        var prefix = 0;
        while (prefix < Math.Min(4, Math.Min(a.Length, b.Length)) && a[prefix] == b[prefix]) prefix++;
        return jaro + prefix * 0.1 * (1 - jaro);
    }

    // Small alphabets make matches, repeats and transpositions common; the CJK extension B characters are surrogate pairs.
    private static readonly string[][] Alphabets = [["A", "B", "C"], ["陳", "志", "明", "𥕢"], ["A", "B", "陳", "𥕢", "?"]];

    private static string RandomString(Random random, string[] alphabet, int codePoints)
    {
        var s = new StringBuilder();
        for (var i = 0; i < codePoints; i++) s.Append(alphabet[random.Next(alphabet.Length)]);
        return s.ToString();
    }

    [Fact]
    public void Bit_parallel_levenshtein_and_jaro_winkler_equal_the_textbook_algorithms()
    {
        var random = new Random(20261106);
        for (var n = 0; n < 4000; n++)
        {
            var alphabet = Alphabets[n % Alphabets.Length];
            var x = RandomString(random, alphabet, random.Next(0, 81));
            var y = random.Next(4) == 0 ? x : RandomString(random, alphabet, random.Next(0, 81));
            Assert.Equal(TextbookLevenshtein(x, y), Similarity.Levenshtein(x, y));
            Assert.Equal(TextbookJaroWinkler(x, y), Similarity.JaroWinkler(x, y));
        }
    }

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("", "ABC", 3)]
    [InlineData("ABC", "", 3)]
    [InlineData("KITTEN", "SITTING", 3)]
    [InlineData("陳志明", "陳智明", 1)]
    public void Levenshtein_counts_edits_per_code_point(string x, string y, int distance) =>
        Assert.Equal(distance, Similarity.Levenshtein(x, y));

    [Fact]
    public void Sixty_four_code_points_run_bit_parallel_and_sixty_five_fall_back_with_the_same_results()
    {
        var a64 = new string('A', 63) + "B";
        var b64 = "B" + new string('A', 63);
        Assert.Equal(TextbookLevenshtein(a64, b64), Similarity.Levenshtein(a64, b64));
        Assert.Equal(TextbookJaroWinkler(a64, b64), Similarity.JaroWinkler(a64, b64));
        Assert.Equal(TextbookLevenshtein(a64 + "C", b64), Similarity.Levenshtein(a64 + "C", b64));
        Assert.Equal(TextbookJaroWinkler(a64 + "C", b64), Similarity.JaroWinkler(a64 + "C", b64));
    }
}
