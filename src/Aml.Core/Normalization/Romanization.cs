namespace Aml.Core.Normalization;

/// <summary>
/// Romanized Chinese names spelled in different systems (Hanyu Pinyin, Wade-Giles, Tongyong, Taiwanese passport).
/// A search variant only: never converted back to Chinese and never a new source name.
/// </summary>
public static class Romanization
{
    /// <summary>Same syllable count and every syllable shares a Pinyin reading, in source order or with the surname moved.</summary>
    public static bool SameReading(string a, string b)
    {
        var x = Syllables(a);
        var y = Syllables(b);
        if (x is null || y is null || x.Length < 2 || x.Length != y.Length) return false;
        return Orders(x).Any(o => o.Zip(y).All(t => Readings(t.First).Overlaps(Readings(t.Second))));
    }

    // Null unless every token is plain Latin letters (after diacritic folding).
    private static string[]? Syllables(string name)
    {
        var tokens = name.Split([' ', '-', ',', '.'], StringSplitOptions.RemoveEmptyEntries).Select(t => Normalize.NameKey(t)).ToArray();
        return tokens.All(t => t.Length > 0 && t.All(char.IsAsciiLetter)) ? tokens : null;
    }

    private static IEnumerable<string[]> Orders(string[] s)
    {
        yield return s;
        yield return [.. s[1..], s[0]];
        yield return [s[^1], .. s[..^1]];
    }

    private static HashSet<string> Readings(string syllable) =>
        Table.TryGetValue(syllable, out var r) ? [syllable, .. r] : [syllable];

    // ponytail: controlled list of common non-Pinyin spellings and their Pinyin readings; it needs business approval
    // and grows from reviewed cases. A spelling with two readings (CHUNG = zhong or chong) lists both.
    private static readonly Dictionary<string, string[]> Table = new()
    {
        ["CHUNG"] = ["ZHONG", "CHONG"], ["JHONG"] = ["ZHONG"], ["JONG"] = ["ZHONG"],
        ["JEN"] = ["REN"], ["JENN"] = ["REN"],
        ["CHEN"] = ["ZHEN"], ["CHANG"] = ["ZHANG"], ["JHANG"] = ["ZHANG"],
        ["HSU"] = ["XU"], ["SYU"] = ["XU"], ["HSIEH"] = ["XIE"], ["SIE"] = ["XIE"],
        ["TSAI"] = ["CAI"], ["TSENG"] = ["ZENG"], ["CHOU"] = ["ZHOU"], ["JHOU"] = ["ZHOU"],
        ["CHIANG"] = ["JIANG", "QIANG"], ["KUO"] = ["GUO"], ["KAO"] = ["GAO"], ["TENG"] = ["DENG"],
        ["TUNG"] = ["DONG", "TONG"], ["TAI"] = ["DAI"], ["PAI"] = ["BAI"], ["YEH"] = ["YE"],
        ["CHIU"] = ["QIU"], ["CIOU"] = ["QIU"], ["CHIEN"] = ["JIAN", "QIAN"],
        ["CHIH"] = ["ZHI", "CHI"], ["JHIH"] = ["ZHI"], ["HSIN"] = ["XIN"], ["SIN"] = ["XIN"],
        ["HSIANG"] = ["XIANG"], ["SIANG"] = ["XIANG"], ["HSIUNG"] = ["XIONG"], ["SHIH"] = ["SHI"],
        ["TZU"] = ["ZI"], ["TSU"] = ["ZU", "CU"], ["HSIAO"] = ["XIAO"], ["SIAO"] = ["XIAO"],
        ["CHI"] = ["JI", "QI"], ["CHENG"] = ["ZHENG"], ["JHENG"] = ["ZHENG"],
    };
}
