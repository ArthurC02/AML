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
    [InlineData("法院\n被告甲甲\n主 文\n甲甲與乙乙", "甲甲", 3)]
    [InlineData("法院\n被告甲甲\n主 文\n甲甲與乙乙", "乙乙", 4)]
    [InlineData("文內提及判決主文內容。", "甲甲", 5)]
    [InlineData("法院\n主文\n內容", "", 5)]
    [InlineData("主文", "甲甲", 5)]
    [InlineData("😀法院\n被告Ａ０１\n主\r\n文\n內容", "A01", 3)]
    public void Caption_observations_append_literal_presence_absence_or_unknown_after_the_unchanged_query(string text, string mention, long marker)
    {
        long[] ids = [2, 4, 3, 1, 4, 2, 5, 3], types = [0, 0, 0, 1, 1, 1, 1, 1];
        var (actual, segments) = OnnxNewsModel.CaptionObservation(ids, types, OnnxNewsModel.EventCaption(text), mention);
        Assert.Equal([.. ids, marker], actual);
        Assert.Equal([.. types, 1L], segments);
        Assert.Equal(new long[] { 2, 4, 3, 1, 4, 2, 5, 3 }, ids);
    }

    [Fact]
    public void A_full_event_input_keeps_every_original_token_without_a_caption_observation()
    {
        var ids = Enumerable.Repeat(7L, 512).ToArray();
        var types = Enumerable.Repeat(1L, 512).ToArray();
        var (actual, segments) = OnnxNewsModel.CaptionObservation(ids, types, "甲甲", "甲甲");
        Assert.Same(ids, actual);
        Assert.Same(types, segments);
    }

    [Theory]
    [InlineData(10, "0-10")]
    [InlineData(510, "0-510")]
    [InlineData(511, "0-510 1-511")]
    [InlineData(1000, "0-510 384-894 490-1000")]
    public void Windows_of_510_tokens_start_every_384_and_the_last_ends_at_the_text(int tokens, string expected) =>
        Assert.Equal(expected, string.Join(" ", OnnxNewsModel.Windows(tokens).Select(w => $"{w.Start}-{w.End}")));

    [Fact]
    public void Optional_ascii_adapter_keeps_party_codes_distinct_and_preserves_original_offsets()
    {
        File.WriteAllLines(vocab, ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "a", "b", "##01", "##02"]);
        var codes = new BertWordPiece(vocab);
        Assert.Equal([1, 1], codes.Encode(OnnxNewsModel.Normalize("A01 B02")).Select(t => t.Id));
        Assert.Equal([(4, 0, 1), (6, 1, 3), (5, 4, 5), (7, 5, 7)],
            codes.Encode(OnnxNewsModel.Normalize("A01 B02", true)).Select(t => (t.Id, t.Start, t.End)));
        Assert.Equal("\U00020000a01 Éİ", OnnxNewsModel.Normalize("\U00020000\uFF2101 Éİ", true));
    }

    [Fact]
    public void Decoding_never_puts_an_I_after_O_or_after_another_role()
    {
        string[] tags = ["O", "B-PER-Suspect", "I-PER-Suspect", "B-ORG-Other", "I-ORG-Other"];
        // Per token the most probable tags are O then I-PER-Suspect, which no valid sequence has.
        var probs = new float[,] { { 0.55f, 0.45f, 0, 0, 0 }, { 0.01f, 0.05f, 0.9f, 0.02f, 0.02f }, { 0.3f, 0.1f, 0.1f, 0.1f, 0.4f } };

        Assert.Equal(["B-PER-Suspect", "I-PER-Suspect", "O"], OnnxNewsModel.Decode(probs, tags));
    }

    [Fact]
    public void Document_roles_share_source_forms_and_adjust_B_and_I_without_changing_other_tokens()
    {
        string[] roles = ["PER-Suspect", "PER-Victim", "PER-Official", "PER-Other", "ORG-Suspect", "ORG-Victim", "ORG-Other"];
        string[] labels = ["O", .. roles.SelectMany(r => new[] { "B-" + r, "I-" + r })];
        var tokens = new List<BertWordPiece.Token> { new(1, 0, 1), new(1, 2, 3), new(1, 4, 5) };
        var probabilities = new float[3, 15];
        probabilities[0, 1] = 0.8f; probabilities[0, 3] = 0.2f;
        probabilities[1, 1] = 0.2f; probabilities[1, 3] = 0.8f;
        probabilities[2, 0] = 1;
        var (groups, features, mask) = OnnxNewsModel.RoleGroups("A A B", tokens, probabilities, new float[,] { { 2 }, { 6 }, { 9 } }, labels);
        Assert.Single(groups);
        Assert.Equal([(0, 1), (1, 2)], groups[0]);
        Assert.Equal(2, features[0, 0, 0]); Assert.Equal(6, features[0, 1, 0]);
        Assert.Equal(0.8f, features[0, 0, 2]); Assert.Equal(0.8f, features[0, 1, 3]);
        Assert.Equal(1, mask[0, 0]); Assert.Equal(1, mask[0, 1]);
        var original = (float[,])probabilities.Clone();
        OnnxNewsModel.ApplyRoleBiases(probabilities, groups, new float[8]);
        Assert.True(Math.Abs(probabilities[0, 1] - original[0, 1]) < 1e-6);
        float[] bias = [0, 3, 0, 0, 0, 0, 0, 0];
        OnnxNewsModel.ApplyRoleBiases(probabilities, groups, bias);
        Assert.True(probabilities[1, 1] > probabilities[1, 3]);
        Assert.Equal(1, probabilities[2, 0]);
    }

    [Fact]
    public void Event_contexts_include_the_prefix_and_all_requested_form_occurrences_without_NER_predictions()
    {
        var tokens = new List<BertWordPiece.Token> { new(1, 0, 1), new(1, 1, 3), new(1, 4, 5), new(1, 6, 7), new(1, 7, 9) };
        var probabilities = new float[5, 15];
        for (var i = 0; i < 5; i++) probabilities[i, 0] = 1;
        var hidden = new float[,] { { 2 }, { 4 }, { 10 }, { 6 }, { 8 } };
        var found = OnnxNewsModel.EventContexts("A01 X A01", "A01", tokens, probabilities, hidden);
        Assert.Equal(3, found.GetLength(0));
        Assert.Equal(6, found[0, 0]); Assert.Equal(3, found[1, 0]); Assert.Equal(7, found[2, 0]);
        Assert.Equal(1, found[0, 1]);
        Assert.Equal(1, found[0, 9]); Assert.Equal(0, found[0, 10]);
        Assert.Equal(0, found[1, 9]); Assert.Equal(1, found[1, 10]);
        Assert.Equal(1, OnnxNewsModel.EventContexts("A01 X A01", "missing", tokens, probabilities, hidden).GetLength(0));
        Assert.Equal(1, OnnxNewsModel.EventContexts("A01 X A01", "", tokens, probabilities, hidden).GetLength(0));
    }

    [Fact]
    public void Event_query_anchors_follow_the_current_sentence_and_all_repeated_occurrences()
    {
        Assert.Equal(new float[] { 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 1 },
            OnnxNewsModel.EventAnchors("A one. A two. A three.", "A", "A two.").Cast<float>());
        Assert.Equal(new float[] { 0, 0, 0, 0, 1, 0, 0, 0, 1 },
            OnnxNewsModel.EventAnchors("A one. unnamed. A two.", "A", "unnamed.").Cast<float>());
        Assert.Equal(new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 0 },
            OnnxNewsModel.EventAnchors("A one. A two. A one.", "A", "A one.").Cast<float>());
        Assert.All(OnnxNewsModel.EventAnchors("AB", "AB", "B").Cast<float>(), value => Assert.Equal(0, value));
        Assert.All(OnnxNewsModel.EventAnchors("A", "A", "missing").Cast<float>(), value => Assert.Equal(0, value));
        Assert.Equal(1, OnnxNewsModel.EventAnchors("", "", "").GetLength(0));
        Assert.Equal(1, OnnxNewsModel.EventAnchors("\U00020000 first. \U00020000 next.", "\U00020000", "\U00020000 next.")[2, 0]);
    }

    [Fact]
    public void A_span_opens_at_B_or_at_an_I_with_none_open_and_continues_over_I_of_its_role() =>
        Assert.Equal([(0, 2, "PER-Suspect"), (3, 5, "ORG-Other"), (5, 6, "PER-Victim"), (6, 7, "PER-Suspect")],
            OnnxNewsModel.Spans(["B-PER-Suspect", "I-PER-Suspect", "O", "I-ORG-Other", "I-ORG-Other", "B-PER-Victim", "I-PER-Suspect"]));
}
