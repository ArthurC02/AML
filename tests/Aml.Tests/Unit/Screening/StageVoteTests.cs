using Aml.Core.Ports;
using Aml.Core.Screening;
using Aml.Core.Screening.Stage;
using Aml.Core.Subjects;
using static Aml.Core.Screening.LegalStage;

namespace Aml.Tests.Unit.Screening;

/// <summary>The vote of the rules and the stage models (watchlist-and-adverse-information.md#three-readers).</summary>
public class StageVoteTests
{
    private const string News = "王大同涉嫌詐欺，檢方今日起訴陳志明。";

    private sealed class Reader(string name, StageAnswer? answer) : IStageReader
    {
        public string Name => name;
        public int Calls { get; private set; }

        public StageAnswer? Read(string text, IReadOnlyList<string> names) { Calls++; return answer; }
    }

    private static readonly SourceProfile Person =
        new("DJ:1", null, ["陳志明"], [], [], [], [new("Special Interest Person (SIP)", "Fraud")], [], [], [], null, [], []);

    private static SourceCondition Adverse(string? news, params IStageReader[] readers) =>
        Assert.Single(SourceConditions.Determine(Person, false, news, stageReaders: readers), c => c.Type == ListType.AdverseMedia);

    // One row per cell: readers present (the rules, then the models that answered) → stage, basis, disagreement.
    [Theory]
    // Only the rules: their reading unchanged.
    [InlineData(Indicted, StageBasis.Keyword, new LegalStage[0], Indicted, StageBasis.Keyword, false)]
    [InlineData(Unknown, null, new LegalStage[0], Unknown, null, false)]
    // The rules and one model: the rules' reading; a model that differs only adds the disagreement.
    [InlineData(Indicted, StageBasis.Keyword, new[] { Indicted }, Indicted, StageBasis.Keyword, false)]
    [InlineData(Indicted, StageBasis.Keyword, new[] { Convicted }, Indicted, StageBasis.Keyword, true)]
    [InlineData(Unknown, null, new[] { Alleged }, Unknown, null, true)]
    // Three: all the same.
    [InlineData(Convicted, StageBasis.ReleaseWholeText, new[] { Convicted, Convicted }, Convicted, StageBasis.VoteUnanimous, false)]
    [InlineData(Unknown, null, new[] { Unknown, Unknown }, Unknown, StageBasis.VoteUnanimous, false)]
    // Three: two the same is not enough, whichever reader is the odd one out, UNKNOWN among the two or not.
    [InlineData(Indicted, StageBasis.Keyword, new[] { Indicted, Convicted }, Unknown, StageBasis.VoteMajority, true)]
    [InlineData(Indicted, StageBasis.Keyword, new[] { Convicted, Indicted }, Unknown, StageBasis.VoteMajority, true)]
    [InlineData(Indicted, StageBasis.Keyword, new[] { Convicted, Convicted }, Unknown, StageBasis.VoteMajority, true)]
    [InlineData(Unknown, null, new[] { Unknown, Convicted }, Unknown, StageBasis.VoteMajority, true)]
    [InlineData(Indicted, StageBasis.Keyword, new[] { Unknown, Unknown }, Unknown, StageBasis.VoteMajority, true)]
    // Three: all different.
    [InlineData(Indicted, StageBasis.Keyword, new[] { Convicted, Alleged }, Unknown, StageBasis.VoteSplit, true)]
    public void The_vote_follows_its_table(LegalStage rules, string? rulesBasis, LegalStage[] models, LegalStage stage, string? basis, bool disagree) =>
        Assert.Equal((stage, basis, disagree, false), StageVote.Decide(new(rules, rulesBasis), [.. models.Select(m => (LegalStage?)m)]));

    // The row "a configured model's answer is missing": UNKNOWN, no basis, the indicator, whatever the others read and however
    // many models are configured; the answers given only say whether the readers that answered disagree.
    [Theory]
    // One model configured, its answer missing: not the rules' stage alone.
    [InlineData(Indicted, StageBasis.Keyword, new LegalStage[0], 1, false)]
    [InlineData(Unknown, null, new LegalStage[0], 1, false)]
    // Two configured, one missing: not a "unanimous" of the two that remain, nor the rules' stage.
    [InlineData(Indicted, StageBasis.Keyword, new[] { Indicted }, 1, false)]
    [InlineData(Indicted, StageBasis.Keyword, new[] { Convicted }, 1, true)]
    // Two configured, both missing.
    [InlineData(Convicted, StageBasis.ReleaseWholeText, new LegalStage[0], 2, false)]
    public void A_missing_answer_leaves_the_stage_unknown_with_the_indicator(LegalStage rules, string? rulesBasis, LegalStage[] answered, int missing, bool disagree) =>
        Assert.Equal((Unknown, (string?)null, disagree, true),
            StageVote.Decide(new(rules, rulesBasis), [.. answered.Select(m => (LegalStage?)m), .. Enumerable.Repeat((LegalStage?)null, missing)]));

    [Fact]
    public void Without_stage_models_the_condition_is_the_rules_alone()
    {
        var c = Adverse(News);
        Assert.Equal((Indicted, StageBasis.Keyword), (c.LegalStage, c.LegalStageBasis));
        Assert.Null(c.StageReaders);
        Assert.Empty(c.ReasonCodes);
    }

    private sealed class Throwing(string name, Exception e) : IStageReader
    {
        public string Name => name;
        public StageAnswer? Read(string text, IReadOnlyList<string> names) => throw e;
    }

    // A model fails by answering nothing or by throwing (a timeout is a cancellation); either way its answer is missing
    // (ADR-0012): it is listed without a stage, the stage is UNKNOWN without a basis, and the condition carries the indicator.
    // The classifier answers (here differing from the rules), fails too, or is not configured (the llm the only model).
    [Theory]
    [InlineData("null", "fails")]
    [InlineData("throw", "fails")]
    [InlineData("timeout", "fails")]
    [InlineData("null", "answers")]
    [InlineData("throw", "answers")]
    [InlineData("timeout", "answers")]
    [InlineData("null", "absent")]
    [InlineData("timeout", "absent")]
    public void A_model_that_does_not_answer_is_named_without_a_stage_and_the_stage_is_unknown(string failure, string classifier)
    {
        IStageReader Failing(string name) => failure switch
        {
            "throw" => new Throwing(name, new InvalidOperationException("model failed")),
            "timeout" => new Throwing(name, new TaskCanceledException("model timed out")),
            _ => new Reader(name, null),
        };
        IStageReader[] models = classifier switch
        {
            "answers" => [new Reader("classifier", new(Convicted)), Failing("llm")],
            "fails" => [Failing("classifier"), Failing("llm")],
            _ => [Failing("llm")],
        };
        var c = Adverse(News, models);
        Assert.Equal((Unknown, (string?)null), (c.LegalStage, c.LegalStageBasis));
        StageReaderResult[] readers = classifier switch
        {
            "answers" => [new("rules", Indicted, StageBasis.Keyword), new("classifier", Convicted), new("llm", null)],
            "fails" => [new("rules", Indicted, StageBasis.Keyword), new("classifier", null), new("llm", null)],
            _ => [new("rules", Indicted, StageBasis.Keyword), new("llm", null)],
        };
        Assert.Equal(readers, c.StageReaders!);
        Assert.Equal(classifier == "answers" ? [StageVote.ReadersDisagree, StageVote.ReaderNoAnswer] : [StageVote.ReaderNoAnswer], c.ReasonCodes);
    }

    // Configured on purpose with one model that answers: today's row, the rules' stage stands and a difference orders review.
    [Fact]
    public void One_configured_model_that_answers_leaves_the_rules_stage()
    {
        var c = Adverse(News, new Reader("llm", new(Convicted)));
        Assert.Equal((Indicted, StageBasis.Keyword), (c.LegalStage, c.LegalStageBasis));
        Assert.Equal([StageVote.ReadersDisagree], c.ReasonCodes);
    }

    [Fact]
    public void Every_reader_is_recorded_with_the_quote_and_whether_the_text_holds_it()
    {
        var c = Adverse(News, new Reader("classifier", new(Convicted)), new Reader("llm", new(Alleged, "檢方 今日起訴")));
        Assert.Equal((Unknown, StageBasis.VoteSplit), (c.LegalStage, c.LegalStageBasis));
        Assert.Equal([StageVote.ReadersDisagree], c.ReasonCodes);
        Assert.Equal([new("rules", Indicted, StageBasis.Keyword), new("classifier", Convicted), new StageReaderResult("llm", Alleged, Quote: "檢方 今日起訴", QuoteInText: true)],
            c.StageReaders!);
        // Without whitespace the quote is in the text or it is not; an empty quote is none.
        Assert.False(Adverse(News, new Reader("llm", new(Indicted, "檢方明日起訴"))).StageReaders![1].QuoteInText);
        Assert.Equal(new StageReaderResult("llm", Indicted), Adverse(News, new Reader("llm", new(Indicted, ""))).StageReaders![1]);
    }

    // The 負面新聞 flag sets the adverse-media condition's status; the model is asked whatever it is, and a missing answer is
    // marked on the condition every time. Only a raised condition (HIT or UNKNOWN) makes a batch job retry: a NOT_HIT
    // condition's stage is one no reviewer acts on (pipeline-api-and-batch.md#durable-batch-execution).
    [Theory]
    [InlineData(FlagValue.Yes, ConditionStatus.Hit, new[] { StageVote.ReaderNoAnswer }, true)]
    [InlineData(FlagValue.Unparseable, ConditionStatus.Unknown, new[] { StageVote.ReaderNoAnswer, SourceConditions.FlagUnparseable, SourceConditions.FlagCategoryConflict }, true)]
    [InlineData(FlagValue.No, ConditionStatus.NotHit, new[] { StageVote.ReaderNoAnswer, SourceConditions.FlagCategoryConflict }, false)]
    public void Only_a_missing_answer_on_a_raised_condition_holds_a_batch_write_back(FlagValue adverseNews, ConditionStatus status, string[] codes, bool retry)
    {
        var reader = new Reader("llm", null);
        var subject = new ScreenedSubject(SubjectRole.Customer, null, ["陳志明"], Core.Shared.Gender.Male, new Core.Shared.PartialDate(1980, 5, 20), ["TW"], null, null, null, null, [], []);
        var a = Core.Cases.CaseAnalyzer.Analyze(new CaseInput(subject,
            [new CandidateRow("陳志明", "1", null, false, new ListFlags(FlagValue.Missing, FlagValue.Missing, adverseNews))],
            new Dictionary<string, SourceProfile> { ["1"] = Person }, new Dictionary<string, NewsItem> { ["1"] = new("1", null, "t", null, null, News) }, []),
            stageReaders: [reader]);

        var c = Assert.Single(Assert.Single(a.Hits).Conditions);
        Assert.Equal((status, Unknown, 1), (c.Status, c.LegalStage, reader.Calls));
        Assert.Equal(codes, c.ReasonCodes);
        Assert.Equal(retry, Core.Cases.CaseLifecycle.StageAnswerMissing(a));
    }

    [Fact]
    public void Without_news_no_model_is_asked()
    {
        var reader = new Reader("llm", new(Convicted));
        var c = Adverse(null, reader);
        Assert.Equal((Unknown, null, 0), (c.LegalStage, c.LegalStageBasis, reader.Calls));
        Assert.Null(c.StageReaders);
    }
}
