using System.Text.RegularExpressions;

namespace Aml.Core.Screening.Stage;

/// <summary>
/// What a report's clauses mean for the person's stage: the transitions of <see cref="LegalStageReading"/>, each one way a
/// clause moves the stage, and the closing decision once the text is read. Each reads named patterns
/// (<see cref="StagePatterns"/>) and what <see cref="ReportForm"/> found.
/// </summary>
internal static class ReportRules
{
    // ---- The transitions, in the order of LegalStageReading.Transitions ----

    // Someone a judgment names who is not its defendant gets a stage only from the clause naming them:
    // 被告依王大明指示…，量處被告有期徒刑七年 sentences the defendant, 檢察官起訴被告…，其中王大明之陳述… indicts the defendant.
    // A co-defendant's clause (同案的李小四則獲判無罪) tells their outcome, not the person's. A text that never writes the
    // person's names (a press release's 張姓男子) is about them as a whole: its 提起公訴 is theirs.
    internal static LegalStage? KeywordStage(ClauseScope c) => c.Other ? null
        : LastStageIn(c.Clause, c.Sentence.Attributable && (!c.Bystander || c.Mine), c.Sentence.Own && (!c.Bystander || c.Mine),
            c.Unnamed && c.Sentence.Attributable);

    // ---- What the report says of the person as a whole ----

    // A judgment names many people besides its defendants (witnesses, account holders, accomplices tried elsewhere):
    // for them its subjectless 起訴 is the defendants'.
    internal static bool Bystander(RulingResult ruling) => ruling.Judgment && !ruling.Defendant;

    // A sentence naming the person says they are under suspicion or names a step; a list of names ending 等9名 is the suspects'.
    internal static bool SaysSuspected(SentenceScope sentence) =>
        sentence.Own && (StagePatterns.Suspicion().IsMatch(sentence.Text) || StagePatterns.Group().IsMatch(sentence.Text));

    /// <summary>
    /// The closing decision, once every clause is read: a ruling's order is its outcome; with no stage read, an
    /// investigation the text tells is the person's (ALLEGED); else what the clauses read.
    /// </summary>
    internal static StageReading Closing(RulingResult ruling, LegalStage stage, string? basis, bool bystander, bool suspected, string news, IReadOnlyList<string> names)
    {
        if (ruling.Stage is { } outcome) return new(outcome, ruling.Basis); // a judgment's order is its outcome
        // 偵辦 or 調查 somewhere in the text is the person's when a sentence naming them says they are under suspicion or
        // names a step (涉嫌, 約談, 移送 …); someone the text names only as a counterpart in the facts is not ALLEGED by it.
        // A text that never writes the Profile's names is about them as a whole.
        var underInvestigation = !bystander && (suspected || !StageText.MentionsAnyName(news, names))
            && StageText.ContainsAny(news, StageGlossary.Investigation);
        return stage == LegalStage.Unknown && underInvestigation ? new(LegalStage.Alleged, StageBasis.UnderInvestigation) : new(stage, basis);
    }

    // ---- Reading stage keywords in a clause ----

    // The last stage keyword of the clause: a dismissal or indictment where the clause may be the person's, 提起公訴 (a
    // judgment's word for the indictment, and a judgment names many people), a conviction or an acquittal only in a
    // sentence naming them.
    private static LegalStage? LastStageIn(string clause, bool attributable, bool own, bool unnamed)
    {
        var found = new List<(int End, LegalStage Stage)>();
        if (attributable)
        {
            Find(found, clause, StagePatterns.Dismissal(), LegalStage.Dismissed);
            Find(found, clause, StagePatterns.Indictment(), LegalStage.Indicted);
        }
        if (own || unnamed) Find(found, clause, StagePatterns.PublicProsecution(), LegalStage.Indicted);
        if (own)
        {
            Find(found, clause, StagePatterns.Conviction(), LegalStage.Convicted);
            Find(found, clause, StagePatterns.Acquittal(), LegalStage.Acquitted);
        }
        return found.Count > 0 ? found.MaxBy(f => f.End).Stage : null;
    }

    private static void Find(List<(int, LegalStage)> found, string clause, Regex pattern, LegalStage stage)
    {
        foreach (Match m in pattern.Matches(clause)) found.Add((m.Index + m.Length, stage));
    }
}
