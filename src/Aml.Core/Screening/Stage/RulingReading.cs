namespace Aml.Core.Screening.Stage;

/// <summary>
/// What a judgment, or a court's press release of its ruling, says of one person. A cascade of three steps:
/// <see cref="RulingForm.Parse"/> reads the text once into what every rule needs (the kind of text, the order's lines,
/// the defendants); <see cref="Readers"/> are the rules (<see cref="RulingRules"/>), each one way a ruling tells a
/// person's outcome; the first reader that speaks gives the stage. A new form of ruling is a new reader and its place in
/// that list, or a new pattern of an existing one.
/// </summary>
/// <remarks>
/// A judgment's order (the lines from 主文 to the 事實 / 理由 heading) is written to a fixed form: who is sentenced
/// (處有期徒刑, 拘役, 罰金, 無期徒刑, 死刑, 免刑) and who is 無罪. Only what the order says of the Profile person counts:
/// where they are the subject of a clause (the name opens it, alone or in a list of names: 林美玲、王大明均無罪; never
/// 王大明對林美玲犯詐欺罪 or 原判決關於林美玲部分撤銷), up to a clause that opens with another person's name (林美玲處
/// 有期徒刑一年，王大明無罪). A sentence in any part of the order makes CONVICTED, else an acquittal ACQUITTED; an order
/// that says neither of them (免訴, 公訴不受理) gives nothing, save what the later readers tell. Whether the person is a
/// listed defendant (a 被告 line before 主文) is told as well: such a person stands indicted even when the order is not read.
/// A judgment the court anonymised calls its defendants by a code from 主文 on (A03犯…，處有期徒刑一年。A07無罪。): when the
/// header's 被告 block holds N names alone on their lines, the order writes none of them, and its sentences have N distinct
/// codes for subject (A03犯…, A03、A07均無罪; not A12 of A03與A12共同犯…), first written in ascending order, the i-th name is
/// the i-th code, and the person's code is one of their names (else no code is theirs).
/// ponytail: pending a judgment Golden Case. Not read: a judgment's order sentence that goes on about the person without
/// naming them again (又犯…罪，處…; a release's is read), a co-defendant of the same surname, and anything after the order
/// that overturns it (the order wins over the rest of the text). An order with no 事實 / 理由 heading is read for 40 lines at most.
/// </remarks>
internal static class RulingReading
{
    internal static RulingResult Read(string news, IReadOnlyList<string> names) =>
        RulingForm.Parse(news, names) is { } text && DecisionList.FirstOf(Readers, text) is var first
            ? new(true, first?.Stage, text.Defendant, text.Sole, first?.Code, text.PartyCode)
            : default;

    /// <summary>What every reader says of the person, in the order of <see cref="Readers"/>; empty when the text is no ruling.</summary>
    internal static IReadOnlyList<(string Code, LegalStage? Stage)> Trace(string news, IReadOnlyList<string> names) =>
        RulingForm.Parse(news, names) is { } text ? DecisionList.AllOf(Readers, text) : [];

    /// <inheritdoc cref="RulingForm.IsCourtRelease"/>
    internal static bool IsCourtRelease(string news) => RulingForm.IsCourtRelease(news);

    // The order of this list is the precedence: what stands under the order's heading first, then the whole release,
    // then what a dismissed appeal leaves standing, then the forms that name the person outside a sentence of the order.
    private static readonly StageRule<RulingText>[] Readers =
    [
        new(StageBasis.OrderUnderHeading, RulingRules.OrderUnderHeading), new(StageBasis.ReleaseWholeText, RulingRules.WholeRelease),
        new(StageBasis.AppealOfListedDefendant, RulingRules.AppealOfListedDefendant), new(StageBasis.AppealOfOnlyDefendant, RulingRules.AppealOfOnlyDefendant),
        new(StageBasis.AppealWithRecalledSentence, RulingRules.AppealWithRecalledSentence), new(StageBasis.ProceedsConfiscated, RulingRules.ProceedsConfiscated),
        new(StageBasis.SentencedByRowNumber, RulingRules.SentencedByRowNumber), new(StageBasis.TriedElsewhere, RulingRules.TriedElsewhereNote),
    ];
}
