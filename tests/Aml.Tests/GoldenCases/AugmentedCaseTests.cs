using Aml.Core.Cases;
using Aml.Core.Identity;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using Aml.Infra.Txt;
using static Aml.Core.Shared.ComparisonStatus;

namespace Aml.Tests.GoldenCases;

/// <summary>
/// Augmented cases (docs/plan/golden-cases.md#augmented-cases): one controlled change to a Golden Case, aimed at a rule
/// no Golden Case covers yet. The base folder is copied, never modified. Augmented cases are not Golden Cases: they are
/// not authoritative, carry no human label and stay out of the metrics. A real case replaces one when it arrives.
/// </summary>
public class AugmentedCaseTests
{
    private const string Profile11 = "名單資料_90000011.txt";
    private const string Profile12 = "名單資料_90000012.txt";

    /// <summary>Copies the base case, applies each edit (the text must occur exactly once) and analyzes the result.</summary>
    private static (CaseInput Input, CaseAnalysis Analysis) Augment(string gc, (string File, string Find, string Replace)[] edits,
        Func<ScreenedSubject, ScreenedSubject>? subject = null)
    {
        var dir = Directory.CreateTempSubdirectory("aml-aug-");
        try
        {
            foreach (var f in Directory.GetFiles(GoldenCaseTests.Folder(gc)))
                File.Copy(f, Path.Combine(dir.FullName, Path.GetFileName(f)));
            foreach (var (file, find, replace) in edits)
            {
                var path = Path.Combine(dir.FullName, file);
                var text = File.ReadAllText(path);
                Assert.True(text.Split(find).Length == 2, $"{gc}/{file}: '{find}' must occur exactly once");
                File.WriteAllText(path, text.Replace(find, replace, StringComparison.Ordinal));
            }
            var input = TxtCaseReader.Read(dir.FullName);
            if (subject is not null) input = input with { Subject = subject(input.Subject) };
            return (input, CaseAnalyzer.Analyze(input));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static (string, string, string) Before(string file, string anchor, string inserted) => (file, anchor, inserted + "\n" + anchor);

    private static IdentityResult Identity(CaseAnalysis a, string profile = "DJ:90000011") =>
        a.Hits.Single(h => h.Profile.ProfileKey == profile).Identity;

    // GC-11: customer born 1973/6/12, TW national ID A123456789; Profile has year-only DOBs 1973 and 1972, no document.

    [Fact]
    public void AUG_11_01_same_national_id_on_the_profile_is_unique_support_row_2()
    {
        var (_, a) = Augment("GC-11", [Before(Profile11, "狀態:\tActive", "身分證明:\n編號\t類型\t號碼\t國家\n1\tNational ID\tA123456789\tTaiwan")]);
        var id = Identity(a);
        Assert.Equal((Exact, Strength.Unique), (id.Evidence[EvidenceGroup.Document].Status, id.Evidence[EvidenceGroup.Document].Strength));
        Assert.Equal((2, IdentityDecision.ProbableMatch), (id.Stage3Row, id.Decision));
        Assert.DoesNotContain(ReasonCodes.NoBilateralDirectIdentifier, id.ReasonCodes);
    }

    [Fact]
    public void AUG_11_02_different_national_id_is_a_document_conflict_row_1()
    {
        var (_, a) = Augment("GC-11", [Before(Profile11, "狀態:\tActive", "身分證明:\n編號\t類型\t號碼\t國家\n1\tNational ID\tA223456788\tTaiwan")]);
        var id = Identity(a);
        Assert.Equal((1, IdentityDecision.Review), (id.Stage3Row, id.Decision));
        Assert.Equal(ReasonCodes.DocumentIdentityConflict, id.ReasonCodes[0]);
    }

    [Fact]
    public void AUG_11_03_passport_is_not_comparable_and_blocks_row_4()
    {
        var (_, a) = Augment("GC-11", [Before(Profile11, "狀態:\tActive", "身分證明:\n編號\t類型\t號碼\t國家\n1\tPassport\t312345678\tTaiwan")]);
        var id = Identity(a);
        Assert.Equal((Uncertain, "NOT_COMPARABLE"), (id.Evidence[EvidenceGroup.Document].Status, id.Evidence[EvidenceGroup.Document].Detail));
        // Row 4 needs nothing uncertain in DOCUMENT, so the GC-11 PROBABLE_MATCH drops to row 5 REVIEW.
        Assert.Equal((5, IdentityDecision.Review), (id.Stage3Row, id.Decision));
    }

    [Fact]
    public void AUG_11_04_full_dob_agreement_is_a_bilateral_direct_identifier()
    {
        var (_, a) = Augment("GC-11", [(Profile11, "Date of Birth\t1973", "Date of Birth\t1973/6/12")]);
        var id = Identity(a);
        Assert.Equal((Exact, Strength.High), (id.Evidence[EvidenceGroup.DateOfBirth].Status, id.Evidence[EvidenceGroup.DateOfBirth].Strength));
        Assert.Equal((4, IdentityDecision.ProbableMatch), (id.Stage3Row, id.Decision));
        Assert.DoesNotContain(ReasonCodes.NoBilateralDirectIdentifier, id.ReasonCodes);
    }

    [Fact]
    public void AUG_11_05_full_dob_conflict_is_row_1_review()
    {
        var (_, a) = Augment("GC-11", [(Profile11, "Date of Birth\t1973", "Date of Birth\t1973/8/1")]);
        var id = Identity(a);
        Assert.Equal((1, IdentityDecision.Review), (id.Stage3Row, id.Decision));
        Assert.Equal(ReasonCodes.FullDobConflict, id.ReasonCodes[0]);
    }

    [Fact]
    public void AUG_11_06_death_before_customer_birth_fires_but_stays_review()
    {
        var (input, a) = Augment("GC-11",
        [
            (Profile11, "已故?:\tFalse", "已故?:\tTrue"),
            (Profile11, "2\tDate of Birth\t1972", "2\tDate of Birth\t1972\n3\tDate of Death\t1970"),
        ]);
        var id = Identity(a);
        Assert.Equal((ReasonCodes.DeathBeforeCustomerBirth, IdentityDecision.Review), (id.HardConflict, id.Decision));
        Assert.Equal(CaseStatus.InReview, CaseLifecycle.AfterAnalysis(a, CaseStatus.Open));
        Assert.Equal(new PartialDate(1970), input.Profiles["90000011"].DateOfDeath);
    }

    [Fact]
    public void AUG_11_07_profile_address_equal_to_the_old_style_customer_address_is_not_support()
    {
        var (_, a) = Augment("GC-11", [Before(Profile11, "狀態:\tActive", "地址:\n編號\t地址\t城市\t國家\n1\t新北市新莊區中華路35巷8號3樓\t新北市\tTaiwan")]);
        var id = Identity(a);
        Assert.Equal((Uncertain, "SAME_ADDRESS_NO_PERIOD"), (id.Evidence[EvidenceGroup.Address].Status, id.Evidence[EvidenceGroup.Address].Detail));
        Assert.Equal(4, id.Stage3Row); // unchanged from GC-11
    }

    [Fact]
    public void AUG_11_08_same_employer_in_a_common_period_with_full_dob_is_row_3()
    {
        var (_, a) = Augment("GC-11",
            [
                (Profile11, "Date of Birth\t1973", "Date of Birth\t1973/6/12"),
                Before(Profile11, "狀態:\tActive", "職位:\n編號\t職位\t組織\t起始日期\t結束日期\n1\t董事長\t宏遠投資股份有限公司\t2015/1\t"),
            ],
            s => s with { Employments = [new Employment("宏遠投資股份有限公司", "董事長", new PartialDate(2016), null)] });
        var id = Identity(a);
        Assert.Equal(Strength.Medium, id.Evidence[EvidenceGroup.Employment].Strength);
        Assert.Equal((3, IdentityDecision.ProbableMatch), (id.Stage3Row, id.Decision));
        Assert.Equal(ReasonCodes.StrongSupportCombination, id.ReasonCodes[0]);
    }

    // GC-12: related party 陳志明, no gender; Profile has an Employer entity and a Wife, no DOB.

    [Fact]
    public void AUG_12_01_spouse_named_on_both_sides_is_support_row_5()
    {
        var (_, a) = Augment("GC-12", [], s => s with { Relatives = [new Relative("HSU YA TING", "配偶")] });
        var id = Identity(a, "DJ:90000012");
        Assert.Equal((Exact, Strength.Medium), (id.Evidence[EvidenceGroup.Relationship].Status, id.Evidence[EvidenceGroup.Relationship].Strength));
        Assert.Equal((5, IdentityDecision.Review), (id.Stage3Row, id.Decision));
    }

    [Fact]
    public void AUG_12_02_undated_employer_match_stays_uncertain()
    {
        var (_, a) = Augment("GC-12", [], s => s with { Employments = [new Employment("Heng Yuan Life Industry Company Limited", null, null, null)] });
        var id = Identity(a, "DJ:90000012");
        Assert.Equal("SAME_ORGANIZATION_NO_COMMON_PERIOD", id.Evidence[EvidenceGroup.Employment].Detail);
        Assert.Equal(6, id.Stage3Row);
    }

    [Fact]
    public void AUG_12_03_entity_record_is_non_person_review()
    {
        var (_, a) = Augment("GC-12", [(Profile12, "記錄類型:\tPerson", "記錄類型:\tEntity")]);
        var id = Identity(a, "DJ:90000012");
        Assert.Equal([ReasonCodes.NonPersonSubjectReview], id.ReasonCodes);
        Assert.Equal(IdentityDecision.Review, id.Decision);
    }

    // GC-01: customer born 1985/3/18; Profile 1 chairman (董事長) from 1990/7, Profile 2 general manager from 1991/3.

    [Fact]
    public void AUG_01_01_minor_director_is_an_impossible_role_but_not_an_impossible_age()
    {
        var (_, a) = Augment("GC-01", [("客戶-背景資料.txt", "1985/3/18", "1972/3/18")]);
        var chairman = Identity(a, "DJ:90000001");
        Assert.Equal((ReasonCodes.ImpossibleRoleAtReportedEvent, IdentityDecision.Review), (chairman.HardConflict, chairman.Decision));
        Assert.Null(Identity(a, "DJ:90000002").HardConflict); // 18 or 19 as general manager: no adult-only constraint
        Assert.Equal(CaseStatus.InReview, CaseLifecycle.AfterAnalysis(a, CaseStatus.Open)); // GC-01 closes; a pending rule cannot
    }
}
