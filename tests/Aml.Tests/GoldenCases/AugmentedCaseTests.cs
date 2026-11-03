using Aml.Core.Cases;
using Aml.Core.Identity;
using Aml.Core.Screening;
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
                var text = File.ReadAllText(path).ReplaceLineEndings("\n"); // Windows checkouts may have CRLF
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

    [Fact]
    public void AUG_01_02_minimum_role_age_boundary_splits_the_two_profiles()
    {
        // Born 1977/3/18: at most 13 in 1990/7 (chairman), at most 14 in 1991/3 (general manager, month precision).
        var (_, a) = Augment("GC-01", [("客戶-背景資料.txt", "1985/3/18", "1977/3/18")]);
        var chairman = Identity(a, "DJ:90000001");
        Assert.Equal((ReasonCodes.ImpossibleAgeAtReportedEvent, IdentityDecision.NoMatch), (chairman.HardConflict, chairman.Decision));
        var manager = Identity(a, "DJ:90000002");
        Assert.Null(manager.HardConflict); // 14 is not below MinimumRoleAge, and 總經理 is not adult-only
        Assert.Equal((6, IdentityDecision.Review), (manager.Stage3Row, manager.Decision));
        Assert.Equal(CaseStatus.InReview, CaseLifecycle.AfterAnalysis(a, CaseStatus.Open));
    }

    [Fact]
    public void AUG_01_03_plausible_age_removes_the_hard_conflict_and_adds_no_support()
    {
        var (_, a) = Augment("GC-01", [("客戶-背景資料.txt", "1985/3/18", "1960/3/18")]);
        foreach (var key in new[] { "DJ:90000001", "DJ:90000002" })
        {
            var id = Identity(a, key);
            Assert.Null(id.HardConflict);
            Assert.Equal((6, IdentityDecision.Review), (id.Stage3Row, id.Decision)); // temporal plausibility is not support
        }
        Assert.All(a.Hits, h => Assert.Equal(ScreeningAssessment.Review, h.PairResult));
    }

    // GC-02: Taiwanese customer 林美玲 born 1975/9/2; Profile SIP / Fraud, keywords Fraud and Tax Evasion, no DOB;
    // news of 2025/3/12 says the case is under investigation (分案偵辦).

    private const string Profile21 = "名單資料_90000021.txt";
    private const string News21 = "新聞資訊_90000021.txt";

    private static HitResult Hit21(CaseAnalysis a) => a.Hits.Single(h => h.Profile.ProfileKey == "DJ:90000021");

    [Fact]
    public void AUG_02_01_month_precision_dob_agreement_is_medium_support_row_4()
    {
        var (_, a) = Augment("GC-02", [Before(Profile21, "狀態:\tActive", "日期:\n編號\t類型\t日期\n1\tDate of Birth\t1975/9")]);
        var id = Hit21(a).Identity;
        Assert.Equal((Partial, Strength.Medium), (id.Evidence[EvidenceGroup.DateOfBirth].Status, id.Evidence[EvidenceGroup.DateOfBirth].Strength));
        Assert.Equal((4, IdentityDecision.ProbableMatch), (id.Stage3Row, id.Decision));
        Assert.Contains(ReasonCodes.NoBilateralDirectIdentifier, id.ReasonCodes); // month precision is not a direct identifier
    }

    [Fact]
    public void AUG_02_02_year_only_dob_conflict_is_dob_conflict_not_full_dob_conflict()
    {
        var (_, a) = Augment("GC-02", [Before(Profile21, "狀態:\tActive", "日期:\n編號\t類型\t日期\n1\tDate of Birth\t1980")]);
        var id = Hit21(a).Identity;
        Assert.Equal((1, IdentityDecision.Review), (id.Stage3Row, id.Decision));
        Assert.Equal(ReasonCodes.DobConflict, id.ReasonCodes[0]);
        Assert.Null(id.HardConflict); // never a stage 1 hard conflict
    }

    [Fact]
    public void AUG_02_03_stated_age_consistent_with_the_customer_is_compatible_not_support()
    {
        var (_, a) = Augment("GC-02", [(News21, "負責人林美玲涉嫌", "負責人林美玲（49歲）涉嫌")]);
        var id = Hit21(a).Identity;
        var age = id.Evidence[EvidenceGroup.StatedAge];
        Assert.Equal((Exact, Direction.Neutral, ReasonCodes.AgeCompatibleAtReport), (age.Status, age.Direction, age.Detail));
        Assert.Equal((6, IdentityDecision.Review), (id.Stage3Row, id.Decision)); // unchanged from GC-02
    }

    [Fact]
    public void AUG_02_04_stated_age_far_from_the_customer_is_a_soft_conflict_row_1()
    {
        var (_, a) = Augment("GC-02", [(News21, "負責人林美玲涉嫌", "負責人林美玲（30歲）涉嫌")]);
        var id = Hit21(a).Identity;
        Assert.Equal(ComparisonStatus.Conflict, id.Evidence[EvidenceGroup.StatedAge].Status);
        Assert.Null(id.HardConflict); // soft, never NO_MATCH
        Assert.Equal((1, IdentityDecision.Review), (id.Stage3Row, id.Decision));
        Assert.Equal(ReasonCodes.AgeDifferenceAtReport, id.ReasonCodes[0]);
    }

    [Fact]
    public void AUG_02_05_indictment_in_the_news_sets_legal_stage_indicted()
    {
        var (_, a) = Augment("GC-02", [(News21, "檢方已分案偵辦", "檢方已依詐欺罪嫌起訴")]);
        var condition = Assert.Single(Hit21(a).Conditions);
        Assert.Equal((ListType.AdverseMedia, LegalStage.Indicted), (condition.Type, condition.LegalStage));
        Assert.Equal(AmlRelevance.Uncertain, condition.AmlRelevance); // ② is not decided by extraction
    }

    [Fact]
    public void AUG_02_06_money_laundering_keyword_adds_a_risk_category()
    {
        var (_, a) = Augment("GC-02", [(Profile21, "關鍵字:\tFraud; Tax Evasion", "關鍵字:\tFraud; Tax Evasion; Money Laundering")]);
        Assert.Equal(["FRAUD", "TAX_EVASION", "MONEY_LAUNDERING"], Assert.Single(Hit21(a).Conditions).RiskCategories);
    }

    [Fact]
    public void AUG_02_07_unmapped_category_is_unknown_and_keeps_every_relevance_check_open()
    {
        var (_, a) = Augment("GC-02", [(Profile21, "Special Interest Person (SIP)\tFraud", "Special Interest Person (SIP)\tGambling")]);
        var hit = Hit21(a);
        var condition = Assert.Single(hit.Conditions);
        Assert.Equal((ListType.Unknown, ConditionStatus.Unknown), (condition.Type, condition.Status));
        Assert.Equal((ScreeningAssessment.Review, true), (hit.PairResult, hit.PairReasonCodes.Contains(AssessmentPolicy.SourceConditionUnknown)));
        Assert.Null(hit.Disposition.Recommendation); // Taiwanese customer: no foreign PEP presumption
        Assert.Contains(DispositionPolicy.EmploymentRelevance, hit.Disposition.PendingChecks);
        Assert.Contains(DispositionPolicy.RegionRelevance, hit.Disposition.PendingChecks);
    }

    [Fact]
    public void AUG_02_08_near_name_is_partial_and_never_support()
    {
        var (_, a) = Augment("GC-02",
        [
            (Profile21, "OSN\t林美玲", "OSN\t林美齡"),
            (Profile21, "1\tMei-Ling\tLin", "1\tMei-Lin\tLin"),
        ]);
        var id = Hit21(a).Identity;
        var name = id.Evidence[EvidenceGroup.Name];
        Assert.Equal((Direction.Prerequisite, Partial), (name.Direction, name.Status));
        Assert.StartsWith("NOT_IDENTICAL_AFTER_NORMALIZATION", name.Detail, StringComparison.Ordinal);
        Assert.Equal((6, IdentityDecision.Review), (id.Stage3Row, id.Decision));
    }

    // GC-03: Vietnamese customer and Profile (citizenship Viet Nam), active official wanted list, PROBABLE_MATCH.

    private static readonly (string, string, string) ToPep =
        ("名單資料_90000031.txt", "1\tOther Official List\t", "1\tPolitically Exposed Person\t");

    [Fact]
    public void AUG_03_01_foreign_pep_is_presumed_on_list_article_22_1()
    {
        var (_, a) = Augment("GC-03", [ToPep]);
        var hit = Assert.Single(a.Hits);
        Assert.Equal(ListType.Pep, Assert.Single(hit.Conditions).Type);
        Assert.Equal(IdentityDecision.ProbableMatch, hit.Identity.Decision); // unchanged by the presumption
        Assert.Equal((Recommendation.OnList, DispositionPolicy.ForeignPepPresumption), (hit.Disposition.Recommendation, hit.Disposition.ReasonCodes[0]));
    }

    [Fact]
    public void AUG_03_02_foreign_pep_with_customer_nationality_missing_needs_confirmation()
    {
        var (_, a) = Augment("GC-03",
        [
            ToPep,
            ("客戶-背景資料.txt", "國籍 1\nViet Nam", "國籍 1\n-"),
        ]);
        var hit = Assert.Single(a.Hits);
        Assert.Null(hit.Disposition.Recommendation);
        Assert.Equal(DispositionPolicy.ConfirmNationality, hit.Disposition.PendingChecks[0]);
    }

    [Fact]
    public void AUG_03_03_former_pep_keeps_the_presumption()
    {
        var (_, a) = Augment("GC-03", [("名單資料_90000031.txt", "1\tOther Official List\t", "1\tPolitically Exposed Person\tFormer PEP")]);
        var hit = Assert.Single(a.Hits);
        Assert.Equal((ListType.Pep, "FORMER_PEP"), (Assert.Single(hit.Conditions).Type, hit.Conditions[0].PepType));
        Assert.Equal(Recommendation.OnList, hit.Disposition.Recommendation);
    }

    [Fact]
    public void AUG_03_04_pep_with_a_taiwanese_customer_gets_no_presumption()
    {
        var (_, a) = Augment("GC-03", [ToPep, ("客戶-背景資料.txt", "國籍 1\nViet Nam", "國籍 1\nTaiwan")]);
        var hit = Assert.Single(a.Hits);
        Assert.Equal(ComparisonStatus.Conflict, hit.Identity.Evidence[EvidenceGroup.Nationality].Status);
        Assert.Equal((5, IdentityDecision.Review), (hit.Identity.Stage3Row, hit.Identity.Decision)); // prerequisites disagree: row 4 out
        Assert.Null(hit.Disposition.Recommendation);
        Assert.Equal(DispositionPolicy.NotDetermined, hit.Disposition.ReasonCodes[^1]);
        Assert.DoesNotContain(DispositionPolicy.ConfirmNationality, hit.Disposition.PendingChecks);
    }

    [Fact]
    public void AUG_03_05_pep_profile_without_dob_needs_an_identity_check()
    {
        var (_, a) = Augment("GC-03", [ToPep, ("名單資料_90000031.txt", "1\tDate of Birth\t1992\n", "")]);
        var hit = Assert.Single(a.Hits);
        Assert.Equal(ComparisonStatus.ProfileMissing, hit.Identity.Evidence[EvidenceGroup.DateOfBirth].Status);
        Assert.Contains(DispositionPolicy.IdentityCheck, hit.Disposition.PendingChecks);
    }

    [Fact]
    public void AUG_03_06_rca_has_no_presumption_and_stays_under_release_control()
    {
        var (_, a) = Augment("GC-03", [("名單資料_90000031.txt", "1\tOther Official List\t", "1\tRelative or Close Associate (RCA)\t")]);
        var hit = Assert.Single(a.Hits);
        var condition = Assert.Single(hit.Conditions);
        Assert.Equal((ListType.Rca, "RCA_UNCONFIRMED"), (condition.Type, condition.PepType));
        Assert.Equal(ScreeningAssessment.Review, hit.PairResult);
        Assert.Null(hit.Disposition.Recommendation); // ① covers PEP only
    }

    [Fact]
    public void AUG_03_07_active_named_sanction_measure_is_a_high_priority_hit()
    {
        var (_, a) = Augment("GC-03",
        [
            ("名單資料_90000031.txt", "1\tOther Official List\t", "1\tSanctions Lists\t"),
            ("名單資料_90000031.txt", "Example Provincial Police\tWanted Persons List", "Example Ministry of Finance\tExample Sanctions List"),
        ]);
        var hit = Assert.Single(a.Hits);
        var condition = Assert.Single(hit.Conditions);
        Assert.Equal((ListType.Sanction, ConditionStatus.Hit), (condition.Type, condition.Status));
        Assert.Equal(("Example Sanctions List", "Example Ministry of Finance"), (condition.Program, condition.Jurisdiction));
        Assert.Equal((ScreeningAssessment.Review, ReviewPriority.High), (hit.PairResult, hit.ReviewPriority));
    }

    [Fact]
    public void AUG_03_08_sanction_without_an_active_measure_is_unknown()
    {
        var (_, a) = Augment("GC-03",
        [
            ("名單資料_90000031.txt", "1\tOther Official List\t", "1\tSanctions Lists\t"),
            ("名單資料_90000031.txt", "Wanted Persons List\tActive", "Wanted Persons List\tDelisted"),
        ]);
        var hit = Assert.Single(a.Hits);
        var condition = Assert.Single(hit.Conditions);
        Assert.Equal((ConditionStatus.Unknown, SourceConditions.SanctionMeasureNotConfirmed), (condition.Status, condition.ReasonCodes[0]));
        Assert.Contains(AssessmentPolicy.SourceConditionUnknown, hit.PairReasonCodes);
        Assert.Equal(ScreeningAssessment.Review, hit.PairResult);
    }

    [Fact]
    public void AUG_03_09_inactive_wanted_list_is_not_described_as_wanted()
    {
        var (_, a) = Augment("GC-03", [("名單資料_90000031.txt", "Wanted Persons List\tActive", "Wanted Persons List\tRemoved")]);
        var condition = Assert.Single(Assert.Single(a.Hits).Conditions);
        Assert.Equal((ListType.OtherOfficialList, LegalStage.Unknown), (condition.Type, condition.LegalStage));
    }
}
