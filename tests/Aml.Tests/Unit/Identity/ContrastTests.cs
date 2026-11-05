using Aml.Core.Identity;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Tests.Unit.Identity;

/// <summary>wouldChangeIf: stage 3 re-run with a document or full DOB assumed to agree or conflict.</summary>
public class ContrastTests
{
    private static readonly PartialDate Born = new(1980, 5, 20);

    private static ScreenedSubject Subject(PartialDate? dob = null, Document? document = null, IReadOnlyList<Employment>? jobs = null) =>
        new(SubjectRole.Customer, null, ["陳志明"], Gender.Male, dob ?? Born, ["TW"], document, null, null, null, [], [], jobs);

    private static SourceProfile Profile(PartialDate[]? dobs = null, Gender gender = Gender.Male) =>
        new("DJ:1", gender, ["陳志明"], dobs ?? [], ["TW"], [], [new SourceCategory("Special Interest Person (SIP)", "Fraud")],
            [], [], [], null, [], []);

    private static void Check(int row, string[] expected, ScreenedSubject s, SourceProfile p)
    {
        var r = IdentityDecisionPolicy.Decide(s, p);
        Assert.Equal(row, r.Stage3Row);
        Assert.Equal(expected, IdentityDecisionPolicy.Contrast(r).Select(c => $"{c.Field}:{c.Assumption}→{c.Stage3Row}:{c.ReasonCode}"));
    }

    private const string DocAgree = $"Document:Agree→2:{ReasonCodes.UniqueIdentifierAgreement}";
    private const string DocConflict = $"Document:Conflict→1:{ReasonCodes.DocumentIdentityConflict}";
    private const string DobAgreeRow4 = $"DateOfBirth:Agree→4:{ReasonCodes.ConsistentAtAvailablePrecision}";
    private const string DobConflict = $"DateOfBirth:Conflict→1:{ReasonCodes.FullDobConflict}";

    [Fact]
    public void Row_6_lists_all_four_assumptions()
    {
        Check(6, [DocAgree, DocConflict, DobAgreeRow4, DobConflict], Subject(), Profile());
        Assert.Equal(
            [
                $"若同類證件相符：身分判斷移至第三階段第 2 列 {ReasonCodes.UniqueIdentifierAgreement}",
                $"若同類證件不符：身分判斷移至第三階段第 1 列 {ReasonCodes.DocumentIdentityConflict}",
                $"若完整出生日期相符：身分判斷移至第三階段第 4 列 {ReasonCodes.ConsistentAtAvailablePrecision}",
                $"若完整出生日期不符：身分判斷移至第三階段第 1 列 {ReasonCodes.FullDobConflict}",
            ],
            IdentityDecisionPolicy.Contrast(IdentityDecisionPolicy.Decide(Subject(), Profile())).Select(c => c.Text));
    }

    [Fact]
    public void Row_5_with_a_gender_conflict_stays_on_row_5_when_the_dob_agrees() =>
        Check(5, [DocAgree, DocConflict, DobConflict], Subject(), Profile([new(1980)], Gender.Female));

    [Fact]
    public void Row_4_leaves_out_the_assumption_that_keeps_it_on_row_4() =>
        Check(4, [DocAgree, DocConflict, DobConflict], Subject(), Profile([new(1980)]));

    [Fact]
    public void Row_3_with_a_full_dob_on_both_sides_lists_only_the_document()
    {
        var jobs = new[] { new Employment("宏遠投資", "經理", new PartialDate(2015), null) };
        var p = Profile([Born]) with { Roles = [new ProfileRole("經理", "宏遠投資", new PartialDate(2016))] };
        Check(3, [DocAgree, DocConflict], Subject(jobs: jobs), p);
    }

    public static TheoryData<string, ScreenedSubject, SourceProfile> Empty() => new()
    {
        { "row 1", Subject(), Profile([new(1970, 1, 1)]) },
        { "row 2", Subject(document: new("TW_ID", "A123456789")), Profile() with { Documents = [new("TW_ID", "A123456789")] } },
        { "hard conflict", Subject(new PartialDate(2000, 3, 1)), Profile() with { Roles = [new ProfileRole("經理", "宏遠投資", new PartialDate(2010, 1, 5))] } },
        { "non-Person", Subject(), Profile() with { RecordType = "Entity" } },
        { "scattered DOBs", Subject(), Profile([new(1980), new(1985), new(1990)]) },
    };

    [Theory]
    [MemberData(nameof(Empty))]
    public void Nothing_is_listed(string why, ScreenedSubject s, SourceProfile p)
    {
        var r = IdentityDecisionPolicy.Decide(s, p);
        Assert.True(why != "scattered DOBs" || r.Stage3Row == 6, "the scattered-DOB case must reach the DOB check, not stop on its row");
        Assert.Empty(IdentityDecisionPolicy.Contrast(r));
    }
}
