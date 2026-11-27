using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Core.Identity;
using Aml.Core.Ports;
using Aml.Core.Screening;
using Aml.Core.Screening.Stage;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using static Aml.Core.Shared.ComparisonStatus;

namespace Aml.Tests.Unit.Screening;

/// <summary>
/// A Profile with several attached news items (新聞資訊_, 新聞資訊1_, 新聞資訊2_ …): every consumer reads every item
/// (identity-adjudication.md#several-news-items, watchlist-and-adverse-information.md#several-news-items). Invented text;
/// the customer is a man born 1980-05-20 and the Profile has no birth date of its own.
/// </summary>
public class SeveralNewsItemsTests
{
    private static readonly PartialDate Reported = new(2026, 5, 20); // the customer is 46 that day

    private static readonly ScreenedSubject Customer =
        new(SubjectRole.Customer, null, ["陳志明"], Gender.Male, new PartialDate(1980, 5, 20), ["TW"], null, null, null, null, [], []);

    private static readonly SourceProfile Profile =
        new("DJ:1", Gender.Male, ["陳志明"], [], ["TW"], [], [new SourceCategory("Special Interest Person (SIP)", "Fraud")], [], [], [], null, [], []);

    private static NewsItem Item(string text, PartialDate? on = null, string? doc = null) => new("1", doc, "標題", "某報", on ?? Reported, text);

    // ---- The stated-value veto across items: at least one compared and every compared one far ----

    [Fact]
    public void A_compatible_age_in_one_item_keeps_a_far_age_in_another_from_concluding()
    {
        var r = IdentityDecisionPolicy.Decide(Customer, Profile, [Item("陳志明（46歲）涉嫌詐欺。"), Item("陳志明（70歲）涉嫌詐欺。")]);

        var age = r.Evidence[EvidenceGroup.StatedAge];
        Assert.Equal((Exact, ReasonCodes.AgeCompatibleAtReport), (age.Status, age.Detail));
        Assert.Equal("報導年齡 46 歲；依報導日期 2026-05-20 推算 46 歲；報導年齡 70 歲；依報導日期 2026-05-20 推算 46 歲", age.Note);
        Assert.Null(r.HardConflict);
        Assert.Equal(IdentityDecision.Review, r.Decision);
    }

    [Fact]
    public void Far_ages_in_every_item_conclude_as_in_one()
    {
        var r = IdentityDecisionPolicy.Decide(Customer, Profile, [Item("陳志明（70歲）涉嫌詐欺。"), Item("陳志明（20歲）涉嫌詐欺。")]);
        Assert.Equal((ReasonCodes.StatedDobConflict, IdentityDecision.NoMatch), (r.HardConflict, r.Decision));
    }

    [Fact]
    public void An_item_whose_age_cannot_be_compared_neither_blocks_nor_concludes()
    {
        // The second item has no report date: its age is not comparable, so the far one stands alone, as in one item.
        var undated = Item("陳志明（46歲）涉嫌詐欺。") with { PublishedOn = null };
        var r = IdentityDecisionPolicy.Decide(Customer, Profile, [Item("陳志明（70歲）涉嫌詐欺。"), undated]);
        Assert.Equal((Conflict, ReasonCodes.StatedDobConflict), (r.Evidence[EvidenceGroup.StatedAge].Status, r.HardConflict));
    }

    [Fact]
    public void A_near_birth_date_in_one_item_keeps_a_far_one_in_another_from_concluding()
    {
        NewsItem near = Item("陳志明（男，民國70年1月1日生）涉嫌詐欺。"), far = Item("陳志明（男，民國50年1月1日生）涉嫌詐欺。");

        var both = IdentityDecisionPolicy.Decide(Customer, Profile, [near, far]);
        var stated = both.Evidence[EvidenceGroup.StatedBirthDate];
        Assert.Equal((Conflict, ReasonCodes.NewsBirthDateDifference, "新聞所載出生日期 1981-01-01、1961-01-01；客戶出生日期 1980-05-20"), (stated.Status, stated.Detail, stated.Note));
        Assert.Equal((null, IdentityDecision.Review, 1), (both.HardConflict, both.Decision, both.Stage3Row));

        // The far one alone concludes.
        Assert.Equal(ReasonCodes.StatedDobConflict, IdentityDecisionPolicy.Decide(Customer, Profile, [far]).HardConflict);
    }

    [Fact]
    public void Genders_of_all_items_are_one_reading()
    {
        Assert.Equal(ReasonCodes.NewsGenderAgrees, NewsStatedCheck.CheckGender(Customer, Profile, [Item("陳志明（男）涉嫌詐欺。"), Item("陳志明涉嫌詐欺。")])!.Detail);
        // 男 in one, 女 in the other: no reading, as both next to one written name in one item.
        Assert.Null(NewsStatedCheck.CheckGender(Customer, Profile, [Item("陳志明（男）涉嫌詐欺。"), Item("陳志明（女）涉嫌詐欺。")]));
    }

    [Fact]
    public void Every_report_date_is_an_event_of_the_temporal_check()
    {
        var e = EvidenceBuilder.TemporalEvidence(Customer, Profile, [Item("陳志明涉嫌詐欺。", new PartialDate(2026, 5, 20)), Item("陳志明涉嫌詐欺。", new PartialDate(1994, 1, 1))]);
        Assert.Equal("最早事件時最多 13 歲", e!.Note);
    }

    // ---- The legal stage across items ----

    private const string Indicts = "陳志明涉嫌詐欺，檢方今日起訴陳志明。";
    private const string Convicts = "臺北地方法院今天依業務侵占罪判處陳志明有期徒刑一年。";
    private const string NoStage = "陳志明出席公司活動。";

    private static SourceCondition Adverse(string[] news, params IStageReader[] readers) =>
        Assert.Single(SourceConditions.DetermineWithNews(Profile, false, news, stageReaders: readers), c => c.Type == ListType.AdverseMedia);

    private sealed class Reader(string name, Func<string, StageAnswer?> answer) : IStageReader
    {
        public string Name => name;
        public StageAnswer? Read(string text, IReadOnlyList<string> names) => answer(text);
    }

    // One row per cell: the items' texts → the condition's stage and its stage codes (rules only).
    [Theory]
    [InlineData(new[] { Indicts }, LegalStage.Indicted, new string[0])]
    [InlineData(new[] { Indicts, Indicts }, LegalStage.Indicted, new string[0])]
    [InlineData(new[] { NoStage, Indicts }, LegalStage.Indicted, new string[0])] // an item that reads no stage says nothing
    [InlineData(new[] { Indicts, NoStage }, LegalStage.Indicted, new string[0])]
    [InlineData(new[] { NoStage, NoStage }, LegalStage.Unknown, new string[0])]
    [InlineData(new[] { Indicts, Convicts }, LegalStage.Unknown, new[] { SourceConditions.NewsItemsDiffer })] // never one item over another
    public void Items_that_read_a_stage_must_read_the_same(string[] news, LegalStage stage, string[] codes)
    {
        var c = Adverse(news);
        Assert.Equal(stage, c.LegalStage);
        Assert.Equal(codes, c.ReasonCodes);
        Assert.Null(c.StageReaders);
    }

    [Fact]
    public void With_models_each_item_is_voted_and_its_readers_are_numbered()
    {
        var models = new IStageReader[] { new Reader("classifier", t => new(t == Indicts ? LegalStage.Indicted : LegalStage.Unknown)), new Reader("llm", t => new(t == Indicts ? LegalStage.Indicted : LegalStage.Unknown)) };
        var c = Adverse([Indicts, NoStage], models);
        Assert.Equal((LegalStage.Indicted, StageBasis.VoteUnanimous), (c.LegalStage, c.LegalStageBasis));
        Assert.Equal([(1, "rules"), (1, "classifier"), (1, "llm"), (2, "rules"), (2, "classifier"), (2, "llm")], c.StageReaders!.Select(r => (r.Item!.Value, r.Reader)));

        // One item alone: its readers carry no number.
        Assert.All(Adverse([Indicts], models).StageReaders!, r => Assert.Null(r.Item));
    }

    [Fact]
    public void A_missing_answer_in_any_item_is_unknown_whatever_the_others_read()
    {
        var models = new IStageReader[] { new Reader("classifier", _ => new(LegalStage.Indicted)), new Reader("llm", t => t == Indicts ? new(LegalStage.Indicted) : null) };
        var c = Adverse([Indicts, NoStage], models);
        Assert.Equal((LegalStage.Unknown, (string?)null), (c.LegalStage, c.LegalStageBasis));
        Assert.Contains(StageVote.ReaderNoAnswer, c.ReasonCodes);
        Assert.Null(c.StageReaders!.Single(r => r is { Item: 2, Reader: "llm" }).Stage);
    }

    // ---- Through the analysis and the explanation ----

    [Fact]
    public void The_explanation_cites_every_item_and_digests_each()
    {
        var input = new CaseInput(Customer, [new CandidateRow("陳志明", "1", null, false)], new Dictionary<string, SourceProfile> { ["1"] = Profile },
            new Dictionary<string, IReadOnlyList<NewsItem>> { ["1"] = [Item(Indicts), Item(Convicts, new PartialDate(2026, 8, 1), doc: "DOC2")] }, []);
        var h = Assert.Single(ExplanationBuilder.Build(input, CaseAnalyzer.Analyze(input, asOf: new DateOnly(2026, 11, 27))).Hits);

        Assert.Equal([("S1", "DJ:1#news", "2026-05-20"), ("S2", "DOC2", "2026-08-01")], h.Sources.Select(s => (s.Ref, s.Id, s.PublishedOn)));
        Assert.True(h.AttachedNews);
        Assert.Equal("某報（2026-05-20）報導「標題」[S1]。", h.NewsDigest!.Summary[0]);
        Assert.Equal("某報（2026-08-01）報導「標題」[S2]。", Assert.Single(h.MoreNewsDigests!).Summary[0]);
        Assert.EndsWith("附加新聞有[S1][S2]。", h.Summary.ExternalData, StringComparison.Ordinal);
        Assert.Contains("ADVERSE_MEDIA（FRAUD；各則附加新聞的法律階段不同（STAGE_NEWS_ITEMS_DIFFER））", h.Summary.ExternalData, StringComparison.Ordinal);
    }
}
