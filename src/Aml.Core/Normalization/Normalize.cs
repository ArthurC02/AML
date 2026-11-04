using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Aml.Core.Shared;

namespace Aml.Core.Normalization;

public static partial class Normalize
{
    /// <summary>
    /// Comparison key: NFKC, diacritics folded, case folded, spaces and punctuation removed.
    /// With <paramref name="foldVariants"/>, controlled variant characters fold to one form (鍾/鐘/钟 → 鐘, simplified → traditional).
    /// </summary>
    public static string NameKey(string name, bool foldVariants = false)
    {
        var d = name.Normalize(NormalizationForm.FormKC).Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (char.IsLetterOrDigit(c))
                sb.Append(foldVariants && CjkVariants.TryGetValue(c, out var v) ? v : char.ToUpperInvariant(c));
        return sb.ToString();
    }

    /// <summary>Keys for a name in source order and with the surname moved to either end (given-first vs surname-first).</summary>
    public static IEnumerable<string> NameKeys(string name, bool foldVariants = false)
    {
        yield return NameKey(name, foldVariants);
        var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) yield break;
        yield return NameKey(string.Join(' ', tokens[1..]) + " " + tokens[0], foldVariants);
        yield return NameKey(tokens[^1] + " " + string.Join(' ', tokens[..^1]), foldVariants);
    }

    /// <summary>
    /// EXACT on source characters, EXACT with VARIANT_CHARACTERS after controlled folding. Otherwise PARTIAL:
    /// ROMANIZATION_VARIANT when the spellings differ only by romanization system, else with the best similarity disclosed.
    /// Similarity only grades PARTIAL; it is never support and has no threshold.
    /// </summary>
    public static (ComparisonStatus Status, string? Detail) CompareNames(IEnumerable<string> subject, IEnumerable<string> profile)
    {
        var s = subject.ToList();
        var p = profile.ToList();
        if (s.Count == 0) return (ComparisonStatus.SubjectMissing, null);
        if (p.Count == 0) return (ComparisonStatus.ProfileMissing, null);
        bool Match(bool fold) => s.SelectMany(n => NameKeys(n, fold)).Intersect(p.SelectMany(n => NameKeys(n, fold))).Any();
        if (Match(false)) return (ComparisonStatus.Exact, null);
        if (Match(true)) return (ComparisonStatus.Exact, Findings.VariantCharacters);
        if (s.Any(a => p.Any(b => Romanization.SameReading(a, b)))) return (ComparisonStatus.Partial, Findings.RomanizationVariant);

        var pairs = s.SelectMany(a => NameKeys(a, true)).SelectMany(_ => p.SelectMany(b => NameKeys(b, true)), (a, b) => (a, b)).ToList();
        var jw = pairs.Max(x => Similarity.JaroWinkler(x.a, x.b));
        var lev = pairs.Min(x => Similarity.Levenshtein(x.a, x.b));
        return (ComparisonStatus.Partial, string.Create(CultureInfo.InvariantCulture,
            $"NOT_IDENTICAL_AFTER_NORMALIZATION（Jaro-Winkler {jw:0.00}，Levenshtein {lev}，{(jw >= NameSimilarityThreshold ? "SIMILAR" : "DISSIMILAR")}）"));
    }

    // ponytail: placeholder threshold pending calibration from labelled cases (open question); it only labels the PARTIAL
    // detail and never makes the name support, a conflict or a hit.
    public const double NameSimilarityThreshold = 0.90;

    // ponytail: controlled list of same-reading variants and simplified forms seen in name data; it needs business
    // approval and grows from reviewed cases. Characters that are distinct surnames (涂/塗, 云/雲, 余/餘) are never folded.
    private static readonly Dictionary<char, char> CjkVariants = new()
    {
        ['鍾'] = '鐘', ['钟'] = '鐘', ['声'] = '聲', ['陈'] = '陳', ['张'] = '張', ['刘'] = '劉', ['杨'] = '楊',
        ['黄'] = '黃', ['吴'] = '吳', ['赵'] = '趙', ['郑'] = '鄭', ['许'] = '許', ['邓'] = '鄧', ['冯'] = '馮',
        ['韩'] = '韓', ['萧'] = '蕭', ['叶'] = '葉', ['苏'] = '蘇', ['卢'] = '盧', ['罗'] = '羅', ['谢'] = '謝',
        ['钱'] = '錢', ['孙'] = '孫', ['马'] = '馬', ['龙'] = '龍', ['东'] = '東', ['国'] = '國', ['华'] = '華',
        ['伟'] = '偉', ['丽'] = '麗', ['庄'] = '莊', ['荣'] = '榮', ['宁'] = '寧', ['台'] = '臺', ['阳'] = '陽',
        ['贵'] = '貴', ['宝'] = '寶', ['凤'] = '鳳', ['兰'] = '蘭', ['颖'] = '穎',
    };

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

    /// <summary>
    /// Full-width digits to ASCII; Chinese numerals before 段/巷/弄/號/樓/鄰 to digits (三段 → 3段); county and township
    /// names merged in 2010 and 2014 to the current city and district (<see cref="AdminDivisionTable"/>).
    /// </summary>
    public static (string Normalized, string? Finding) TaiwanAddress(string raw)
    {
        var s = AddressNumeral().Replace(HalfWidth(raw.Trim()), m => ChineseNumber(m.Value)?.ToString(CultureInfo.InvariantCulture) ?? m.Value);
        var m = OldCounty().Match(s);
        if (!m.Success) return (s, null);
        return (s[..m.Index] + MergedCounties[m.Groups["county"].Value.Replace('台', '臺')] + m.Groups["town"].Value + "區" + s[(m.Index + m.Length)..],
            Findings.HistoricalAdminDivision);
    }

    /// <summary>Version of the administrative-division mapping, recorded with HISTORICAL_ADMIN_DIVISION.</summary>
    public const string AdminDivisionTable = "tw-admin-2014-12-25";

    // Counties merged into special municipalities: 2010-12-25 (新北, 臺中, 臺南, 高雄) and 2014-12-25 (桃園).
    private static readonly Dictionary<string, string> MergedCounties = new()
    {
        ["臺北縣"] = "新北市", ["臺中縣"] = "臺中市", ["臺南縣"] = "臺南市", ["高雄縣"] = "高雄市", ["桃園縣"] = "桃園市",
    };

    [GeneratedRegex("(?<county>[台臺]北縣|[台臺]中縣|[台臺]南縣|高雄縣|桃園縣)(?<town>\\p{IsCJKUnifiedIdeographs}{1,3}?)[市鄉鎮]")]
    private static partial Regex OldCounty();

    [GeneratedRegex("[〇零一二兩三四五六七八九十廿卅百]+(?=[段巷弄號樓鄰])")]
    private static partial Regex AddressNumeral();

    /// <summary>
    /// Arabic or Chinese numerals: 四十四, 廿二, 卅一, 一百零二, 十. Null for anything that is not one number,
    /// so approximations such as 七八十 are rejected.
    /// </summary>
    public static int? ChineseNumber(string s)
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

    public static bool IsPostalCodeOnly(string raw) => PostalCode().IsMatch(HalfWidth(raw.Trim()));

    [GeneratedRegex("^\\d{3,6}$")]
    private static partial Regex PostalCode();

    /// <summary>A PEP / 黑名單 / 負面新聞 / Is RCA? cell (extraction-and-normalization.md#source-flags).</summary>
    public static Subjects.FlagValue Flag(string? raw) => raw?.Trim() switch
    {
        null or "" or "-" or "–" or "—" => Subjects.FlagValue.Missing,
        var v when FlagMissing.Contains(v) => Subjects.FlagValue.Missing,
        var v when FlagYes.Contains(v) => Subjects.FlagValue.Yes,
        var v when FlagNo.Contains(v) => Subjects.FlagValue.No,
        _ => Subjects.FlagValue.Unparseable,
    };

    private static readonly HashSet<string> FlagYes = new(StringComparer.OrdinalIgnoreCase) { "是", "✓", "✔", "☑", "V", "Y", "Yes", "TRUE", "1", "checked", "on" };
    private static readonly HashSet<string> FlagNo = new(StringComparer.OrdinalIgnoreCase) { "否", "不是", "X", "✗", "×", "N", "No", "FALSE", "0", "unchecked", "off", "☐" };
    private static readonly HashSet<string> FlagMissing = new(StringComparer.OrdinalIgnoreCase) { "N/A", "NA" };

    public static string? NullIfPlaceholder(string? s) =>
        string.IsNullOrWhiteSpace(s) || IsPlaceholder(s) ? null : s.Trim();

    /// <summary>
    /// Text whose sole meaning is "no data". Only the whole value counts: 「未提供（居住國臺灣）」, 無錫市, 無業 and 無國籍 are values.
    /// </summary>
    public static bool IsPlaceholder(string s) => Placeholders.Contains(s.Trim());

    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "-", "–", "—", "－", "未提供", "未記載", "不詳", "無", "N/A", "NA", "n.a.", "none", "null",
    };
}
