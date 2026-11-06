using System.Text.RegularExpressions;

namespace Aml.Core.Normalization;

public static partial class AddressCompare
{
    /// <summary>
    /// city + district + road + section + lane + alley + number + floor, from a normalized Taiwan address;
    /// null when the text does not parse down to the house number. Village and neighborhood are left out.
    /// </summary>
    public static string? TaiwanAddressKey(string address)
    {
        var m = TaiwanAddress().Match(Normalize.TaiwanAddress(address).Normalized.Replace('台', '臺'));
        if (!m.Success) return null;
        return string.Concat(KeyParts.Select(g => m.Groups[g].Value + "|"));
    }

    private static readonly string[] KeyParts = ["city", "district", "road", "section", "lane", "alley", "number", "floor"];

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
        foreach (Match w in Word().Matches(Normalize.HalfWidth(text)))
        {
            var t = Normalize.NameKey(w.Value);
            if (t.Length == 0) continue;
            if (t.Any(Normalize.IsCjk) && t.Length > 1) tokens.UnionWith(CharacterBigrams(t));
            else tokens.Add(t);
        }
        return tokens;
    }

    private static IEnumerable<string> CharacterBigrams(string t) => Enumerable.Range(0, t.Length - 1).Select(i => t.Substring(i, 2));

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"^\d{0,6}(?<city>\p{IsCJKUnifiedIdeographs}{2}[市縣])(?<district>\p{IsCJKUnifiedIdeographs}{1,3}?[區鄉鎮市])?"
        + @"(?:\p{IsCJKUnifiedIdeographs}{1,3}?[里村])?(?:\d+鄰)?(?<road>\p{IsCJKUnifiedIdeographs}+?(?:路|街|大道))"
        + @"(?<section>[\d一二三四五六七八九十]+段)?(?<lane>\d+巷)?(?<alley>\d+弄)?(?<number>\d+(?:之\d+)?號)(?<floor>\d+樓(?:之\d+)?)?")]
    private static partial Regex TaiwanAddress();
}
