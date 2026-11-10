using System.Globalization;
using System.Text;

namespace Aml.Infra.Models;

/// <summary>
/// The BERT tokenizer of bert-base-chinese as the training code ran it (Hugging Face BertNormalizer with clean_text and
/// handle_chinese_chars, no lower-casing; BertPreTokenizer; WordPiece), keeping each token's offsets in the input.
/// Written out because Microsoft.ML.Tokenizers drops characters this one keeps, such as ○ in 王○○.
/// </summary>
internal sealed class BertWordPiece
{
    private const int MaxWordChars = 100;
    private readonly Dictionary<string, int> vocab;

    public BertWordPiece(string vocabFile)
    {
        vocab = File.ReadLines(vocabFile).Select((w, i) => (w, i)).ToDictionary(x => x.w, x => x.i, StringComparer.Ordinal);
        Unknown = vocab["[UNK]"]; Classification = vocab["[CLS]"]; Separator = vocab["[SEP]"];
    }

    public int Unknown { get; }
    public int Classification { get; }
    public int Separator { get; }

    public readonly record struct Token(int Id, int Start, int End);

    public List<Token> Encode(string text)
    {
        var tokens = new List<Token>();
        foreach (var (word, at) in Words(text)) Pieces(word, at, tokens);
        return tokens;
    }

    // Words: runs between whitespace, punctuation and CJK ideographs; each punctuation mark and each CJK ideograph is a
    // word of its own. As clean_text does first, U+0000, U+FFFD, control characters other than \t \n \r, and a lone
    // surrogate are removed: they split nothing, so AB<U+200B>CD is one word ABCD. Each word is (its kept text, the index
    // in the input of each kept UTF-16 unit).
    private static IEnumerable<(string Word, List<int> At)> Words(string text)
    {
        var word = new StringBuilder();
        var at = new List<int>();
        for (var i = 0; i < text.Length;)
        {
            if (!Rune.TryGetRuneAt(text, i, out var rune)) { i++; continue; }
            var n = rune.Utf16SequenceLength;
            var cp = rune.Value;
            if (cp == 0 || cp == 0xFFFD || (cp is not ('\t' or '\n' or '\r') && IsControl(rune))) { i += n; continue; }
            if (Rune.IsWhiteSpace(rune) || IsPunctuation(rune) || IsCjk(cp))
            {
                if (word.Length > 0) { yield return (word.ToString(), [.. at]); word.Clear(); at.Clear(); }
                if (!Rune.IsWhiteSpace(rune)) yield return (text.Substring(i, n), [.. Enumerable.Range(i, n)]);
            }
            else
            {
                word.Append(text, i, n);
                at.AddRange(Enumerable.Range(i, n));
            }
            i += n;
        }
        if (word.Length > 0) yield return (word.ToString(), at);
    }

    // Greedy longest match from the left; a word with a part outside the vocabulary is one [UNK].
    private void Pieces(string word, List<int> at, List<Token> tokens)
    {
        int start = at[0], end = at[^1] + 1;
        var first = tokens.Count;
        if (word.EnumerateRunes().Count() > MaxWordChars) { tokens.Add(new(Unknown, start, end)); return; }
        for (var from = 0; from < word.Length;)
        {
            var to = word.Length;
            var id = -1;
            while (to > from)
            {
                var piece = from > 0 ? "##" + word[from..to] : word[from..to];
                if (vocab.TryGetValue(piece, out id)) break;
                to--;
                if (to > from && char.IsLowSurrogate(word[to])) to--;
                id = -1;
            }
            if (id < 0)
            {
                tokens.RemoveRange(first, tokens.Count - first);
                tokens.Add(new(Unknown, start, end));
                return;
            }
            tokens.Add(new(id, at[from], at[to - 1] + 1));
            from = to;
        }
    }

    private static bool IsControl(Rune r) =>
        Rune.GetUnicodeCategory(r) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned;

    private static bool IsPunctuation(Rune r) =>
        r.Value is >= 33 and <= 47 or >= 58 and <= 64 or >= 91 and <= 96 or >= 123 and <= 126
        || Rune.GetUnicodeCategory(r) is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;

    private static bool IsCjk(int cp) =>
        cp is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0x20000 and <= 0x2A6DF or >= 0x2A700 and <= 0x2B73F
            or >= 0x2B740 and <= 0x2B81F or >= 0x2B820 and <= 0x2CEAF or >= 0xF900 and <= 0xFAFF or >= 0x2F800 and <= 0x2FA1F;
}
