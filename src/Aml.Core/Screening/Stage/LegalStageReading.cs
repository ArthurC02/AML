namespace Aml.Core.Screening.Stage;

/// <summary>
/// The legal stage that attached news tells of the Profile person. <see cref="ReportForm.Prepare"/> reads the text once
/// (quoted speech, another person's bracketed asides and reported speech blanked out; the names marked out where they
/// stand for a relative or a bystander); the text is then read sentence by sentence and clause by clause, and in a clause
/// that is the person's the <see cref="Transitions"/> are the rules (<see cref="ReportRules"/>), each one way a clause
/// moves the stage; the first that speaks gives the new stage. A judgment's or court release's order
/// (<see cref="RulingReading"/>) wins over what the clauses tell (<see cref="ReportRules.Closing"/>). A new way a report
/// tells a stage is a new transition and its place in that list, or a new pattern.
/// </summary>
/// <remarks>
/// ponytail: keyword heuristic on attached news. Replace with approved legal-stage extraction when judgment wording
/// appears in a Golden Case.
/// - A judgment (conviction, acquittal) counts only in a sentence naming the Profile person: co-defendants' sentences
///   are not theirs (GC-12).
/// - A dismissal or indictment counts in such a sentence, or in one that points at nobody else: a subjectless 檢方已起訴
///   or a collective 起訴七人 (GC-12) is the person's; 同案, 另一, 其他 … mark someone else. Negation goes first:
///   不起訴, 緩起訴 and 撤回起訴 are DISMISSED, never INDICTED.
/// - A modal, request, plan, condition or hearsay before the keyword in its clause (可, 恐, 求, 最重, 推定, 擬, 預計, 即將,
///   將會, 若, 如果, 一旦, 據傳) is no stage: 最重可判處, 請求從重判處, 恐遭起訴, 擬起訴. Words that only hold those
///   characters (恐嚇, 恐怖, 恐龍, 可疑, 可是, 許可, 求償, 要求, 模擬, 若干) are not modals.
/// - A negation or question just before the keyword (未, 沒有, 並未, 尚未, 從未, 不會, 無, 是否, with 被/遭/受 between)
///   is no stage: 尚未起訴, 沒有被起訴, 是否起訴. 不予起訴 and 免予起訴 are DISMISSED.
/// - Quoted speech (「…」, 『…』, “…”) is a claim, not a report: no stage is read inside it, but names still are.
/// - A clause whose own subject is another person is not the Profile person's, even in a sentence that names them:
///   王大明與李四涉詐欺，李四遭起訴. The clause must open (past 而, 但, 隨後 …) with a 2 or 3 character name of a common
///   surname other than the person's own, or "X姓男子", right before 遭, 被 or 獲 (李四遭, 林姓男子被); 王男遭 for 王大明
///   is the person, and 施用毒品遭 or 他曾涉案遭 open with no name.
///   無罪 is an acquittal only as a judgment's word (see Acquittal; 堅稱無罪 is a claim).
/// - The last stage in the text wins; 定讞 alone settles what came before, and 通緝 is WANTED only while no judicial
///   stage is told. 偵辦 or 調查 give ALLEGED only when there is no stage.
/// The full rules, with examples, are in docs/feature/watchlist-and-adverse-information.md (Stage from attached news).
/// </remarks>
internal static class LegalStageReading
{
    internal static StageReading FromNews(string? news, IReadOnlyList<string> names)
    {
        if (news is null) return new(LegalStage.Unknown, null);
        var stage = LegalStage.Unknown;
        string? basis = null; // the code of the transition that last set the stage
        var text = ReportForm.Prepare(news, names);
        var ruling = RulingReading.Read(news, names);
        var (judgment, sole) = (ruling.Judgment, ruling.Sole);
        var suspected = false;
        var bystander = ReportRules.Bystander(ruling);
        foreach (var (start, length) in StageText.Spans(text.Blanked, 0, text.Blanked.Length, StageText.SentenceEnds))
        {
            var sentence = ReportForm.Scope(text, start, length, judgment, bystander, sole);
            suspected |= ReportRules.SaysSuspected(sentence);
            // 佯稱被害人涉案，已遭通緝: what a fraudster told the victim, to the end of that clause and through the next one.
            // No further: the sentence may go on to the fraudster's own arrest and indictment.
            var claimed = false;
            var theirPart = false; // the clause before this one named the person and what they are charged with (…所涉犯嫌)
            foreach (var (at, size) in StageText.Spans(text.Blanked, start, length, StageText.ClauseEnds))
            {
                var clause = text.Blanked.Substring(at, size);
                if (claimed) { claimed = false; if (!ReportForm.OpensPassive(clause)) continue; }
                if (ReportForm.PretenceStart(clause) is >= 0 and var cut) (claimed, clause) = (true, clause[..cut]);
                if (bystander) clause = ReportForm.BlankIndictmentPapers(clause);
                var mine = ReportForm.IsMine(text, at, size, clause, names, ref theirPart);
                if (!mine && ReportForm.SpeaksOfAnother(text, at, size, clause, news)) continue;
                if (DecisionList.FirstOf(Transitions, new ClauseScope(clause, mine, sentence, sole, bystander, names, stage)) is { } next) (stage, basis) = next;
            }
        }
        return ReportRules.Closing(ruling, stage, basis, bystander, suspected, text, names);
    }

    // The order of this list is the precedence: a stage keyword of the clause first; the others only where it has none.
    private static readonly StageRule<ClauseScope>[] Transitions =
    [
        new(StageBasis.Keyword, ReportRules.KeywordStage), new(StageBasis.SentenceOpeningClause, ReportRules.SentencedOpening),
        new(StageBasis.FinalSettles, ReportRules.FinalSettles), new(StageBasis.WantedNotice, ReportRules.WantedWhileNoStage),
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
