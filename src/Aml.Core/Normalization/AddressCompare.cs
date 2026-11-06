using System.Text.RegularExpressions;

namespace Aml.Core.Normalization;

public static partial class AddressCompare
{
    // ponytail: placeholder threshold pending calibration (open question); it labels the detail only, never support.
    public const double JaccardThreshold = 0.5;

    public static double Jaccard(string a, string b)
    {
        var x = Tokens(a);
        var y = Tokens(b);
        return x.Count == 0 && y.Count == 0 ? 0 : (double)x.Intersect(y).Count() / x.Union(y).Count();
    }

    private static HashSet<string> Tokens(string text)
    {
        var tokens = new HashSet<string>();
        foreach (Match w in Word().Matches(CjkText.HalfWidth(text)))
        {
            var t = NameComparison.NameKey(w.Value);
            if (t.Length == 0) continue;
            if (t.Any(CjkText.IsCjk) && t.Length > 1) tokens.UnionWith(CharacterBigrams(t));
            else tokens.Add(t);
        }
        return tokens;
    }

    private static IEnumerable<string> CharacterBigrams(string t) => Enumerable.Range(0, t.Length - 1).Select(i => t.Substring(i, 2));

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}
