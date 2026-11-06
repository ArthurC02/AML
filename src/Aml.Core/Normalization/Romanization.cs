using System.Text;

namespace Aml.Core.Normalization;

/// <summary>
/// Romanized Chinese names spelled in different systems (Hanyu Pinyin, Wade-Giles, Tongyong, Taiwanese passport).
/// A search variant only: never converted back to Chinese and never a new source name.
/// </summary>
public static class Romanization
{
    private const int MinSyllables = 2;
    private const int LongestSyllable = 6;

    /// <summary>
    /// Every word segments into syllables, the syllable counts agree and every syllable shares a Pinyin reading,
    /// in source order or with the surname moved.
    /// </summary>
    public static bool SameReading(string a, string b)
    {
        var x = SyllableReadingsPerWordOrder(a);
        var y = SyllableReadingsPerWordOrder(b);
        if (x is null || y is null) return false;
        var sourceOrder = y[0];
        if (sourceOrder.Length < MinSyllables) return false;
        return x.Any(o => o.Length == sourceOrder.Length && o.Zip(sourceOrder).All(t => t.First.Overlaps(t.Second)));
    }

    /// <summary>
    /// Hanyu Pinyin keys (upper case, no separators, ü as V) for retrieval across scripts: a Chinese name by the readings
    /// of its characters, a romanized name by its syllables' Pinyin readings in each word order. Polyphones expand, at most
    /// <see cref="MaxReadingKeys"/> keys. Empty when a character has no reading or a word does not segment.
    /// </summary>
    public static IReadOnlyList<string> ReadingKeys(string name)
    {
        var n = name.Normalize(NormalizationForm.FormKC);
        if (n.EnumerateRunes().Any(r => HanziReadings.ContainsKey(r.Value))) return HanziReadingKeys(n);
        return SyllableReadingsPerWordOrder(name)?.SelectMany(o => Product(o)).Distinct().Take(MaxReadingKeys).ToList() ?? [];
    }

    public const int MaxReadingKeys = 16;

    private static List<string> HanziReadingKeys(string name)
    {
        var sets = new List<IReadOnlyCollection<string>>();
        foreach (var r in name.EnumerateRunes())
            if (HanziReadings.TryGetValue(r.Value, out var p)) sets.Add(p);
            else if (IsUnreadUnknownOrOtherScript(r)) return [];
        return Product(sets).Take(MaxReadingKeys).ToList();
    }

    private static bool IsUnreadUnknownOrOtherScript(Rune r) =>
        Normalize.IsUnrenderable(r) || (!Rune.IsWhiteSpace(r) && !Rune.IsPunctuation(r));

    private static IEnumerable<string> Product(IReadOnlyList<IReadOnlyCollection<string>> sets)
    {
        IEnumerable<string> acc = [""];
        foreach (var set in sets) acc = acc.SelectMany(_ => set, (k, s) => k + s);
        return acc;
    }

    private static List<HashSet<string>[]>? SyllableReadingsPerWordOrder(string name)
    {
        var words = Normalize.NameTokens(name).SelectMany(w => w.Split([',', '.'], StringSplitOptions.RemoveEmptyEntries)).ToArray();
        if (words.Length == 0) return null;
        // ponytail: a hyphenated given name (Chih-Ming) marks Taiwanese passport style, so a syllable that is also a
        // Wade-Giles/Tongyong/passport spelling reads only in those systems (Chi-Ming is ji/qi, not chi). Heuristic; a
        // name-level system detector is the upgrade if reviewed cases show misses.
        var taiwanStyle = words.Any(w => w.Contains('-', StringComparison.Ordinal));
        var syllables = new List<string[]>();
        foreach (var w in words)
            if (Syllables(w) is { } s) syllables.Add(s);
            else return null;
        return WordOrders(syllables).Select(o => o.SelectMany(x => x).Select(s => Readings(s, taiwanStyle)).ToArray()).ToList();
    }

    private static IEnumerable<List<string[]>> WordOrders(List<string[]> w)
    {
        yield return w;
        if (w.Count < 2) yield break;
        yield return [.. w[1..], w[0]];
        yield return [w[^1], .. w[..^1]];
    }

    private static string[]? Syllables(string word)
    {
        var acc = new List<string>();
        foreach (var hyphenPart in word.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = Normalize.NameKey(hyphenPart);
            if (key.Length == 0 || !key.All(char.IsAsciiLetter) || !Split(key, 0, acc)) return null;
        }
        return acc.Count == 0 ? null : [.. acc];
    }

    // ponytail: longest syllable first with backtracking; a string with two parses (XIAN = XIAN or XI'AN) takes the longest.
    private static bool Split(string s, int i, List<string> acc)
    {
        if (i == s.Length) return true;
        for (var n = Math.Min(LongestSyllable, s.Length - i); n > 0; n--)
        {
            var syllable = s.Substring(i, n);
            if (!Pinyin.Contains(syllable) && !Spellings.ContainsKey(syllable)) continue;
            acc.Add(syllable);
            if (Split(s, i + n, acc)) return true;
            acc.RemoveAt(acc.Count - 1);
        }
        return false;
    }

    private static HashSet<string> Readings(string syllable, bool taiwanStyle)
    {
        var r = Spellings.TryGetValue(syllable, out var other) ? new HashSet<string>(other) : [];
        var spellingIsItsOwnReading = Pinyin.Contains(syllable) && (r.Count == 0 || !taiwanStyle);
        if (spellingIsItsOwnReading) r.Add(syllable);
        return r;
    }

    private const int PinyinColumn = 0;
    private const int WadeGilesPassportColumn = 1;
    private const int TongyongColumn = 2;

    private static readonly string[][] Rows = Normalize.DataLines("romanization.txt").Select(l => l.Split('\t')).ToArray();
    private static readonly HashSet<string> Pinyin = Rows.Select(r => r[PinyinColumn]).ToHashSet();

    // ponytail: the passport list is controlled and grows from reviewed cases.
    private static readonly (string Spelling, string Pinyin)[] PassportSpellings = [("LEE", "LI"), ("JENN", "REN"), ("JONG", "ZHONG"), ("SOONG", "SONG")];

    private static readonly Dictionary<string, HashSet<string>> Spellings = Rows
        .SelectMany(r => new[] { (r[WadeGilesPassportColumn], r[PinyinColumn]), (r[TongyongColumn], r[PinyinColumn]) })
        .Concat(PassportSpellings)
        .GroupBy(x => x.Item1, x => x.Item2).ToDictionary(g => g.Key, g => g.ToHashSet());

    // Data/hanzi-pinyin.txt rows: Pinyin, then every character with that reading.
    private static readonly Dictionary<int, List<string>> HanziReadings = BuildHanzi();

    private static Dictionary<int, List<string>> BuildHanzi()
    {
        var map = new Dictionary<int, List<string>>();
        foreach (var l in Normalize.DataLines("hanzi-pinyin.txt"))
        {
            var tab = l.IndexOf('\t', StringComparison.Ordinal);
            foreach (var r in l[(tab + 1)..].EnumerateRunes())
                (map.TryGetValue(r.Value, out var list) ? list : map[r.Value] = []).Add(l[..tab]);
        }
        return map;
    }
}
