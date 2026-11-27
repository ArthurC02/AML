using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Core.Identity;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Tests.Unit.Identity;

/// <summary>
/// Native-script name conflict (identity-adjudication.md#native-script-name-conflict): romanised names agree, the complete
/// Chinese-character names differ. A signal that rules out stage 3 rows 3 and 4, never a conclusion. Invented names.
/// </summary>
public class NativeNameTests
{
    private const string Roman = "Chang Wen-Hua";

    private static ScreenedSubject Customer(string[] names, PartialDate? dob = null, Document? document = null, string? reference = null) =>
        new(SubjectRole.Customer, null, names, Gender.Male, dob ?? new PartialDate(1980, 5, 20), ["TW"], document, reference, null, null, [], []);

    private static SourceProfile Profile(string[] names, PartialDate[]? dobs = null, Document? document = null, string? reference = null) =>
        new("DJ:1", Gender.Male, names, dobs ?? [new PartialDate(1980)], ["TW"], [], [new SourceCategory("Special Interest Person (SIP)", "Fraud")],
            [], [], [], null, [], [], Documents: document is null ? null : [document], OtherIdentifiers: reference is null ? null : [new OtherIdentifier("Ref", reference)]);

    /// <summary>Customer forms | Profile forms (separated by /) | the contrasted pair, or empty for silence.</summary>
    [Theory]
    // Conflict: romanised names agree (EXACT), every complete Han form differs.
    [InlineData("張文華/Chang Wen-Hua", "Chang Wen-Hua/章文華", "張文華|章文華")]        // surname, same reading
    [InlineData("張文華/Chang Wen-Hua", "Chang Wen-Hua/張文樺", "張文華|張文樺")]        // one given character
    [InlineData("張文華/Chang Wen-Hua", "Chang Wen-Hua/張雯樺", "張文華|張雯樺")]        // both given characters
    [InlineData("張文華/Chang Wen-Hua", "Zhang Wenhua/章文華", "張文華|章文華")]         // the same reading across Wade-Giles and Pinyin (ROMANIZATION_VARIANT)
    [InlineData("張文華/Chang Wen-Hua/章大明", "Chang Wen-Hua/章文華", "張文華|章文華")]  // a customer alias that does not agree either
    // Agreement under the comparison's relaxations: no signal.
    [InlineData("張文華/Chang Wen-Hua", "Chang Wen-Hua/张文华", "")]                     // simplified, folded
    [InlineData("鍾文華/Chung Wen-Hua", "Chung Wen-Hua/鐘文華", "")]                     // controlled variant
    [InlineData("張 文華/Chang Wen-Hua", "Chang Wen-Hua/張文華", "")]                    // spacing
    [InlineData("張·文華/Chang Wen-Hua", "Chang Wen-Hua/張文華", "")]                    // middle dot
    [InlineData("文華 張/Chang Wen-Hua", "Chang Wen-Hua/張文華", "")]                    // name order
    [InlineData("張文華/Chang Wen-Hua", "Chang Wen-Hua/章文華/張文華", "")]               // one of several native forms agrees
    [InlineData("張文華/Chang Wen-Hua/章文華", "Chang Wen-Hua/章文華", "")]               // the customer's alias agrees
    // Not comparable: silent.
    [InlineData("張文華/Chang Wen-Hua", "Chang Wen-Hua", "")]                            // no Profile native form
    [InlineData("Chang Wen-Hua", "Chang Wen-Hua/章文華", "")]                            // no customer native form
    [InlineData("張文華/Chang Wen-Hua", "Chang Wen-Hua/章○華", "")]                      // masked Profile form
    [InlineData("張○華/Chang Wen-Hua", "Chang Wen-Hua/章文華", "")]                      // masked customer form
    [InlineData("張文華/Chang Wen-Hua", "Chang Wen-Hua/章?華", "")]                      // unknown character
    [InlineData("張文華/Chang Wen-Hua", "Chang Wen-Hua/チャン・ウェンホア", "")]             // kana only: another script
    [InlineData("張文華/Chang Wen-Hua", "Chang Wen-Hua/張華", "")]                       // one character left out
    [InlineData("張華/Chang Hua", "Chang Hua/張文華", "")]                               // one character added
    [InlineData("張文華/Chang Wen-Hua", "Chang Wen-Hua/章華明德", "")]                    // no pair of equal length
    [InlineData("張文華/Chang Wen-Hua", "Lin Mei-Ling/章文華", "")]                      // the romanised names do not agree
    public void The_conflict_and_its_boundaries(string customer, string profile, string expected)
    {
        var s = customer.Split('/');
        var p = profile.Split('/');
        var (status, detail) = Core.Normalization.NameComparison.CompareNames(s, p);
        var found = NativeNames.Find(s, p, status, detail);
        Assert.Equal(expected, found is var (c, n) ? $"{c}|{n}" : "");
    }

    /// <summary>A list form that may be the same character (one-to-many Simplified ↔ Traditional, variant, shinjitai) is silence, not agreement; a homophone of another character signals.</summary>
    [Theory]
    [InlineData("杰傑"), InlineData("万萬"), InlineData("团團"), InlineData("勋勳"), InlineData("广廣"), InlineData("愿願")]
    [InlineData("艳豔"), InlineData("艳艷"), InlineData("豔艷"), InlineData("涌湧"), InlineData("启啟"), InlineData("启啓"), InlineData("啟啓")]
    [InlineData("为為"), InlineData("为爲"), InlineData("為爲"), InlineData("裏裡"), InlineData("眞真"), InlineData("衞衛"), InlineData("樑梁")]
    [InlineData("淩凌"), InlineData("産產"), InlineData("滙匯"), InlineData("敎教"), InlineData("髙高"), InlineData("邨村"), InlineData("沢澤")]
    [InlineData("広廣"), InlineData("余餘"), InlineData("于於"), InlineData("云雲"), InlineData("范範"), InlineData("沈瀋"), InlineData("姜薑")]
    [InlineData("游遊"), InlineData("岳嶽"), InlineData("肖蕭"), InlineData("里裡"), InlineData("里裏"), InlineData("后後"), InlineData("松鬆")]
    [InlineData("谷穀"), InlineData("涂塗")]
    public void A_possibly_same_character_silences_the_signal(string pair)
    {
        string[] s = [$"林{pair[0]}明", "Lin Chih-Ming"], p = ["Lin Chih-Ming", $"林{pair[1]}明"];
        Assert.Null(NativeNames.Find(s, p, ComparisonStatus.Exact, null));
        Assert.Null(NativeNames.Find(p, s, ComparisonStatus.Exact, null));
        Assert.True(NativeNames.MayBeSameCharacter(new(pair[0]), new(pair[1])));
    }

    [Fact]
    public void A_homophone_that_is_no_variant_still_signals() =>
        Assert.Equal<(string, string)?>(("林志明", "林智銘"), NativeNames.Find(["林志明", "Lin Chih-Ming"], ["Lin Chih-Ming", "林智銘"], ComparisonStatus.Exact, null));

    private static readonly Document Id = new("TW_NATIONAL_ID", "A123456789");

    /// <summary>Stage 3 with the conflict: rows 3 and 4 are ruled out (rows 5 or 6, REVIEW); row 2 keeps its row; the code is recorded alongside.</summary>
    [Theory]
    [InlineData("row4", "章文華", 5, IdentityDecision.Review, new[] { "PARTIAL_SUPPORT_ONLY", "NATIVE_NAME_CONFLICT", "NO_BILATERAL_DIRECT_IDENTIFIER" })]
    [InlineData("row4", "張文華", 4, IdentityDecision.ProbableMatch, new[] { "CONSISTENT_AT_AVAILABLE_PRECISION", "NO_BILATERAL_DIRECT_IDENTIFIER" })]
    [InlineData("row3", "章文華", 5, IdentityDecision.Review, new[] { "PARTIAL_SUPPORT_ONLY", "NATIVE_NAME_CONFLICT" })]
    [InlineData("row3", "張文華", 3, IdentityDecision.ProbableMatch, new[] { "STRONG_SUPPORT_COMBINATION" })]
    [InlineData("row2", "章文華", 2, IdentityDecision.ProbableMatch, new[] { "UNIQUE_IDENTIFIER_AGREEMENT", "NATIVE_NAME_CONFLICT" })]
    [InlineData("row6", "章文華", 6, IdentityDecision.Review, new[] { "NO_NON_PREREQUISITE_SUPPORT", "NATIVE_NAME_CONFLICT", "NO_BILATERAL_DIRECT_IDENTIFIER" })]
    public void Stage_3_with_the_conflict(string shape, string profileNative, int row, IdentityDecision decision, string[] reasons)
    {
        var full = new PartialDate(1980, 5, 20);
        var (s, p) = shape switch
        {
            "row4" => (Customer(["張文華", Roman]), Profile([Roman, profileNative])),
            "row3" => (Customer(["張文華", Roman], reference: "R1"), Profile([Roman, profileNative], [full], reference: "R1")),
            "row2" => (Customer(["張文華", Roman], document: Id), Profile([Roman, profileNative], document: Id)),
            _ => (Customer(["張文華", Roman]), Profile([Roman, profileNative], [])),
        };
        var r = IdentityDecisionPolicy.Decide(s, p);
        Assert.Equal((row, decision, (string?)null), (r.Stage3Row, r.Decision, r.HardConflict));
        Assert.Equal(reasons, r.ReasonCodes);
    }

    [Fact]
    public void The_name_evidence_and_the_explanation_show_both_forms()
    {
        var s = Customer(["張文華", Roman]);
        var p = Profile([Roman, "章文華"]);
        var name = IdentityDecisionPolicy.Decide(s, p).Evidence[EvidenceGroup.Name];
        const string Note = "羅馬拼音姓名相同，但中文姓名不同：客戶姓名「張文華」、名單 DJ:1 姓名「章文華」；同音不同字可能是另一人，未自動判定，請覆核";
        Assert.Equal((Direction.Prerequisite, ComparisonStatus.Exact, ReasonCodes.NativeNameConflict, Note), (name.Direction, name.Status, name.Detail, name.Note));
        Assert.DoesNotContain(ReasonCodes.NativeNameConflict, HardConflictGate.ValidatedReasonCodes);

        var input = new CaseInput(s, [new CandidateRow(Roman, "1", null, false)], new Dictionary<string, SourceProfile> { ["1"] = p },
            new Dictionary<string, IReadOnlyList<NewsItem>>(), []);
        var a = CaseAnalyzer.Analyze(input, asOf: new DateOnly(2026, 11, 27));
        var h = Assert.Single(ExplanationBuilder.Build(input, a).Hits);
        Assert.StartsWith(Note + "（NATIVE_NAME_CONFLICT）。相符：", h.Summary.AgreementsAndDifferences, StringComparison.Ordinal);
        Assert.Equal(ReviewPriority.Normal, Assert.Single(a.Hits).ReviewPriority); // row 5
    }
}
