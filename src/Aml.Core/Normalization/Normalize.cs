using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Aml.Core.Shared;

namespace Aml.Core.Normalization;

public static partial class Normalize
{
    public const string DiacriticVariant = "DIACRITIC_VARIANT";
    public const string NameOrderVariant = "NAME_ORDER_VARIANT";
    public const string NotComparableCrossScript = "NOT_COMPARABLE_CROSS_SCRIPT";

    /// <summary>Stands in a key for a character the source could not render (?, ＊, U+FFFD); a key holding it is never EXACT.</summary>
    public const char UnknownCharacter = '?';

    /// <summary>
    /// Comparison key: NFKC, diacritics folded, case folded, spaces and punctuation removed; letters, digits and
    /// private-use (造字) characters kept per code point, unrenderable characters kept as <see cref="UnknownCharacter"/>.
    /// With <paramref name="foldVariants"/>, controlled variant characters fold to one form (鍾/鐘/钟 → 鐘, simplified → traditional).
    /// </summary>
    public static string NameKey(string name, bool foldVariants = false) => Key(name, foldVariants, keepDiacritics: false);

    private static string Key(string name, bool fold, bool keepDiacritics)
    {
        var d = name.Normalize(NormalizationForm.FormKC);
        if (!keepDiacritics) d = FoldDiacritics(d);
        var sb = new StringBuilder(d.Length);
        foreach (var r in d.EnumerateRunes())
        {
            if (IsUnrenderable(r)) sb.Append(UnknownCharacter);
            else if (IsKeyCharacter(r, keepDiacritics))
                sb.Append(fold && CjkVariants.TryGetValue(r.Value, out var v) ? new Rune(v) : Rune.ToUpperInvariant(r));
        }
        return sb.ToString();
    }

    // đ has no canonical decomposition.
    private static string FoldDiacritics(string s) => s.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);

    internal static bool IsUnrenderable(Rune r) => r.Value is UnknownCharacter or '*' or ReplacementCharacter;

    private const int ReplacementCharacter = 0xFFFD;

    private static bool IsKeyCharacter(Rune r, bool keepDiacritics) =>
        Rune.IsLetterOrDigit(r) || Rune.GetUnicodeCategory(r) == UnicodeCategory.PrivateUse
        || (keepDiacritics && Rune.GetUnicodeCategory(r) == UnicodeCategory.NonSpacingMark);

    internal static string[] NameTokens(string name) =>
        NameSeparator().Split(name.Normalize(NormalizationForm.FormKC)).Where(t => t.Length > 0).ToArray();

    [GeneratedRegex("[\\s·‧・･]+")]
    private static partial Regex NameSeparator();

    /// <summary>Keys for a name in source order and with the surname moved to either end (given-first vs surname-first).</summary>
    public static IEnumerable<string> NameKeys(string name, bool foldVariants = false) => Keys(name, foldVariants, false, reorder: true);

    private static IEnumerable<string> Keys(string name, bool fold, bool keepDiacritics, bool reorder)
    {
        yield return Key(name, fold, keepDiacritics);
        var tokens = NameTokens(name);
        if (!reorder || tokens.Length < 2) yield break;
        yield return Key(string.Join(' ', tokens[1..]) + " " + tokens[0], fold, keepDiacritics);
        yield return Key(tokens[^1] + " " + string.Join(' ', tokens[..^1]), fold, keepDiacritics);
    }

    private static readonly (bool Order, bool Strip, bool Fold)[] RelaxationsFewestFirst =
        [(false, false, false), (true, false, false), (false, true, false), (false, false, true),
         (true, true, false), (true, false, true), (false, true, true), (true, true, true)];

    /// <summary>
    /// EXACT on source characters in source order. EXACT with a finding when it needs a relaxation: VARIANT_CHARACTERS after
    /// controlled folding, DIACRITIC_VARIANT after diacritic folding, NAME_ORDER_VARIANT after moving the surname.
    /// A key with an unknown character is never EXACT. Otherwise PARTIAL: ROMANIZATION_VARIANT when the spellings differ
    /// only by romanization system, NOT_COMPARABLE_CROSS_SCRIPT when no pair shares a script, else with the best
    /// similarity disclosed. Similarity only grades PARTIAL; it is never support and has no threshold.
    /// </summary>
    public static (ComparisonStatus Status, string? Detail) CompareNames(IEnumerable<string> subject, IEnumerable<string> profile)
    {
        var s = subject.ToList();
        var p = profile.ToList();
        if (s.Count == 0) return (ComparisonStatus.SubjectMissing, null);
        if (p.Count == 0) return (ComparisonStatus.ProfileMissing, null);
        if (IsExact(s, p, out var relaxations)) return (ComparisonStatus.Exact, relaxations);
        if (s.Any(a => p.Any(b => Romanization.SameReading(a, b)))) return (ComparisonStatus.Partial, Findings.RomanizationVariant);
        return (ComparisonStatus.Partial, SimilarityDetail(s, p));
    }

    private static bool IsExact(List<string> s, List<string> p, out string? relaxationsUsed)
    {
        IEnumerable<string> Comparable(List<string> names, (bool Order, bool Strip, bool Fold) x) =>
            names.SelectMany(n => Keys(n, x.Fold, !x.Strip, x.Order)).Where(k => k.Length > 0 && !k.Contains(UnknownCharacter, StringComparison.Ordinal));
        foreach (var x in RelaxationsFewestFirst)
            if (Comparable(s, x).Intersect(Comparable(p, x)).Any())
            {
                string?[] found = [x.Fold ? Findings.VariantCharacters : null, x.Strip ? DiacriticVariant : null, x.Order ? NameOrderVariant : null];
                relaxationsUsed = found.Any(f => f is not null) ? string.Join("；", found.OfType<string>()) : null;
                return true;
            }
        relaxationsUsed = null;
        return false;
    }

    private static string SimilarityDetail(List<string> s, List<string> p)
    {
        var pairs = (from a in s.SelectMany(n => NameKeys(n, true))
                     from b in p.SelectMany(n => NameKeys(n, true))
                     let script = SharedScript(a, b)
                     where script is not null
                     select (a, b, Cjk: script == Script.Cjk)).ToList();
        if (pairs.Count == 0) return NotComparableCrossScript;
        var best = pairs.MaxBy(x => Similarity.JaroWinkler(x.a, x.b));
        var jw = Similarity.JaroWinkler(best.a, best.b);
        var lev = pairs.Min(x => Similarity.Levenshtein(x.a, x.b));
        var threshold = best.Cjk ? NameSimilarityThresholdCjk : NameSimilarityThresholdLatin;
        return string.Create(CultureInfo.InvariantCulture,
            $"{EvidenceDetails.NotIdenticalAfterNormalization}（Jaro-Winkler {jw:0.00}，Levenshtein {lev}，{(jw >= threshold ? "SIMILAR" : "DISSIMILAR")}）");
    }

    // ponytail: placeholder thresholds, one per script, pending calibration from labelled cases (open question); they only
    // label the PARTIAL detail and never make the name support, a conflict or a hit.
    public const double NameSimilarityThresholdCjk = 0.90;
    public const double NameSimilarityThresholdLatin = 0.90;

    [Flags]
    private enum Script { None = 0, Latin = 1, Cjk = 2, Other = 4 }

    private static Script? SharedScript(string a, string b)
    {
        var both = Scripts(a) & Scripts(b);
        return both.HasFlag(Script.Cjk) ? Script.Cjk : both.HasFlag(Script.Latin) ? Script.Latin : both.HasFlag(Script.Other) ? Script.Other : null;
    }

    private const int LatinExtendedBEnd = 0x024F;
    private const int LatinExtendedAdditionalStart = 0x1E00;
    private const int LatinExtendedAdditionalEnd = 0x1EFF;
    private const int CjkRadicalsSupplementStart = 0x2E80;

    private static Script Scripts(string key)
    {
        var s = Script.None;
        foreach (var r in key.EnumerateRunes())
            s |= Rune.GetUnicodeCategory(r) == UnicodeCategory.PrivateUse ? Script.Cjk
                : !Rune.IsLetter(r) ? Script.None
                : r.Value <= LatinExtendedBEnd || r.Value is >= LatinExtendedAdditionalStart and <= LatinExtendedAdditionalEnd ? Script.Latin
                : r.Value >= CjkRadicalsSupplementStart ? Script.Cjk
                : Script.Other;
        return s;
    }

    private const string DistinctSurnamesNeverFolded = "涂塗云雲余餘后後";

    // ponytail: the controlled list holds same-reading variants seen in name data (鍾/鐘) and simplified forms that OpenCC
    // maps one-to-many; it needs business approval and grows from reviewed cases.
    private static readonly Dictionary<char, char> ControlledVariants = new()
    {
        ['鍾'] = '鐘',
        ['钟'] = '鐘',
        ['声'] = '聲',
        ['陈'] = '陳',
        ['张'] = '張',
        ['刘'] = '劉',
        ['杨'] = '楊',
        ['黄'] = '黃',
        ['吴'] = '吳',
        ['赵'] = '趙',
        ['郑'] = '鄭',
        ['许'] = '許',
        ['邓'] = '鄧',
        ['冯'] = '馮',
        ['韩'] = '韓',
        ['萧'] = '蕭',
        ['叶'] = '葉',
        ['苏'] = '蘇',
        ['卢'] = '盧',
        ['罗'] = '羅',
        ['谢'] = '謝',
        ['钱'] = '錢',
        ['孙'] = '孫',
        ['马'] = '馬',
        ['龙'] = '龍',
        ['东'] = '東',
        ['国'] = '國',
        ['华'] = '華',
        ['伟'] = '偉',
        ['丽'] = '麗',
        ['庄'] = '莊',
        ['荣'] = '榮',
        ['宁'] = '寧',
        ['台'] = '臺',
        ['阳'] = '陽',
        ['贵'] = '貴',
        ['宝'] = '寶',
        ['凤'] = '鳳',
        ['兰'] = '蘭',
        ['颖'] = '穎',
    };

    // Simplified → traditional: OpenCC one-to-one characters (Data/hans-hant.txt), overridden by the controlled list.
    private static readonly Dictionary<int, int> CjkVariants = BuildVariants();

    private static Dictionary<int, int> BuildVariants()
    {
        var map = DataLines("hans-hant.txt").Select(l => l.EnumerateRunes().ToArray()).ToDictionary(r => r[0].Value, r => r[1].Value);
        foreach (var (k, v) in ControlledVariants) map[k] = v;
        foreach (var c in DistinctSurnamesNeverFolded)
            foreach (var k in map.Where(x => x.Key == c || x.Value == c).Select(x => x.Key).ToList()) map.Remove(k);
        return map;
    }

    /// <summary>Lines of an embedded reference table (Normalization/Data), without its source/license header lines.</summary>
    internal static IEnumerable<string> DataLines(string file)
    {
        using var stream = typeof(Normalize).Assembly.GetManifestResourceStream(file) ?? throw new InvalidOperationException(file);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0 && line[0] != '#') yield return line;
    }

    /// <summary>ISO 3166-1 alpha-2 code (<see cref="Countries"/>), or null when the value does not map unambiguously (AMBIGUOUS).</summary>
    public static string? Country(string raw) => Countries.Lookup(raw);

    public static Gender? Gender(string? raw) => raw?.Trim() switch
    {
        "男" or "Male" => Shared.Gender.Male,
        "女" or "Female" => Shared.Gender.Female,
        _ => null,
    };

    public static string[] Sentences(string text) => text.Split('。', '！', '？', '\n', '；');

    public static bool IsCjk(char c) => c is >= CjkExtensionAStart and <= CjkUnifiedIdeographsEnd;

    private const char CjkExtensionAStart = '㐀';
    private const char CjkUnifiedIdeographsEnd = '鿿';

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
        var s = WithArabicAddressNumerals(HalfWidth(raw.Trim()));
        var m = OldCounty().Match(s);
        if (!m.Success) return (s, null);
        return (s[..m.Index] + CurrentCityAndDistrict(m) + s[(m.Index + m.Length)..], Findings.HistoricalAdminDivision);
    }

    private static string WithArabicAddressNumerals(string s) =>
        AddressNumeral().Replace(s, m => ChineseNumber(m.Value)?.ToString(CultureInfo.InvariantCulture) ?? m.Value);

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
    public static Subjects.FlagValue SourceFlag(string? raw) => raw?.Trim() switch
    {
        null or "" => Subjects.FlagValue.Missing,
        var v when IsDash(v) => Subjects.FlagValue.Missing,
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

    public static bool IsDash(string s) => s.Trim() is "-" or "–" or "—" or "－";

    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "-", "–", "—", "－", "未提供", "未記載", "不詳", "無", "N/A", "NA", "n.a.", "none", "null",
    };
}
