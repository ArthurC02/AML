namespace Aml.Core.Screening.Stage;

/// <summary>
/// The legal stage that attached news tells of the Profile person. The text is read sentence by sentence and clause by
/// clause, and in each clause the <see cref="Transitions"/> are the rules (<see cref="ReportRules"/>), each one way a clause
/// moves the stage; the first that speaks gives the new stage. A judgment's or court release's order (<see cref="RulingReading"/>) wins
/// over what the clauses tell (<see cref="ReportRules.Closing"/>). A new way a report tells a stage is a new transition
/// and its place in that list, or a new pattern.
/// </summary>
/// <remarks>
/// ponytail: keyword heuristic on attached news, kept to the rules the vote with the stage models needs
/// (watchlist-and-adverse-information.md, Stage from attached news). Replace with approved legal-stage extraction when
/// judgment wording appears in a Golden Case.
/// - A judgment (conviction, acquittal) and 提起公訴 count only in a sentence naming the Profile person: co-defendants'
///   sentences are not theirs (GC-12).
/// - A dismissal or indictment counts in such a sentence, or in any other outside a judgment's bystander: a subjectless 檢方已起訴 or a collective
///   起訴七人 (GC-12) is the person's. 不起訴, 緩起訴, 撤回起訴 and 撤回公訴 are DISMISSED, never INDICTED.
/// - Someone a judgment names who is not its defendant gets a stage only from the clause naming them.
/// - The last stage in the text wins; 通緝 in a report is no stage. 偵辦 or 調查 give ALLEGED only when there is no stage.
/// </remarks>
internal static class LegalStageReading
{
    internal static StageReading FromNews(string? news, IReadOnlyList<string> names)
    {
        if (news is null) return new(LegalStage.Unknown, null);
        var stage = LegalStage.Unknown;
        string? basis = null; // the code of the transition that last set the stage
        var ruling = RulingReading.Read(news, names);
        var suspected = false;
        var bystander = ReportRules.Bystander(ruling);
        foreach (var (start, length) in StageText.Spans(news, 0, news.Length, StageText.SentenceEnds))
        {
            var sentence = ReportForm.Scope(news, start, length, names, bystander);
            suspected |= ReportRules.SaysSuspected(sentence);
            foreach (var (at, size) in StageText.Spans(news, start, length, StageText.ClauseEnds))
            {
                var clause = news.Substring(at, size);
                var scope = new ClauseScope(clause, ReportForm.IsMine(clause, names), sentence, bystander);
                if (DecisionList.FirstOf(Transitions, scope) is { } next) (stage, basis) = (next.Stage, next.Code);
            }
        }
        return ReportRules.Closing(ruling, stage, basis, bystander, suspected, news, names);
    }

    // The order of this list is the precedence.
    private static readonly StageRule<ClauseScope>[] Transitions =
    [
        new(StageBasis.Keyword, ReportRules.KeywordStage),
    ];
}

/// <summary>A decision list of stage rules, as both readers run theirs.</summary>
internal static class DecisionList
{
    /// <summary>The first rule that speaks, in the list's order, with its code.</summary>
    internal static (LegalStage Stage, string Code)? FirstOf<T>(StageRule<T>[] rules, T text)
    {
        foreach (var rule in rules)
            if (rule.Read(text) is { } stage) return (stage, rule.Code);
        return null;
    }

    /// <summary>
    /// What every rule of the list says (null: it does not speak), in the list's order. For measuring rules and finding
    /// texts where two rules disagree; decisions use <see cref="FirstOf{T}"/>.
    /// </summary>
    internal static IReadOnlyList<(string Code, LegalStage? Stage)> AllOf<T>(StageRule<T>[] rules, T text) =>
        [.. rules.Select(r => (r.Code, r.Read(text)))];
}
