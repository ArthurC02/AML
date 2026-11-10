using Aml.Infra.Models;

namespace Aml.Tests.Unit.Extraction;

/// <summary>
/// Tokens, windows and spans of the ONNX news models, as the training code made them (Hugging Face BertNormalizer,
/// BertPreTokenizer and WordPiece; 510-token windows every 384; BIO decoding), on a made-up vocabulary.
/// </summary>
public sealed class NewsModelTokensTests : IDisposable
{
    private readonly string vocab = Path.Combine(Path.GetTempPath(), $"vocab-{Guid.NewGuid():N}.txt");
    private readonly BertWordPiece tokenizer;

    public NewsModelTokensTests()
    {
        File.WriteAllLines(vocab, ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "王", "大", "明", "○○", "ab", "##c", "，", "。"]);
        tokenizer = new BertWordPiece(vocab);
    }

    public void Dispose() => File.Delete(vocab);

    private List<(int, int, int)> Encode(string text) => tokenizer.Encode(text).Select(t => (t.Id, t.Start, t.End)).ToList();

    [Fact]
    public void Each_ideograph_and_punctuation_mark_is_a_word_and_other_runs_are_word_pieces()
    {
        // ○ is a symbol, not punctuation or an ideograph, so ○○ stays one word, as in 王○○.
        Assert.Equal([(4, 0, 1), (7, 1, 3), (10, 3, 4), (8, 4, 6), (9, 6, 7), (11, 7, 8)], Encode("王○○，abc。"));
    }

    [Theory]
    [InlineData("xyz", 0, 3)]
    [InlineData("abzz", 0, 4)]   // a word with a part outside the vocabulary is one [UNK]
    [InlineData("⑴", 0, 1)]
    [InlineData("\U00020000", 0, 2)] // an ideograph outside the BMP keeps both UTF-16 units
    public void A_word_outside_the_vocabulary_is_one_unknown_token(string text, int start, int end) =>
        Assert.Equal([(1, start, end)], Encode(text));

    [Fact]
    public void Whitespace_separates_words_and_is_dropped() =>
        Assert.Equal([(4, 0, 1), (6, 2, 3), (5, 4, 5)], Encode("王\t明 大"));

    [Theory]
    [InlineData("a​bc")]  // zero-width space: format character
    [InlineData("a\u0085bc")]  // next line: a control character, though .NET counts it as whitespace
    [InlineData("a\u0001bc")]
    public void A_removed_character_splits_no_word_and_offsets_skip_it(string text) =>
        Assert.Equal([(8, 0, 3), (9, 3, 4)], Encode(text)); // ab ##c, as for abc

    [Fact]
    public void A_lone_surrogate_left_by_a_crop_is_removed()
    {
        // Built in code: test data would not carry a lone surrogate through intact.
        var text = "a" + (char)0xD800 + "bc" + (char)0xDC00;
        Assert.Equal([(8, 0, 3), (9, 3, 4)], Encode(text));
    }


    [Fact]
    public void Text_is_normalized_per_character_so_offsets_stay_valid()
    {
        Assert.Equal("AB1", OnnxNewsModel.Normalize("ＡＢ１"));
        Assert.Equal("㈱", OnnxNewsModel.Normalize("㈱")); // NFKC gives (株): longer, so the character is kept
    }

    [Theory]
    [InlineData(10, "0-10")]
    [InlineData(510, "0-510")]
    [InlineData(511, "0-510 1-511")]
    [InlineData(1000, "0-510 384-894 490-1000")]
    public void Windows_of_510_tokens_start_every_384_and_the_last_ends_at_the_text(int tokens, string expected) =>
        Assert.Equal(expected, string.Join(" ", OnnxNewsModel.Windows(tokens).Select(w => $"{w.Start}-{w.End}")));

    [Fact]
    public void A_span_opens_at_B_or_at_an_I_with_none_open_and_continues_over_I_of_its_role() =>
        Assert.Equal([(0, 2, "PER-Suspect"), (3, 5, "ORG-Other"), (5, 6, "PER-Victim"), (6, 7, "PER-Suspect")],
            OnnxNewsModel.Spans(["B-PER-Suspect", "I-PER-Suspect", "O", "I-ORG-Other", "I-ORG-Other", "B-PER-Victim", "I-PER-Suspect"]));
}
