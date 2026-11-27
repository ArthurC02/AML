using System.Text.RegularExpressions;

namespace Aml.Core.Screening.Stage;

/// <summary>
/// What the forms of a ruling mean for the person's stage: the readers of <see cref="RulingReading"/>, each one way a
/// judgment or court release tells a person's outcome. Each reads named patterns (<see cref="StagePatterns"/>) and what
/// <see cref="RulingForm"/> found, and gives the stage, or null when it does not speak.
/// </summary>
internal static class RulingRules
{
    // ---- The readers, in the order of RulingReading.Readers ----

    // What stands under the heading is the outcome.
    internal static LegalStage? OrderUnderHeading(RulingText t) => Outcome(t, t.Block);

    // A release that says nothing of the person under its heading is read whole: it also gives the outcome in its
    // opening and its reasons, in the order's own form. (One with no heading was read whole already.)
    internal static LegalStage? WholeRelease(RulingText t) => t.Release && t.HasHeading ? Outcome(t, t.All) : null;

    // 張文華犯罪所得新臺幣五萬元…沒收 under the heading: proceeds of crime are taken from the person convicted of it,
    // also where the release's order rules on nothing else of theirs (an appeal on the confiscation alone).
    internal static LegalStage? ProceedsConfiscated(RulingText t) =>
        t.Release && t.HasHeading && StageText.Sentences(t.OrderText)
            .Any(s => StageText.ContainsAny(s, StageGlossary.Confiscation) && !StagePatterns.NotTheirProceeds().IsMatch(s)
                && t.Own.Exists(n => n != StageGlossary.Defendant && ProceedsOf(s, n)))
            ? LegalStage.Convicted : null;

    // A large case numbers its defendants and sentences them by number: 王大明等15人各犯…罪，各處有期徒刑7年（編號1）、
    // 5年（編號2）… in the order, 李小四（編號2） in the facts. The person's number in a sentence of the order that
    // sentences is their conviction. Only a bracketed number under the heading counts (not 附表編號2 of an exhibit),
    // and not in a sentence that also acquits or speaks of the court below or an appeal.
    internal static LegalStage? SentencedByRowNumber(RulingText t)
    {
        if (!t.Release) return null;
        var numbers = RulingForm.AfterNames(t).Select(i => StagePatterns.RowNumber().Match(t.WholeText, i)).Where(m => m.Success).Select(m => Number(m.Groups[1])).ToHashSet();
        return numbers.Count > 0 && StageText.Sentences(t.HasHeading ? t.OrderText : t.WholeText)
            .Where(s => StagePatterns.OrderSentence().IsMatch(s) && !StagePatterns.NotThisOrder().IsMatch(s)).SelectMany(s => StagePatterns.NumberedRows().Matches(s))
            .Any(r => Number(r.Groups[1]) is var low && (r.Groups[2].Success ? Number(r.Groups[2]) : low) is var high && numbers.Any(n => n >= low && n <= high))
            ? LegalStage.Convicted : null;

        static int Number(Group g) => int.Parse(g.Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    // ---- What a sentence of the order's form says of the person ----

    // Sentence by sentence, what the rows say of the person (RulingForm.SaidOfPerson): a sentence in any part makes
    // CONVICTED, else an acquittal ACQUITTED.
    private static LegalStage? Outcome(RulingText t, string[] rows)
    {
        LegalStage? read = null;
        foreach (var said in RulingForm.SaidOfPerson(t, rows))
        {
            if (StagePatterns.OrderSentence().IsMatch(said)) return LegalStage.Convicted;
            // 原判決關於X沒收部分、Y無罪部分均撤銷 names a part of the judgment below, not an outcome.
            // 張文華無罪部分，檢察官上訴駁回 upholds the acquittal and stays one.
            if (StagePatterns.RevokedAcquittal().Replace(said, "").Contains(StageGlossary.Acquitted, StringComparison.Ordinal)) read = LegalStage.Acquitted;
        }
        return read;
    }

    // 張文華犯罪所得, 張文華之犯罪所得: the proceeds are the named person's.
    private static bool ProceedsOf(string sentence, string name)
    {
        foreach (var after in StageGlossary.ProceedsAfterName)
            if (sentence.Contains(name + after, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
