using System.Collections.Frozen;
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
        return x.Any(o => o.Length == sourceOrder.Length && o.Zip(sourceOrder).All(t => t.First.Set.Overlaps(t.Second.Set)));
    }

    /// <summary>
    /// Hanyu Pinyin keys (upper case, no separators, ü as V) for retrieval across scripts: a Chinese name by the readings
    /// of its characters, a romanized name by its syllables' Pinyin readings in each word order. Polyphones expand, at most
    /// <see cref="MaxReadingKeys"/> keys. Empty when a character has no reading or a word does not segment.
    /// </summary>
    public static IReadOnlyList<string> ReadingKeys(string name)
    {
        var n = NameComparison.Nfkc(name);
        if (HasHanzi(n)) return HanziReadingKeys(n);
        return SyllableReadingsPerWordOrder(name)?.SelectMany(o => Product(Array.ConvertAll(o, r => r.InOrder))).Distinct().Take(MaxReadingKeys).ToList() ?? [];
    }

    public const int MaxReadingKeys = 16;

    // An ASCII name holds no Chinese character, so the common romanized name skips the per-character lookup.
    private static bool HasHanzi(string name)
    {
        if (Ascii.IsValid(name)) return false;
        foreach (var r in name.EnumerateRunes())
            if (HanziReadings.ContainsKey(r.Value)) return true;
        return false;
    }

    private static List<string> HanziReadingKeys(string name)
    {
        var sets = new List<string[]>();
        foreach (var r in name.EnumerateRunes())
            if (HanziReadings.TryGetValue(r.Value, out var p)) sets.Add(p);
            else if (IsUnreadUnknownOrOtherScript(r)) return [];
        return Product(sets).Take(MaxReadingKeys).ToList();
    }

    private static bool IsUnreadUnknownOrOtherScript(Rune r) =>
        NameComparison.IsUnrenderable(r) || (!Rune.IsWhiteSpace(r) && !Rune.IsPunctuation(r));

    // Every pick of one reading per set, the last set varying fastest, each key concatenated once.
    private static IEnumerable<string> Product(IReadOnlyList<string[]> options)
    {
        if (options.Any(o => o.Length == 0)) yield break;
        var pick = new int[options.Count];
        var parts = new string[options.Count];
        while (true)
        {
            for (var i = 0; i < options.Count; i++) parts[i] = options[i][pick[i]];
            yield return string.Concat(parts);
            var last = options.Count - 1;
            while (last >= 0 && ++pick[last] == options[last].Length) pick[last--] = 0;
            if (last < 0) yield break;
        }
    }

    private static List<SyllableReadings[]>? SyllableReadingsPerWordOrder(string name)
    {
        var words = NameComparison.NameTokens(name).SelectMany(w => w.Split([',', '.'], StringSplitOptions.RemoveEmptyEntries)).ToArray();
        if (words.Length == 0) return null;
        // ponytail: a hyphenated given name (Chih-Ming) marks Taiwanese passport style, so a syllable that is also a
        // Wade-Giles/Tongyong/passport spelling reads only in those systems (Chi-Ming is ji/qi, not chi). Heuristic; a
        // name-level system detector is the upgrade if reviewed cases show misses.
        var taiwanStyle = words.Any(w => w.Contains('-', StringComparison.Ordinal));
        var readingsPerWord = new List<SyllableReadings[]>();
        foreach (var w in words)
            if (Syllables(w) is { } s) readingsPerWord.Add(Array.ConvertAll(s, x => taiwanStyle ? ReadingsOf[x].TaiwanStyle : ReadingsOf[x].Pinyin));
            else return null;
        return WordOrders(readingsPerWord).Select(o => o.SelectMany(x => x).ToArray()).ToList();
    }

    // Source order, surname moved last, surname moved first; with two words the last two are the same order, given once.
    private static IEnumerable<List<T[]>> WordOrders<T>(List<T[]> w)
    {
        yield return w;
        if (w.Count < 2) yield break;
        yield return [.. w[1..], w[0]];
        if (w.Count > 2) yield return [w[^1], .. w[..^1]];
    }

    private static string[]? Syllables(string word)
    {
        var acc = new List<string>();
        foreach (var hyphenPart in word.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = NameComparison.NameKey(hyphenPart);
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
            var candidate = s.AsSpan(i, n);
            if (!PinyinBySpan.TryGetValue(candidate, out var syllable) && !SpellingsBySpan.TryGetValue(candidate, out syllable, out _)) continue;
            acc.Add(syllable);
            if (Split(s, i + n, acc)) return true;
            acc.RemoveAt(acc.Count - 1);
        }
        return false;
    }

    /// <summary>A syllable's Pinyin readings: the set for <see cref="SameReading"/>, and the same readings in the set's order for keys.</summary>
    private sealed record SyllableReadings(HashSet<string> Set, string[] InOrder)
    {
        public static SyllableReadings Of(HashSet<string> set) => new(set, [.. set]);
    }

    private static HashSet<string> Readings(string syllable, bool taiwanStyle)
    {
        var isPinyin = Pinyin.Contains(syllable);
        if (!Spellings.TryGetValue(syllable, out var other)) return isPinyin ? [syllable] : [];
        return isPinyin && !taiwanStyle ? [.. other, syllable] : other;
    }

    private const int PinyinColumn = 0;
    private const int WadeGilesPassportColumn = 1;
    private const int TongyongColumn = 2;

    private static readonly string[][] Rows = EmbeddedTables.Lines("romanization.txt").Select(l => l.Split('\t')).ToArray();
    private static readonly HashSet<string> Pinyin = Rows.Select(r => r[PinyinColumn]).ToHashSet();

    // ponytail: the passport list is controlled and grows from reviewed cases.
    private static readonly (string Spelling, string Pinyin)[] PassportSpellings = [("LEE", "LI"), ("JENN", "REN"), ("JONG", "ZHONG"), ("SOONG", "SONG")];

    private static readonly Dictionary<string, HashSet<string>> Spellings = Rows
        .SelectMany(r => new[] { (r[WadeGilesPassportColumn], r[PinyinColumn]), (r[TongyongColumn], r[PinyinColumn]) })
        .Concat(PassportSpellings)
        .GroupBy(x => x.Item1, x => x.Item2).ToDictionary(g => g.Key, g => g.ToHashSet());

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> PinyinBySpan = Pinyin.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly Dictionary<string, HashSet<string>>.AlternateLookup<ReadOnlySpan<char>> SpellingsBySpan = Spellings.GetAlternateLookup<ReadOnlySpan<char>>();

    // A syllable's readings depend only on the syllable and the name's style, so every syllable's are worked out once,
    // shared read-only by every name, instead of per name.
    private static readonly FrozenDictionary<string, (SyllableReadings Pinyin, SyllableReadings TaiwanStyle)> ReadingsOf =
        Pinyin.Concat(Spellings.Keys).Distinct(StringComparer.Ordinal)
            .ToFrozenDictionary(s => s, s => (SyllableReadings.Of(Readings(s, false)), SyllableReadings.Of(Readings(s, true))), StringComparer.Ordinal);

    // Data/hanzi-pinyin.txt rows: Pinyin, then every character with that reading.
    private static readonly Dictionary<int, string[]> HanziReadings = BuildHanzi();

    private static Dictionary<int, string[]> BuildHanzi()
    {
        var map = new Dictionary<int, List<string>>();
        foreach (var l in EmbeddedTables.Lines("hanzi-pinyin.txt"))
        {
            var tab = l.IndexOf('\t', StringComparison.Ordinal);
            foreach (var r in l[(tab + 1)..].EnumerateRunes())
                (map.TryGetValue(r.Value, out var list) ? list : map[r.Value] = []).Add(l[..tab]);
        }
        return map.ToDictionary(e => e.Key, e => e.Value.ToArray());
    }
}
