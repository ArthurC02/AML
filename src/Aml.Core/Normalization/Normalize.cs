using System.Text;
using System.Text.RegularExpressions;
using Aml.Core.Shared;

namespace Aml.Core.Normalization;

public static partial class Normalize
{
    /// <summary>Comparison key: diacritics folded, case folded, spaces and punctuation removed. CJK kept as is.</summary>
    public static string NameKey(string name)
    {
        var d = name.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
        return sb.ToString();
    }

    // ponytail: only the countries seen in samples; extend from an ISO table when more appear.
    public static string Country(string raw) => raw.Trim() switch
    {
        "Taiwan" or "台灣" or "臺灣" or "中華民國" => "TW",
        "Viet Nam" or "Vietnam" or "越南" => "VN",
        var other => other.ToUpperInvariant(),
    };

    public static Gender? Gender(string? raw) => raw?.Trim() switch
    {
        "男" or "Male" => Shared.Gender.Male,
        "女" or "Female" => Shared.Gender.Female,
        _ => null,
    };

    public static string HalfWidth(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(c is >= '０' and <= '９' ? (char)(c - '０' + '0') : c);
        return sb.ToString();
    }

    /// <summary>Full-width digits to ASCII; pre-2010 台北縣 cities/townships to 新北市 districts.</summary>
    public static (string Normalized, string? Finding) TaiwanAddress(string raw)
    {
        var s = HalfWidth(raw.Trim());
        var m = OldTaipeiCounty().Match(s);
        if (!m.Success) return (s, null);
        return (s[..m.Index] + "新北市" + m.Groups[1].Value + "區" + s[(m.Index + m.Length)..], Findings.HistoricalAdminDivision);
    }

    public static bool IsPostalCodeOnly(string raw) => PostalCode().IsMatch(HalfWidth(raw.Trim()));

    [GeneratedRegex("[台臺]北縣(\\p{IsCJKUnifiedIdeographs}{1,3}?)[市鄉鎮]")]
    private static partial Regex OldTaipeiCounty();

    [GeneratedRegex("^\\d{3,6}$")]
    private static partial Regex PostalCode();

    public static string? NullIfPlaceholder(string? s) =>
        string.IsNullOrWhiteSpace(s) || s.Trim() == "-" ? null : s.Trim();
}
