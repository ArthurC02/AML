namespace Aml.Core.Normalization;

/// <summary>String similarity on comparison keys, per code point; a PARTIAL feature inside the NAME group only.</summary>
public static class Similarity
{
    public static int Levenshtein(string x, string y)
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

    public static double JaroWinkler(string x, string y)
    {
        if (x == y) return 1;
        var (a, b) = (CodePoints(x), CodePoints(y));
        if (a.Length == 0 || b.Length == 0) return 0;
        var jaro = Jaro(a, b);
        if (jaro <= BoostThreshold) return jaro;
        return jaro + CommonPrefixLength(a, b) * PrefixScale * (1 - jaro);
    }

    public const double BoostThreshold = 0.7;
    private const int MaxPrefixLength = 4;
    private const double PrefixScale = 0.1;

    private static int CommonPrefixLength(int[] a, int[] b)
    {
        var prefix = 0;
        while (prefix < Math.Min(MaxPrefixLength, Math.Min(a.Length, b.Length)) && a[prefix] == b[prefix]) prefix++;
        return prefix;
    }

    private static double Jaro(int[] a, int[] b)
    {
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
        return (m / a.Length + m / b.Length + (m - transpositions / 2.0) / m) / 3;
    }

    private static int[] CodePoints(string s) => s.EnumerateRunes().Select(r => r.Value).ToArray();
}
