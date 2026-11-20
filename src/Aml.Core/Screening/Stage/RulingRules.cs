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

    // A defendant appeals a conviction only: 上訴駁回 on the appeal of an 上訴人即被告 leaves that conviction standing.
    internal static LegalStage? AppealOfListedDefendant(RulingText t) =>
        RulingWords(t).Contains(StageGlossary.DismissedAppeal, StringComparison.Ordinal) && t.Listed.Exists(l => StageText.StartsWithAny(l, StageGlossary.AppellantDefendant)) // 即被告 opens the line under 上訴人
            ? LegalStage.Convicted : null;

    // A release has no such line: its opening says its only defendant appealed (被告提起上訴, 被告不服提起上訴), and a
    // line under its heading is 上訴駁回 and nothing more.
    internal static LegalStage? AppealOfOnlyDefendant(RulingText t) =>
        t.Release && t.Sole && t.Appealed && t.HasHeading && t.Block.Any(l => StageText.Compact(l).TrimEnd('。') == StageGlossary.DismissedAppeal) ? LegalStage.Convicted : null;

    // A release's 上訴駁回 leaves standing the sentence the release recalls of the person
    // (經…法院判決「王大明犯殺人罪，處有期徒刑十年」, 判處王大明有期徒刑): a sentence of the text naming them with one.
    // ponytail: a prosecutor's dismissed appeal against an acquittal, in a release that also writes the penalty asked
    // for the person (請求判處王大明…), reads CONVICTED; tell whose appeal it was if that turns up.
    // 其他上訴駁回（即被告王大明、李小四部分） dismisses the appeals of those it names: their sentences stand.
    internal static LegalStage? AppealWithRecalledSentence(RulingText t) =>
        t.Release && StagePatterns.AppealDismissed().IsMatch(t.WholeText) && StageText.Sentences(t.WholeText)
            .Any(s => t.Own.Exists(n => s.Contains(n, StringComparison.OrdinalIgnoreCase))
                && (s.Contains(StageGlossary.SentencedWord, StringComparison.Ordinal) || StagePatterns.OrderSentence().IsMatch(s) || (StagePatterns.DismissedPart().Match(s) is { Success: true } part && t.Own.Exists(n => part.Value.Contains(n, StringComparison.OrdinalIgnoreCase)))))
            ? LegalStage.Convicted : null;

    // 陳志明犯罪所得新臺幣五萬元…沒收 under the heading: proceeds of crime are taken from the person convicted of it,
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

    // 王大明（本院審理中）, 王大明（由本院另案審理）: charged, and not ruled on here.
    internal static LegalStage? TriedElsewhereNote(RulingText t) =>
        t.Release && RulingForm.AfterNames(t).Exists(i => StagePatterns.TriedElsewhere().IsMatch(t.WholeText, i)) ? LegalStage.Indicted : null;

    // ---- What a sentence of the order's form says of the person ----

    // Sentence by sentence, what the rows say of the person (RulingForm.SaidOfPerson): a sentence in any part makes
    // CONVICTED, else an acquittal ACQUITTED. A release's rows that say nothing so are read as a table.
    private static LegalStage? Outcome(RulingText t, string[] rows)
    {
        LegalStage? read = null;
        foreach (var said in RulingForm.SaidOfPerson(t, rows))
        {
            if (StagePatterns.OrderSentence().IsMatch(said)) return LegalStage.Convicted;
            // 陳志明緩刑3年: probation is given on a conviction. Only right after the person's own name.
            if (t.Release && StagePatterns.Probation().Match(said) is { Success: true } p && t.Own.Exists(n => n != StageGlossary.Defendant && said.AsSpan(0, p.Index).EndsWith(n, StringComparison.OrdinalIgnoreCase))) return LegalStage.Convicted;
            // 原判決關於X沒收部分、Y無罪部分均撤銷 names a part of the judgment below, not an outcome.
            // 陳志明無罪部分，檢察官上訴駁回 upholds the acquittal and stays one.
            if (StagePatterns.RevokedAcquittal().Replace(said, "").Contains(StageGlossary.Acquitted, StringComparison.Ordinal)) read = LegalStage.Acquitted;
        }
        return read ?? (t.Release ? TableOutcome(rows, [.. t.Own.Where(n => n != StageGlossary.Defendant).Select(StageText.Compact)], t.Own, t.Surnames) : null);
    }

    // A release may give its defendants as a table (RulingForm.TableRows walks the person's rows). The first penalty
    // among a row's cells is theirs (a cell opening 有期徒刑, 拘役 …, or a sentence of the order's form), unless 無罪
    // comes first. Lines that are only a short name are skipped: defendants sharing one penalty cell stand one under the other.
    // ponytail: cells merged the other way round (one name, the next row's penalty) read as this row's; a table with a
    // column of names and a separate column of outcomes read column by column is not told apart.
    private static LegalStage? TableOutcome(string[] rows, List<string> own, List<string> names, HashSet<char> surnames)
    {
        var terms = RulingForm.HasTermColumn(rows);
        foreach (var cells in RulingForm.TableRows(rows, own, names, surnames))
            foreach (var cell in cells)
            {
                if (StagePatterns.TablePenalty().IsMatch(cell) || StagePatterns.OrderSentence().IsMatch(cell)) return LegalStage.Convicted;
                // A cell that says another outcome comes first: 無罪, 免訴, 不受理, 上訴駁回, 撤銷.
                if (cell.Contains(StageGlossary.Acquitted, StringComparison.Ordinal)) return LegalStage.Acquitted;
                if (cell.Length <= 10 && StagePatterns.TableOtherOutcome().IsMatch(cell)) break;
                // Under a column headed 宣告刑, 刑度 or 有期徒刑 the cell is the term alone: 1年8月、3月（得易科罰金）, 各10月, 緩刑5年.
                if (terms && StagePatterns.TableTerm().IsMatch(cell)) return LegalStage.Convicted;
            }
        return null;
    }

    // Where a dismissed appeal is looked for: a release's whole text, a judgment's order.
    private static string RulingWords(RulingText t) => t.Release ? t.WholeText : t.OrderText;

    // 陳志明犯罪所得, 陳志明之犯罪所得: the proceeds are the named person's.
    private static bool ProceedsOf(string sentence, string name)
    {
        foreach (var after in StageGlossary.ProceedsAfterName)
            if (sentence.Contains(name + after, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
