namespace Aml.Core.Screening.Stage;

/// <summary>One way a text tells a person's stage, under the code the catalog explains it by: the stage, or null when this rule does not speak.</summary>
internal sealed record StageRule<T>(string Code, Func<T, LegalStage?> Read);

/// <summary>The codes of the rules that give a legal stage (kind LEGAL_STAGE_BASIS in /api/codes), shared by the rules, the catalog and the tests.</summary>
internal static class StageBasis
{
    // RulingReading.Readers, in their order.
    internal const string OrderUnderHeading = "STAGE_ORDER_UNDER_HEADING";
    internal const string ReleaseWholeText = "STAGE_RELEASE_WHOLE_TEXT";
    internal const string ProceedsConfiscated = "STAGE_PROCEEDS_CONFISCATED";
    internal const string SentencedByRowNumber = "STAGE_SENTENCED_BY_ROW_NUMBER";

    // LegalStageReading.Transitions, in their order.
    internal const string Keyword = "STAGE_KEYWORD";
    internal const string WantedNotice = "STAGE_WANTED_NOTICE";

    // ReportRules.Closing, the closing decision of LegalStageReading.FromNews.
    internal const string UnderInvestigation = "STAGE_UNDER_INVESTIGATION";

    // Outside news: SourceConditions.Shape, the active official wanted list.
    internal const string OfficialWantedList = "STAGE_OFFICIAL_WANTED_LIST";

    // StageVote.Decide: the rules and two stage models vote.
    internal const string VoteUnanimous = "STAGE_VOTE_UNANIMOUS";
    internal const string VoteMajority = "STAGE_VOTE_MAJORITY";
    internal const string VoteSplit = "STAGE_VOTE_SPLIT";
}

/// <summary>
/// The stage a news text tells of the person, and the code of the rule that gave it: from the rules alone, null exactly
/// when the stage is UNKNOWN; from the vote of three readers, a STAGE_VOTE_* code also with UNKNOWN (all three on
/// UNKNOWN, or any vote short of unanimity).
/// </summary>
internal readonly record struct StageReading(LegalStage Stage, string? Basis);

/// <summary>
/// What a ruling says of the person: whether the text is a judgment or release; the person's stage by it (null: it does
/// not say); whether they are a defendant of it; the code of the reader that gave the stage (null when none spoke).
/// </summary>
internal readonly record struct RulingResult(bool Judgment, LegalStage? Stage, bool Defendant, string? Basis);

/// <summary>A judgment or a court's release of its ruling, read once (<see cref="RulingForm.Parse"/>) into what every ruling rule needs.</summary>
internal sealed class RulingText
{
    /// <summary>A court's press release (no party lines, a heading of its own or none); else a judgment with 主文.</summary>
    public bool Release { get; init; }

    /// <summary>The text has a heading for the order.</summary>
    public bool HasHeading { get; init; }

    /// <summary>The lines under the heading, to the order's end; of a release with no heading, all its lines.</summary>
    public string[] Block { get; init; } = [];

    /// <summary>The text's lines, 400 at most.</summary>
    public string[] All { get; init; } = [];

    /// <summary>The person's names; with 被告 when they are the only defendant.</summary>
    public List<string> Own { get; init; } = [];

    public bool Defendant { get; init; }

    private string? orderText, wholeText;

    /// <summary>The order's lines as one text (<see cref="RulingForm.Text"/>), made once.</summary>
    public string OrderText => orderText ??= RulingForm.Text(this, Block);

    /// <summary>All the lines as one text, made once.</summary>
    public string WholeText => wholeText ??= RulingForm.Text(this, All);
}

/// <summary>What a sentence of the report says of the person, and whose steps it may tell (<see cref="ReportForm.Scope"/>).</summary>
internal readonly record struct SentenceScope(string Text, bool Own, bool Attributable);

/// <summary>
/// A clause of the report; <see cref="Mine"/> says it names the person; <see cref="Stage"/> is the stage read so far, and
/// <see cref="StageOwn"/> says a sentence naming the person gave it; <see cref="Named"/> says this clause or one before it
/// in the sentence is the person's.
/// </summary>
internal readonly record struct ClauseScope(string Clause, bool Mine, SentenceScope Sentence, bool Bystander, LegalStage Stage, bool StageOwn, bool Named);
