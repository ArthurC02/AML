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
        Func<ScreenedSubject, ScreenedSubject>? subject = null, IReadOnlyDictionary<string, string>? addFiles = null, string[]? removeFiles = null)
    {
        var dir = Directory.CreateTempSubdirectory("aml-aug-");
        try
        {
            foreach (var f in Directory.GetFiles(GoldenCaseTests.Folder(gc)))
                File.Copy(f, Path.Combine(dir.FullName, Path.GetFileName(f)));
            foreach (var name in removeFiles ?? []) File.Delete(Path.Combine(dir.FullName, name));
            foreach (var (name, content) in addFiles ?? new Dictionary<string, string>())
                File.WriteAllText(Path.Combine(dir.FullName, name), content);
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
    public void AUG_11_03_passport_is_not_comparable_and_does_not_block_row_4()
    {
        var (_, a) = Augment("GC-11", [Before(Profile11, "狀態:\tActive", "身分證明:\n編號\t類型\t號碼\t國家\n1\tPassport\t312345678\tTaiwan")]);
        var id = Identity(a);
        Assert.Equal((Uncertain, "NOT_COMPARABLE"), (id.Evidence[EvidenceGroup.Document].Status, id.Evidence[EvidenceGroup.Document].Detail));
        // A document of another type is not comparable, which is not unsettled: GC-11 stays at row 4.
        Assert.Equal((4, IdentityDecision.ProbableMatch), (id.Stage3Row, id.Decision));
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

    [Fact]
    public void AUG_11_09_unread_attachments_are_disclosed()
    {
        var (input, _) = Augment("GC-11", [], addFiles: new Dictionary<string, string>
        {
            ["截圖.png"] = "not really an image",
            ["附件.txt"] = "<html><body>page source</body></html>",
        });
        Assert.Contains("UNSUPPORTED_ATTACHMENT:截圖.png", input.Subject.Findings);
        Assert.Contains("UNSUPPORTED_ATTACHMENT:附件.txt", input.Subject.Findings);
    }

    private const string Case11LastRow = "2\t钟仁声\t100\tDJ ALT\t90000011\t未符合\t符合\t確認後符合\t不是";

    [Fact]
    public void AUG_11_10_processing_record_is_imported_decision_only()
    {
        var (input, a) = Augment("GC-11",
            [("案件資訊.txt", Case11LastRow, Case11LastRow + "\n\n客戶檢核處理紀錄\n日期\t人員\t結果\n2026/9/12\t專員甲\t確認後符合")]);
        Assert.Contains(input.LegacyDecisions, d => d.Field == "客戶檢核處理紀錄" && d.Value.Contains("確認後符合", StringComparison.Ordinal));
        Assert.Equal(2, Assert.Single(a.Hits).CandidateRows); // the record table never becomes a candidate row
        Assert.DoesNotContain("LEGACY_RECORD_BOUNDARY_UNCLEAR", input.Subject.Findings);
    }

    [Fact]
    public void AUG_11_11_text_after_the_processing_record_makes_the_boundary_unclear()
    {
        var (input, _) = Augment("GC-11",
            [("案件資訊.txt", Case11LastRow, Case11LastRow + "\n\n客戶檢核處理紀錄\n日期\t人員\t結果\n2026/9/12\t專員甲\t確認後符合\n經查與名單人物為同一人")]);
        Assert.Contains("LEGACY_RECORD_BOUNDARY_UNCLEAR", input.Subject.Findings);
    }

    // GC-12: related party 陳志明, no gender; Profile has an Employer entity and a Wife, no DOB.

    [Fact]
    public void AUG_12_04_same_reference_number_on_the_profile_is_low_support_row_5()
    {
        var (_, a) = Augment("GC-12", [Before(Profile12, "狀態:\tActive", "其他識別碼:\n編號\t類型\t號碼\n1\tReference\tB123456780")]);
        var id = Identity(a, "DJ:90000012");
        Assert.Equal((Exact, Strength.Low), (id.Evidence[EvidenceGroup.OtherIdentifier].Status, id.Evidence[EvidenceGroup.OtherIdentifier].Strength));
        Assert.Equal(ComparisonStatus.SubjectMissing, id.Evidence[EvidenceGroup.Document].Status); // never DOCUMENT evidence
        Assert.Equal((5, IdentityDecision.Review), (id.Stage3Row, id.Decision));
    }

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

    [Fact]
    public void AUG_02_09_judgment_naming_the_person_is_convicted()
    {
        var (_, a) = Augment("GC-02", [(News21, "檢方已分案偵辦", "法院一審判處林美玲有期徒刑二年")]);
        Assert.Equal(LegalStage.Convicted, Assert.Single(Hit21(a).Conditions).LegalStage);
    }

    [Fact]
    public void AUG_02_10_a_co_defendants_sentence_is_not_attributed()
    {
        var (_, a) = Augment("GC-02", [(News21, "檢方已分案偵辦。", "檢方已起訴。同案另一名被告已判處有期徒刑二年。")]);
        Assert.Equal(LegalStage.Indicted, Assert.Single(Hit21(a).Conditions).LegalStage);
    }

    [Fact]
    public void AUG_02_11_stated_age_in_chinese_numerals_is_compared()
    {
        var (_, a) = Augment("GC-02", [(News21, "負責人林美玲涉嫌", "負責人林美玲（四十九歲）涉嫌")]);
        Assert.Equal(ReasonCodes.AgeCompatibleAtReport, Hit21(a).Identity.Evidence[EvidenceGroup.StatedAge].Detail);
    }

    [Fact]
    public void AUG_02_12_unparseable_customer_dob_is_disclosed()
    {
        var (input, a) = Augment("GC-02", [("客戶-背景資料.txt", "1975/9/2", "1975年9月2日")]);
        Assert.Contains("UNPARSEABLE:出生日期", input.Subject.Findings);
        Assert.Equal(ComparisonStatus.SubjectMissing, Hit21(a).Identity.Evidence[EvidenceGroup.DateOfBirth].Status);
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

    [Fact]
    public void AUG_03_10_address_country_contradicting_the_text_is_a_field_conflict()
    {
        var (input, _) = Augment("GC-03", [("客戶-聯絡資料.txt", "國家\nTaiwan", "國家\nViet Nam")]);
        Assert.Contains("FIELD_CONFLICT:永久地址", input.Subject.Findings);
        Assert.Equal("VN", Assert.Single(input.Subject.Addresses).Country); // both facts kept: the field says VN, the text stays Taiwanese
    }

    // Source flags. Column order assumed (… Is RCA?, Name Property, PEP, 黑名單, 負面新聞); the samples stop at Is RCA?.

    private const string Case03Row1 = "1\tNguyễn Thị Hồng Nhung\t100\tDJ PRIMARY\t90000031\t未符合\t符合\t確認後符合\t不是";
    private const string Case03Row2 = "2\tNguyen Thi Hong Nhung\t100\tDJ ALT\t90000031\t未符合\t符合\t確認後符合\t不是";

    [Fact]
    public void AUG_03_11_pep_flag_wins_over_the_category_and_records_the_conflict()
    {
        var (_, a) = Augment("GC-03", [("案件資訊.txt", Case03Row1, Case03Row1 + "\t是\t否\t否")]);
        var hit = Assert.Single(a.Hits);
        var pep = hit.Conditions.Single(c => c.Type == ListType.Pep);
        Assert.Equal((ConditionStatus.Hit, ConditionBasis.Flag), (pep.Status, pep.Basis));
        Assert.Contains(SourceConditions.FlagCategoryConflict, pep.ReasonCodes); // the category said Other Official List
        Assert.Contains(hit.Conditions, c => c.Status == ConditionStatus.NotHit); // NO is a value: 黑名單 and 負面新聞 NOT_HIT
        Assert.Equal(Recommendation.OnList, hit.Disposition.Recommendation); // ① on the flag
    }

    [Fact]
    public void AUG_03_12_all_flags_no_is_not_applicable()
    {
        var (_, a) = Augment("GC-03",
            [("案件資訊.txt", Case03Row1, Case03Row1 + "\t否\t否\t否"), ("案件資訊.txt", Case03Row2, Case03Row2 + "\t否\t否\t否")]);
        var hit = Assert.Single(a.Hits);
        Assert.All(hit.Conditions, c => Assert.Equal(ConditionStatus.NotHit, c.Status));
        Assert.Equal(ScreeningAssessment.NotApplicable, hit.PairResult);
    }

    [Fact]
    public void AUG_03_13_unparseable_flag_is_unknown()
    {
        var (_, a) = Augment("GC-03", [("案件資訊.txt", Case03Row1, Case03Row1 + "\t待確認\t否\t否")]);
        var pep = Assert.Single(a.Hits).Conditions.Single(c => c.Type == ListType.Pep);
        Assert.Equal((ConditionStatus.Unknown, true), (pep.Status, pep.ReasonCodes.Contains(SourceConditions.FlagUnparseable)));
    }

    [Fact]
    public void AUG_03_14_blacklist_flag_takes_its_type_from_the_official_list_entry()
    {
        var (_, a) = Augment("GC-03", [("案件資訊.txt", Case03Row1, Case03Row1 + "\t否\t是\t否")]);
        var listed = Assert.Single(a.Hits).Conditions.Single(c => c.Status == ConditionStatus.Hit);
        Assert.Equal((ListType.OfficialWantedList, LegalStage.Wanted, ConditionBasis.Flag), (listed.Type, listed.LegalStage, listed.Basis));
    }

    [Fact]
    public void AUG_02_13_adverse_news_on_non_predicate_conduct_is_unrelated_and_set_aside()
    {
        var (_, a) = Augment("GC-02",
        [
            (Profile21, "1\tSpecial Interest Person (SIP)\tFraud", "1\tAdverse Media\t"),
            (Profile21, "關鍵字:\tFraud; Tax Evasion\n", ""),
            (News21, "貿易商虛開發票逃漏稅", "貿易商負責人酒駕"),
            (News21, "負責人涉詐欺", "移送法辦"),
            (News21, "一家貿易公司負責人林美玲涉嫌以虛設交易詐取貨款，並開立不實發票逃漏稅捐，檢方已分案偵辦", "林美玲酒後駕車肇事，警方依公共危險罪移送"),
        ]);
        var hit = Hit21(a);
        var condition = Assert.Single(hit.Conditions);
        Assert.Equal((ListType.AdverseMedia, AmlRelevance.Unrelated), (condition.Type, condition.AmlRelevance));
        Assert.Equal((Recommendation.NotOnList, DispositionPolicy.AdverseUnrelated), (hit.Disposition.Recommendation, hit.Disposition.ReasonCodes[0]));
        Assert.Equal(IdentityDecision.Review, hit.Identity.Decision); // ② says nothing about identity
    }

    [Fact]
    public void AUG_02_14_fraud_is_never_unrelated()
    {
        var (_, a) = Augment("GC-02", [(News21, "檢方已分案偵辦", "林美玲另涉酒駕")]);
        Assert.Equal(AmlRelevance.Uncertain, Assert.Single(Hit21(a).Conditions).AmlRelevance); // FRAUD category stays a predicate
    }

    [Fact]
    public void AUG_01_04_year_only_role_date_downgrades_the_validated_conflict_to_review()
    {
        var (_, a) = Augment("GC-01", [("名單資料_90000001.txt", "1\t董事長\t宏遠投資股份有限公司\t1990/7\t1995/12", "1\t董事長\t宏遠投資股份有限公司\t1990\t1995/12")]);
        var chairman = Identity(a, "DJ:90000001");
        Assert.Equal((ReasonCodes.ImpossibleAgeAtReportedEvent, IdentityDecision.Review), (chairman.HardConflict, chairman.Decision));
        Assert.Equal([ReasonCodes.ImpossibleAgeAtReportedEvent, ReasonCodes.SourceQualityDowngrade], chairman.ReasonCodes);
        Assert.Equal(IdentityDecision.NoMatch, Identity(a, "DJ:90000002").Decision); // the other Profile is unaffected
    }

    [Fact]
    public void AUG_12_05_the_2026_10_14_ruling_is_flagged_without_a_recommendation()
    {
        var (_, a) = Augment("GC-12", []);
        var d = Assert.Single(a.Hits).Disposition;
        Assert.Null(d.Recommendation); // GC-12 expected: recommendation NONE
        Assert.Contains(DispositionPolicy.Ruling20261014MayApply, d.ReasonCodes);
    }

    // HTML input: the same GC-03 facts as HTML pages give the same result as the .txt export.

    private const string Customer03Html = """
        <html><body><h2>基本資料</h2><table>
        <tr><th>客戶號碼</th><td>AC12345678</td></tr>
        <tr><th>性別</th><td><select><option>男</option><option selected>女</option></select></td></tr>
        <tr><th>客戶名稱</th><td><input type="text" value="NGUYEN THI HONG NHUNG"></td></tr>
        <tr><th>出生日期</th><td>1992/4/10</td></tr>
        <tr><th>證件種類</th><td>居留證</td></tr>
        <tr><th>國籍 1</th><td>Viet Nam</td></tr>
        <tr><th>本地名稱</th><td>-</td></tr>
        <tr><th>證件號碼</th><td>AC12345678</td></tr>
        <tr><th>國籍 2</th><td>-</td></tr>
        </table><h2>詳細資料</h2><table>
        <tr><th>客戶類型</th><td>個人</td></tr>
        <tr><th>職業</th><td>製造業(3)</td></tr>
        </table></body></html>
        """;

    private const string Profile03Html = """
        <html><body><table>
        <tr><th>Profile_ID</th><td>90000031</td></tr>
        <tr><th>記錄類型</th><td>Person</td></tr>
        <tr><th>性別</th><td>Female</td></tr>
        <tr><th>已故?</th><td>False</td></tr>
        <tr><th>狀態</th><td>Active</td></tr>
        </table>
        <h3>主要名稱</h3><table><tr><th>編號</th><th>名字</th><th>姓</th></tr><tr><td>1</td><td>Thị Hồng Nhung</td><td>Nguyễn</td></tr></table>
        <h3>拼寫變化</h3><table><tr><th>編號</th><th>名字</th><th>姓</th></tr><tr><td>1</td><td>Thi Hong Nhung</td><td>Nguyen</td></tr></table>
        <h3>國家資料</h3><table><tr><th>編號</th><th>類型</th><th>國家</th></tr>
          <tr><td>1</td><td>Citizenship</td><td>Viet Nam</td></tr><tr><td>2</td><td>Resident of</td><td>Viet Nam</td></tr></table>
        <h3>地址</h3><table><tr><th>編號</th><th>地址</th><th>城市</th><th>國家</th></tr>
          <tr><td>1</td><td>Thôn Example, Xã An Hòa</td><td>Tỉnh Example</td><td>Viet Nam</td></tr></table>
        <h3>日期</h3><table><tr><th>編號</th><th>類型</th><th>日期</th></tr><tr><td>1</td><td>Date of Birth</td><td>1992</td></tr></table>
        <h3>類別</h3><table><tr><th>編號</th><th>描述1</th><th>描述2</th></tr><tr><td>1</td><td>Other Official List</td><td></td></tr></table>
        <h3>名單資訊</h3><table><tr><th>機關</th><th>名單</th><th>狀態</th><th>決定日期</th><th>罪名</th></tr>
          <tr><td>Example Provincial Police</td><td>Wanted Persons List</td><td>Active</td><td>2024/6/30</td><td>Fraud</td></tr></table>
        </body></html>
        """;

    [Fact]
    public void AUG_03_15_html_pages_give_the_same_result_as_the_export()
    {
        var (input, a) = Augment("GC-03", [], removeFiles: ["客戶-背景資料.txt", "名單資料_90000031.txt"],
            addFiles: new Dictionary<string, string> { ["客戶-背景資料.html"] = Customer03Html, ["名單資料_90000031.html"] = Profile03Html });
        Assert.Equal(Gender.Female, input.Subject.Gender); // the selected option, not the first
        var hit = Assert.Single(a.Hits);
        Assert.Equal((ListType.OfficialWantedList, LegalStage.Wanted), (Assert.Single(hit.Conditions).Type, hit.Conditions[0].LegalStage));
        Assert.Equal((4, IdentityDecision.ProbableMatch), (hit.Identity.Stage3Row, hit.Identity.Decision));
        Assert.Contains("名單資料_90000031.html", input.SourceHashes!.Keys); // the raw page is hashed
        Assert.DoesNotContain(input.Subject.Findings, f => f.StartsWith("UNSUPPORTED_ATTACHMENT", StringComparison.Ordinal));
    }

    [Fact]
    public void AUG_03_16_html_case_table_reads_flag_checkboxes()
    {
        const string html = """
            <html><body><table>
            <tr><th>編號</th><th>被偵測到的名字</th><th>資料類別</th><th>關聯方參考號碼</th><th>資料來源</th><th>是否符合名單？</th></tr>
            <tr><td>1</td><td>NGUYEN THI HONG NHUNG</td><td>客戶</td><td></td><td>客戶名稱</td><td>符合</td></tr>
            </table><h3>疑似符合的名單資料</h3><table>
            <tr><th rowspan="2">編號</th><th rowspan="2">姓名/名稱</th><th rowspan="2">相似度分數</th><th rowspan="2">SDN禁制類別</th><th rowspan="2">名單資料編號</th>
              <th colspan="2">是否符合名單？</th><th rowspan="2">理由</th><th rowspan="2">Is RCA?</th><th colspan="3">Name Property</th></tr>
            <tr><th>全不符合</th><th>全符合</th><th>PEP</th><th>黑名單</th><th>負面新聞</th></tr>
            <tr><td>1</td><td>Nguyen Thi Hong Nhung</td><td>100</td><td>DJ ALT</td><td>90000031</td><td>未符合</td><td>符合</td><td>確認後符合</td><td><input type="checkbox"></td>
              <td><input type="checkbox" checked></td><td><input type="checkbox"></td><td><input type="checkbox"></td></tr>
            </table></body></html>
            """;
        var (input, a) = Augment("GC-03", [], removeFiles: ["案件資訊.txt"], addFiles: new Dictionary<string, string> { ["案件資訊.html"] = html });
        Assert.False(Assert.Single(input.Rows).IsRca); // unchecked Is RCA? reads 否
        Assert.Contains(Assert.Single(a.Hits).Conditions, c => c.Type == ListType.Pep && c.Status == ConditionStatus.Hit && c.Basis == ConditionBasis.Flag);
    }
}
