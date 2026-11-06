using System.Globalization;

namespace Aml.Core.Normalization;

public static class ChineseNumerals
{
    /// <summary>
    /// Arabic or Chinese numerals: 四十四, 廿二, 卅一, 一百零二, 十. Null for anything that is not one number,
    /// so approximations such as 七八十 are rejected.
    /// </summary>
    public static int? Parse(string s)
    {
        if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return n;
        s = s.Replace("廿", "二十", StringComparison.Ordinal).Replace("卅", "三十", StringComparison.Ordinal);
        var total = 0;
        var hundred = s.IndexOf('百', StringComparison.Ordinal);
        if (hundred >= 0)
        {
            if (hundred > 1 || (hundred == 1 && Digit(s[0]) is null)) return null;
            total = 100 * (hundred == 0 ? 1 : Digit(s[0])!.Value);
            s = s[(hundred + 1)..].TrimStart('零', '〇');
            if (s.Length == 0) return total;
        }
        var ten = s.IndexOf('十', StringComparison.Ordinal);
        if (ten < 0) return s.Length == 1 && Digit(s[0]) is { } d ? total + d : null;
        if (ten > 1 || s.Length - ten > 2) return null;
        int? tens = ten == 0 ? 1 : Digit(s[0]);
        int? ones = ten == s.Length - 1 ? 0 : Digit(s[^1]);
        return tens is null || ones is null ? null : total + tens.Value * 10 + ones.Value;
    }

    private static int? Digit(char c) => "〇一二三四五六七八九".IndexOf(c, StringComparison.Ordinal) switch
    {
        >= 0 and var i => i,
        _ => c switch { '零' => 0, '兩' => 2, _ => null },
    };
}
