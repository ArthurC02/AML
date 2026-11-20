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

    // Someone a judgment names who is not its defendant gets a conviction or acquittal only from the clause
    // naming them: 被告依王大明指示…，量處被告有期徒刑七年 sentences the defendant.
    internal static LegalStage? KeywordStage(ClauseScope c) =>
        LastStageIn(c.Clause, c.Sentence.Attributable, c.Sentence.Own && (!c.Bystander || c.Mine));

    // 處有期徒刑二年 opening a clause is the person's when the judgment has no other defendant, or when the
    // sentence says whom the court ruled on: 判決被告王大明犯詐欺罪，處有期徒刑二年.
    internal static LegalStage? SentencedOpening(ClauseScope c) =>
        c.Sentence.Own && Says(c.Clause, StagePatterns.Sentenced()) && (c.Sole || c.Names.Take(StageGlossary.MaxNames).Any(n => n.Length > 1
            && RuledOn(c.Sentence.Text, n))) ? LegalStage.Convicted : null;

    // 定讞 alone settles what came before: an indictment (or nothing said) ends in a conviction, while
    // 不起訴處分，全案定讞 and 判決無罪，全案定讞 stay what they are.
    // 共犯李小四日前定讞 or 另案定讞 settles someone or something else.
    internal static LegalStage? FinalSettles(ClauseScope c) =>
        c.Sentence.Own && c.Stage is LegalStage.Unknown or LegalStage.Indicted && Says(c.Clause, StagePatterns.Final())
            && !StageText.ContainsAny(c.Clause, StageGlossary.OtherParty) && !c.Clause.Contains(StageGlossary.AnotherCase, StringComparison.Ordinal) ? LegalStage.Convicted : null;

    // A wanted notice is a stage only until a judicial one is told, before or after it (起訴後棄保遭通緝 stays INDICTED).
    internal static LegalStage? WantedWhileNoStage(ClauseScope c) =>
        (c.Sentence.Own || c.Sentence.Collective) && c.Stage == LegalStage.Unknown && Says(c.Clause, StagePatterns.WantedNotice()) ? LegalStage.Wanted : null;

    // ---- What the report says of the person as a whole ----

    // A judgment names many people besides its defendants (witnesses, account holders, accomplices tried elsewhere):
    // for them its subjectless 起訴 is the defendants', and 起訴書, 起訴範圍 and 起訴意旨 are the case's papers.
    internal static bool Bystander(RulingResult ruling) => ruling.Judgment && !ruling.Defendant;

    // A sentence naming the person says they are under suspicion or names a step; a list of names ending 等9名 is the suspects'.
    internal static bool SaysSuspected(SentenceScope sentence) =>
        sentence.Own && (StagePatterns.Suspicion().IsMatch(sentence.Text) || StagePatterns.Group().IsMatch(sentence.Text));

    /// <summary>
    /// The closing decision, once every clause is read: a ruling's order is its outcome; a listed defendant with no stage
    /// read stands indicted; with no stage read, an investigation the text tells is the person's (ALLEGED); else what the
    /// clauses read.
    /// </summary>
    internal static StageReading Closing(RulingResult ruling, LegalStage stage, string? basis, bool bystander, bool suspected, ReportText text, IReadOnlyList<string> names)
    {
        if (ruling.Stage is { } outcome) return new(outcome, ruling.Basis); // a judgment's order is its outcome
        if (ruling.Defendant && stage == LegalStage.Unknown) return new(LegalStage.Indicted, StageBasis.ListedDefendant); // a listed defendant stands indicted, whatever the order goes on to say
        // 偵辦 or 調查 somewhere in the text is the person's when a sentence naming them says they are under suspicion or
        // names a step (涉嫌, 約談, 移送 …); someone the text names only as a counterpart in the facts is not ALLEGED by it.
        // A text that never writes the Profile's names is about them as a whole.
        var underInvestigation = !bystander && (suspected || !StageText.MentionsAnyName(text.Named, names))
            && StageText.ContainsAny(text.Blanked, StageGlossary.Investigation);
        return stage == LegalStage.Unknown && underInvestigation ? new(LegalStage.Alleged, StageBasis.UnderInvestigation) : new(stage, basis);
    }

    // ---- Reading stage keywords in a clause ----

    private static LegalStage? LastStageIn(string clause, bool attributable, bool own)
    {
        var found = new List<(int End, LegalStage Stage)>();
        var modalEnd = StageText.FirstModalEnd(clause); // once per clause: a modal ending at or before a keyword qualifies it
        if (attributable)
        {
            Find(found, clause, StagePatterns.Dismissal(), LegalStage.Dismissed, modalEnd);
            Find(found, clause, StagePatterns.Indictment(), LegalStage.Indicted, modalEnd);
        }
        if (own)
        {
            // 提起公訴 is a judgment's word for the indictment, and a judgment names many people: only with the person's name.
            // Tried and left out: reading 對李小四等4人提起公訴 in a report for everyone the text names. On the second dev
            // set it gave the indictment to five people the group did not hold and to three it did.
            Find(found, clause, StagePatterns.PublicProsecution(), LegalStage.Indicted, modalEnd);
            Find(found, clause, StagePatterns.Conviction(), LegalStage.Convicted, modalEnd);
            Find(found, clause, StagePatterns.Acquittal(), LegalStage.Acquitted, modalEnd);
        }
        return found.Count > 0 ? found.MaxBy(f => f.End).Stage : null;
    }

    private static bool Says(string clause, Regex pattern)
    {
        var found = new List<(int, LegalStage)>();
        Find(found, clause, pattern, LegalStage.Unknown, StageText.FirstModalEnd(clause));
        return found.Count > 0;
    }

    // A keyword counts unless a modal ends at or before it, or a negation stands right before it.
    private static void Find(List<(int, LegalStage)> found, string clause, Regex pattern, LegalStage stage, int modalEnd)
    {
        foreach (Match m in pattern.Matches(clause))
            if (modalEnd > m.Index && !StageText.Negated(clause, m.Index)) found.Add((m.Index + m.Length, stage));
    }

    // 判決被告王大明…, 判決王大明…: the sentence says the court ruled on this name.
    private static bool RuledOn(string sentence, string name)
    {
        foreach (var before in StageGlossary.RuledOn)
            if (sentence.Contains(before + name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
