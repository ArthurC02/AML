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
            $"NOT_IDENTICAL_AFTER_NORMALIZATION（Jaro-Winkler {jw:0.00}，Levenshtein {lev}）"));
    }

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
