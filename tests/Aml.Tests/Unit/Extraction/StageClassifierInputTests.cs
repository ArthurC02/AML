using Aml.Infra.Models;

namespace Aml.Tests.Unit.Extraction;

/// <summary>
/// The person-stage classifier's evidence text, built as the training code built it (prep_stage.py row(); expectations
/// worked out by hand from that code), on made-up news.
/// </summary>
public class StageClassifierInputTests
{
    private const string M = OnnxStageClassifier.Open + "陳志明" + OnnxStageClassifier.Close;
    private static readonly string Head = new string('甲', 59) + "。"; // the first 60 characters, a sentence of no one

    private static string Evidence(string text, string[] forms, int budget = 509, int tokensPerPiece = 1) =>
        OnnxStageClassifier.Evidence(text, forms, budget, _ => tokensPerPiece, s => OnnxStageClassifier.NamesAPerson(s, forms));

    [Fact]
    public void The_head_the_persons_sentences_and_a_following_sentence_that_names_no_one()
    {
        var text = Head + "陳志明涉嫌詐騙。檢方今日起訴。法院審理中。陳志明否認。被告王大同另案偵辦。陳志明亦稱無罪。";
        Assert.Equal(Head + M + "涉嫌詐騙。檢方今日起訴。…" + M + "否認。…" + M + "亦稱無罪。", Evidence(text, ["陳志明"]));
    }

    [Fact]
    public void Longer_forms_are_marked_first_and_whitespace_between_pieces_is_one_space()
    {
        var text = Head + "陳志明到案。\n另有消息。\n志明稱冤。";
        Assert.Equal(Head + M + "到案。 另有消息。 " + OnnxStageClassifier.Open + "志明" + OnnxStageClassifier.Close + "稱冤。", Evidence(text, ["志明", "陳志明"]));
    }

    [Fact]
    public void Over_the_budget_the_head_the_first_pieces_to_60_percent_and_the_last_that_fit()
    {
        var text = Head + "陳志明一。陳志明二。陳志明三。陳志明四。陳志明五。";
        // Six pieces of 10 tokens each (9 and the joint) against 45: head and 一 (20 ≤ 27), then 五 and 四 from the end (40 ≤ 45).
        Assert.Equal(Head + M + "一。…" + M + "四。" + M + "五。", Evidence(text, ["陳志明"], budget: 45, tokensPerPiece: 9));
    }

    [Theory]
    [InlineData("被告王大同坦承犯行。", true)]
    [InlineData("嫌犯林男遭羈押。", true)]
    [InlineData("檢方起訴陳志明。", true)]
    [InlineData("被告坦承犯行。", false)]
    [InlineData("法院審理中。", false)]
    public void At_run_time_a_sentence_names_a_person_by_a_profile_form_or_an_accused_name(string sentence, bool names) =>
        Assert.Equal(names, OnnxStageClassifier.NamesAPerson(sentence, ["陳志明"]));
}
