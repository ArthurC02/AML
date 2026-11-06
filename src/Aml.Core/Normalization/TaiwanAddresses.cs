using System.Globalization;
using System.Text.RegularExpressions;
using Aml.Core.Shared;

namespace Aml.Core.Normalization;

public static partial class TaiwanAddresses
{
    /// <summary>
    /// Full-width digits to ASCII; Chinese numerals before 段/巷/弄/號/樓/鄰 to digits (三段 → 3段); county and township
    /// names merged in 2010 and 2014 to the current city and district (<see cref="AdminDivisionTable"/>).
    /// </summary>
    public static (string Normalized, string? Finding) Normalize(string raw)
    {
        var s = WithArabicAddressNumerals(CjkText.HalfWidth(raw.Trim()));
        var m = OldCounty().Match(s);
        if (!m.Success) return (s, null);
        return (s[..m.Index] + CurrentCityAndDistrict(m) + s[(m.Index + m.Length)..], Findings.HistoricalAdminDivision);
    }

    private static string WithArabicAddressNumerals(string s) =>
        AddressNumeral().Replace(s, m => ChineseNumerals.Parse(m.Value)?.ToString(CultureInfo.InvariantCulture) ?? m.Value);

    private static string CurrentCityAndDistrict(Match oldCounty) =>
        MergedCounties[oldCounty.Groups["county"].Value.Replace('台', '臺')] + oldCounty.Groups["town"].Value + "區";

    /// <summary>Version of the administrative-division mapping, recorded with HISTORICAL_ADMIN_DIVISION.</summary>
    public const string AdminDivisionTable = "tw-admin-2014-12-25";

    // Counties merged into special municipalities: 2010-12-25 (新北, 臺中, 臺南, 高雄) and 2014-12-25 (桃園).
    private static readonly Dictionary<string, string> MergedCounties = new()
    {
        ["臺北縣"] = "新北市",
        ["臺中縣"] = "臺中市",
        ["臺南縣"] = "臺南市",
        ["高雄縣"] = "高雄市",
        ["桃園縣"] = "桃園市",
    };

    // The town is the shortest run before 市, 鄉 or 鎮, except the three townships whose own name holds one of them:
    // 桃園縣平鎮市, 臺南縣新市鄉 and 臺南縣左鎮鄉 (平鎮區, 新市區, 左鎮區), tried first.
    [GeneratedRegex("(?<county>[台臺]北縣|[台臺]中縣|[台臺]南縣|高雄縣|桃園縣)(?<town>平鎮|新市|左鎮|\\p{IsCJKUnifiedIdeographs}{1,3}?)[市鄉鎮]")]
    private static partial Regex OldCounty();

    [GeneratedRegex("[〇零一二兩三四五六七八九十廿卅百]+(?=[段巷弄號樓鄰])")]
    private static partial Regex AddressNumeral();

    public static bool IsPostalCodeOnly(string raw) => PostalCode().IsMatch(CjkText.HalfWidth(raw.Trim()));

    [GeneratedRegex("^\\d{3,6}$")]
    private static partial Regex PostalCode();

    /// <summary>
    /// city + district + road + section + lane + alley + number + floor, from a normalized Taiwan address;
    /// null when the text does not parse down to the house number. Village and neighborhood are left out.
    /// </summary>
    public static string? Key(string address)
    {
        var m = KeyPattern().Match(Normalize(address).Normalized.Replace('台', '臺'));
        if (!m.Success) return null;
        return string.Concat(KeyParts.Select(g => m.Groups[g].Value + "|"));
    }

    private static readonly string[] KeyParts = ["city", "district", "road", "section", "lane", "alley", "number", "floor"];

    [GeneratedRegex(@"^\d{0,6}(?<city>\p{IsCJKUnifiedIdeographs}{2}[市縣])(?<district>(?:平鎮|新市|左鎮)區|\p{IsCJKUnifiedIdeographs}{1,3}?[區鄉鎮市])?"
        + @"(?:\p{IsCJKUnifiedIdeographs}{1,3}?[里村])?(?:\d+鄰)?(?<road>\p{IsCJKUnifiedIdeographs}+?(?:路|街|大道))"
        + @"(?<section>[\d一二三四五六七八九十]+段)?(?<lane>\d+巷)?(?<alley>\d+弄)?(?<number>\d+(?:之\d+)?號)(?<floor>\d+樓(?:之\d+)?)?")]
    private static partial Regex KeyPattern();
}
