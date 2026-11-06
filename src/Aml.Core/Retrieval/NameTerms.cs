using System.Text;
using Aml.Core.Normalization;

namespace Aml.Core.Retrieval;

/// <summary>The terms a name is indexed and searched by: words (Chinese split per character) and character n-grams of its key.</summary>
internal static class NameTerms
{
    private const int CjkGramLength = 2;
    private const int LatinGramLength = 3;
    private const int MaxCjkNameLengthWithUnigrams = 3;
    private const string StartMark = "^";
    private const string EndMark = "$";
    private const int MaxStackallocInts = 256;

    public static string Key(string name) => NameComparison.NameKey(name, true);

    /// <summary>Normalized words; a Chinese word becomes one term per character.</summary>
    public static string[] Tokens(string name)
    {
        var tokens = new List<string>();
        foreach (var word in name.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var key = Key(word);
            if (key.Length == 0) continue;
            if (!IsCjk(key)) tokens.Add(key);
            else foreach (var r in key.EnumerateRunes()) tokens.Add(r.ToString());
        }
        return [.. tokens];
    }

    private static readonly char[] WordSeparators = [' ', '-', ',', '.', '·'];

    /// <summary>
    /// Code point n-grams of the key between start and end marks, plus single characters for a short Chinese name. Each gram
    /// is one slice of the marked key, cut at code point boundaries, so a gram costs one string and nothing else.
    /// </summary>
    public static HashSet<string> Grams(string key)
    {
        var grams = new HashSet<string>(StringComparer.Ordinal);
        if (key.Length == 0) return grams;
        var marked = StartMark + key + EndMark;
        Span<int> starts = marked.Length < MaxStackallocInts ? stackalloc int[marked.Length + 1] : new int[marked.Length + 1];
        var codePoints = 0;
        for (var i = 0; i < marked.Length; i += Rune.GetRuneAt(marked, i).Utf16SequenceLength) starts[codePoints++] = i;
        starts[codePoints] = marked.Length;

        var cjk = IsCjk(key);
        var n = cjk ? CjkGramLength : LatinGramLength;
        for (var i = 0; i + n <= codePoints; i++) grams.Add(marked[starts[i]..starts[i + n]]);
        var keyCodePoints = codePoints - 2;
        if (cjk && keyCodePoints <= MaxCjkNameLengthWithUnigrams)
            for (var i = 1; i <= keyCodePoints; i++) grams.Add(marked[starts[i]..starts[i + 1]]);
        return grams;
    }

    public static HashSet<string> ReadingKeys(string name) => Romanization.ReadingKeys(name).ToHashSet(StringComparer.Ordinal);

    public static bool IsCjk(string key) => key.EnumerateRunes().Any(IsCjk);

    private const int CjkExtensionAStart = 0x3400;
    private const int CjkUnifiedEnd = 0x9FFF;
    private const int PrivateUseStart = 0xE000;
    private const int CjkCompatibilityEnd = 0xFAFF;
    private const int CjkExtensionBStart = 0x20000;
    private const int CjkExtensionsEnd = 0x323AF;

    // The private use area holds 造字, bank-defined characters for names.
    private static bool IsCjk(Rune r) => r.Value is >= CjkExtensionAStart and <= CjkUnifiedEnd
        or >= PrivateUseStart and <= CjkCompatibilityEnd
        or >= CjkExtensionBStart and <= CjkExtensionsEnd;
}
