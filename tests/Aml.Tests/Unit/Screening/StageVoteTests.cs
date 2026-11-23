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
        Assert.Equal((stage, basis, disagree), StageVote.Decide(new(rules, rulesBasis), models));

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

    // A model fails by answering nothing or by throwing (a timeout is a cancellation); either way it drops out (ADR-0012).
    [Theory]
    [InlineData("null")]
    [InlineData("throw")]
    [InlineData("timeout")]
    public void A_model_that_does_not_answer_leaves_the_rules_alone_and_is_not_listed(string failure)
    {
        IStageReader Failing(string name) => failure switch
        {
            "throw" => new Throwing(name, new InvalidOperationException("model failed")),
            "timeout" => new Throwing(name, new TaskCanceledException("model timed out")),
            _ => new Reader(name, null),
        };
        var c = Adverse(News, Failing("classifier"), Failing("llm"));
        Assert.Equal((Indicted, StageBasis.Keyword), (c.LegalStage, c.LegalStageBasis));
        Assert.Equal([new StageReaderResult("rules", Indicted, StageBasis.Keyword)], c.StageReaders!);
        Assert.Empty(c.ReasonCodes);
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

    [Fact]
    public void Without_news_no_model_is_asked()
    {
        var reader = new Reader("llm", new(Convicted));
        var c = Adverse(null, reader);
        Assert.Equal((Unknown, null, 0), (c.LegalStage, c.LegalStageBasis, reader.Calls));
        Assert.Null(c.StageReaders);
    }
}
