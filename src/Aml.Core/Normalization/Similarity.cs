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

    /// <summary>Jaro-Winkler: the common-prefix bonus (up to 4, scale 0.1) applies only when Jaro exceeds <see cref="BoostThreshold"/>.</summary>
    public static double JaroWinkler(string x, string y)
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
        if (jaro <= BoostThreshold) return jaro;
        var prefix = 0;
        while (prefix < Math.Min(4, Math.Min(a.Length, b.Length)) && a[prefix] == b[prefix]) prefix++;
        return jaro + prefix * 0.1 * (1 - jaro);
    }

    /// <summary>Winkler's boost threshold.</summary>
    public const double BoostThreshold = 0.7;

    private static int[] CodePoints(string s) => s.EnumerateRunes().Select(r => r.Value).ToArray();
}
