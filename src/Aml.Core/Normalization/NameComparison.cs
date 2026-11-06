using System.Buffers;
using System.Globalization;
using System.Text;
using Aml.Core.Shared;

namespace Aml.Core.Normalization;

public static class NameComparison
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

    private const int MaxStackallocChars = 512;

    /// <summary>
    /// NFKC, skipped for a name made only of ASCII and CJK Unified Ideographs (U+4E00 to U+9FFF): none of those decompose or
    /// compose under any normalization form, so the name is already normalized, and Normalize would still copy it.
    /// </summary>
    internal static string Nfkc(string s) => IsNormalizationInvariant(s) ? s : s.Normalize(NormalizationForm.FormKC);

    private const char CjkUnifiedStart = '一';
    private const char CjkUnifiedEnd = '鿿';

    private static bool IsNormalizationInvariant(string s)
    {
        foreach (var c in s)
            if (!char.IsAscii(c) && c is < CjkUnifiedStart or > CjkUnifiedEnd) return false;
        return true;
    }

    private static string Key(string name, bool fold, bool keepDiacritics)
    {
        var d = Nfkc(name);
        if (!keepDiacritics && !IsNormalizationInvariant(d)) d = FoldDiacritics(d);
        // Each rune (one or two chars) becomes at most one rune of at most two chars: twice the length always fits.
        var capacity = d.Length * 2;
        char[]? rented = null;
        var key = capacity <= MaxStackallocChars ? stackalloc char[capacity] : (rented = ArrayPool<char>.Shared.Rent(capacity));
        try
        {
            var length = 0;
            foreach (var r in d.EnumerateRunes())
            {
                if (IsUnrenderable(r)) key[length++] = UnknownCharacter;
                else if (IsKeyCharacter(r, keepDiacritics))
                    length += (fold && CjkVariants.TryGetValue(r.Value, out var v) ? new Rune(v) : Rune.ToUpperInvariant(r)).EncodeToUtf16(key[length..]);
            }
            return new string(key[..length]);
        }
        finally
        {
            if (rented is not null) ArrayPool<char>.Shared.Return(rented);
        }
    }

    // đ has no canonical decomposition.
    private static string FoldDiacritics(string s) => s.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);

    internal static bool IsUnrenderable(Rune r) => r.Value is UnknownCharacter or '*' or ReplacementCharacter;

    private const int ReplacementCharacter = 0xFFFD;

    private static bool IsKeyCharacter(Rune r, bool keepDiacritics) =>
        Rune.IsLetterOrDigit(r) || Rune.GetUnicodeCategory(r) == UnicodeCategory.PrivateUse
        || (keepDiacritics && Rune.GetUnicodeCategory(r) == UnicodeCategory.NonSpacingMark);

    /// <summary>The name's words: runs of characters between white space (as char.IsWhiteSpace, the same set as regex \s) and middle dots.</summary>
    internal static string[] NameTokens(string name)
    {
        var n = Nfkc(name);
        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i <= n.Length; i++)
        {
            var separator = i == n.Length || IsNameSeparator(n[i]);
            if (!separator && start < 0) start = i;
            else if (separator && start >= 0)
            {
                tokens.Add(n[start..i]);
                start = -1;
            }
        }
        return [.. tokens];
    }

    private static bool IsNameSeparator(char c) => char.IsWhiteSpace(c) || c is '·' or '‧' or '・' or '･';

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
        var map = EmbeddedTables.Lines("hans-hant.txt").Select(l => l.EnumerateRunes().ToArray()).ToDictionary(r => r[0].Value, r => r[1].Value);
        foreach (var (k, v) in ControlledVariants) map[k] = v;
        foreach (var c in DistinctSurnamesNeverFolded)
            foreach (var k in map.Where(x => x.Key == c || x.Value == c).Select(x => x.Key).ToList()) map.Remove(k);
        return map;
    }
}
