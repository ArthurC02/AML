using System.Text.Json;
using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Core.Identity;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using Aml.Infra.Txt;
using Aml.Services;

namespace Aml.Tests.Unit.Screening;

/// <summary>Flags and categories, legal stage and ② relevance from news, Article 22 ①, review priority, risk scoring, former PEP and RCA scope.</summary>
public class ScreeningPolicyTests
{
    private static ScreenedSubject Subject(params string[] nationality) =>
        new(SubjectRole.Customer, null, ["陳志明"], Gender.Male, new PartialDate(1980, 5, 20), nationality.Length == 0 ? ["TW"] : nationality,
            null, null, null, null, [], []);

    private static SourceProfile Profile(string category, string? sub = null, OfficialListEntry? list = null,
        IReadOnlyList<ProfileRole>? roles = null, params string[] citizenship) =>
        new("DJ:1", Gender.Male, ["陳志明"], [], citizenship.Length == 0 ? ["TW"] : citizenship, [], [new SourceCategory(category, sub)], [],
            roles ?? [], [], list, [], []);

    private static HitResult Analyze(SourceProfile p, ListFlags? flags = null, bool isRca = false, ScreenedSubject? subject = null, DateOnly? asOf = null) =>
        CaseAnalyzer.Analyze(new CaseInput(subject ?? Subject(), [new CandidateRow("陳志明", "1", null, isRca, flags)],
            new Dictionary<string, SourceProfile> { ["1"] = p }, new Dictionary<string, NewsItem>(), []), asOf: asOf).Hits[0];

    private static ListFlags Flags(FlagValue pep = FlagValue.Missing, FlagValue blacklist = FlagValue.Missing, FlagValue adverse = FlagValue.Missing) =>
        new(pep, blacklist, adverse);

    private static IdentityResult Identity(IdentityDecision decision = IdentityDecision.Review) =>
        new(new Dictionary<EvidenceGroup, Evidence>(), null, 6, decision, []);

    private static SourceCondition Condition(ListType type, ConditionStatus status = ConditionStatus.Hit, LegalStage stage = LegalStage.Unknown,
        AmlRelevance? relevance = null, ConditionBasis basis = ConditionBasis.CategoryMapping, params string[] codes) =>
        new(type, status, basis, [], stage, relevance, SourceConditions.PolicyVersion, codes);

    // a mapped category whose flag is blank keeps its condition; 黑名單 = NO never clears a Sanction category.

    [Fact]
    public void sanction_category_with_pep_no_and_other_flags_blank_keeps_the_sanction_condition()
    {
        var hit = Analyze(Profile("Sanctions Lists", list: new OfficialListEntry("OFAC", "SDN List", "Active", null)), Flags(pep: FlagValue.No));
        var sanction = hit.Conditions.Single(c => c.Type == ListType.Sanction);
        Assert.Equal((ConditionStatus.Hit, ConditionBasis.CategoryMapping), (sanction.Status, sanction.Basis));
        Assert.Equal((ScreeningAssessment.Review, ReviewPriority.High), (hit.PairResult, hit.ReviewPriority));
    }

    [Fact]
    public void sanction_category_with_blacklist_no_is_unknown_with_a_conflict()
    {
        var hit = Analyze(Profile("Sanctions Lists"), Flags(FlagValue.No, FlagValue.No, FlagValue.No));
        var sanction = hit.Conditions.Single(c => c.Type == ListType.Sanction);
        Assert.Equal(ConditionStatus.Unknown, sanction.Status);
        Assert.Contains(SourceConditions.FlagCategoryConflict, sanction.ReasonCodes);
        Assert.Equal((ScreeningAssessment.Review, ReviewPriority.High), (hit.PairResult, hit.ReviewPriority));
    }

    [Fact]
    public void rca_category_with_is_rca_blank_and_other_flags_set_keeps_the_rca_condition()
    {
        var hit = Analyze(Profile("Relative or Close Associate (RCA)", "Spouse"), Flags(FlagValue.No, FlagValue.No, FlagValue.No));
        var rca = hit.Conditions.Single(c => c.Type == ListType.Rca);
        Assert.Equal((ConditionStatus.Hit, ConditionBasis.CategoryMapping), (rca.Status, rca.Basis));
        Assert.Equal(ScreeningAssessment.Review, hit.PairResult);
    }

    // Decision table: PEP, 黑名單 and Is RCA? flags × category × an official-list entry naming a Sanction (or none).
    // A 黑名單 YES takes its type from the official-list entry before the category; with neither, the type is undecided.
    [Theory]
    [InlineData(FlagValue.Missing, FlagValue.Yes, false, "Sanctions Lists", null, "SDN List", "Sanction:Hit")]
    [InlineData(FlagValue.Missing, FlagValue.Yes, false, "Special Interest Person (SIP)", "Fraud", "UN Sanctions List", "Sanction:Hit,AdverseMedia:Hit")]
    [InlineData(FlagValue.Missing, FlagValue.Yes, false, "Politically Exposed Person", null, "UN Sanctions List", "Sanction:Hit,Pep:Hit")]
    [InlineData(FlagValue.Missing, FlagValue.Yes, false, "Relative or Close Associate (RCA)", "Spouse", "EU Sanctions Regime", "Sanction:Hit,Rca:Hit")]
    [InlineData(FlagValue.Missing, FlagValue.Yes, false, "Politically Exposed Person", null, null, "Unknown:Unknown,Pep:Hit")]
    [InlineData(FlagValue.Missing, FlagValue.No, false, "Sanctions Lists", null, null, "Sanction:Unknown")]
    [InlineData(FlagValue.Missing, FlagValue.No, false, "Special Interest Person (SIP)", "Fraud", "UN Sanctions List", "Sanction:NotHit,AdverseMedia:Hit")]
    [InlineData(FlagValue.Missing, FlagValue.No, false, "Politically Exposed Person", null, null, "Unknown:NotHit,Pep:Hit")]
    [InlineData(FlagValue.Missing, FlagValue.Unparseable, false, "Sanctions Lists", null, "SDN List", "Sanction:Unknown")]
    [InlineData(FlagValue.Missing, FlagValue.Unparseable, false, "Relative or Close Associate (RCA)", "Spouse", null, "Unknown:Unknown,Rca:Hit")]
    [InlineData(FlagValue.Yes, FlagValue.Missing, false, "Politically Exposed Person", null, null, "Pep:Hit")]
    [InlineData(FlagValue.Yes, FlagValue.Missing, false, "Special Interest Person (SIP)", "Fraud", null, "Pep:Hit,AdverseMedia:Hit")]
    [InlineData(FlagValue.Missing, FlagValue.Missing, true, "Relative or Close Associate (RCA)", "Spouse", null, "Rca:Hit")]
    [InlineData(FlagValue.Missing, FlagValue.Missing, true, "Politically Exposed Person", null, null, "Pep:Hit,Rca:Hit")]
    [InlineData(FlagValue.No, FlagValue.Yes, true, "Sanctions Lists", null, "SDN List", "Pep:NotHit,Sanction:Hit,Rca:Hit")]
    public void source_conditions_decision_table(FlagValue pep, FlagValue blacklist, bool isRca, string category, string? sub, string? list, string expected)
    {
        var entry = list is null ? null : new OfficialListEntry("UN", list, "Active", null);
        var conditions = SourceConditions.Determine(Profile(category, sub, entry), isRca, null, Flags(pep, blacklist));
        Assert.Equal(expected, string.Join(",", conditions.Select(c => $"{c.Type}:{c.Status}")));
    }

    [Fact]
    public void a_flag_with_a_value_still_wins_over_its_category()
    {
        var pep = Analyze(Profile("Politically Exposed Person"), Flags(pep: FlagValue.No)).Conditions.Single(c => c.Type == ListType.Pep);
        Assert.Equal(ConditionStatus.NotHit, pep.Status);
        Assert.Contains(SourceConditions.FlagCategoryConflict, pep.ReasonCodes);
    }

    // legal stage.

    [Theory]
    [InlineData("檢察官對陳志明為不起訴處分。", LegalStage.Dismissed)]
    [InlineData("陳志明獲緩起訴處分。", LegalStage.Dismissed)]
    [InlineData("陳志明遭起訴後，檢方撤回起訴。", LegalStage.Dismissed)]                // the last stage wins
    [InlineData("陳志明涉嫌詐欺，最重可判處十年有期徒刑。", LegalStage.Unknown)]       // possibility, not a judgment
    [InlineData("檢方請求從重判處陳志明。", LegalStage.Unknown)]                      // request, not a judgment
    [InlineData("陳志明堅稱無罪。", LegalStage.Unknown)]                              // claim, not an acquittal
    [InlineData("陳志明涉嫌詐欺。同案其他被告已遭起訴。", LegalStage.Unknown)]          // co-defendant's indictment
    [InlineData("陳志明一審判處有期徒刑二年，二審改判無罪。", LegalStage.Acquitted)]   // the last stage wins
    [InlineData("法院判決陳志明無罪定讞。", LegalStage.Acquitted)]
    [InlineData("陳志明遭起訴，一審判處有期徒刑三年。", LegalStage.Convicted)]
    [InlineData("檢方已起訴。", LegalStage.Indicted)]                                 // subjectless: the person's
    [InlineData("陳志明因恐嚇取財罪被判處有期徒刑一年。", LegalStage.Convicted)]       // 恐嚇 is an offence, not 恐 the modal
    [InlineData("陳志明涉及恐怖活動遭判處有期徒刑五年。", LegalStage.Convicted)]
    [InlineData("陳志明要求和解未果，遭判處有期徒刑一年。", LegalStage.Convicted)]     // 求 in another clause
    [InlineData("陳志明因可疑交易遭檢方起訴。", LegalStage.Indicted)]                  // 可疑 is no modal
    [InlineData("陳志明要求和解遭判處有期徒刑。", LegalStage.Convicted)]               // nor 要求, 訴求 or 許可 in the same clause
    [InlineData("陳志明因訴求勞資爭議被起訴。", LegalStage.Indicted)]
    [InlineData("陳志明未經許可吸金遭起訴。", LegalStage.Indicted)]
    [InlineData("檢方請求法院依法從重判處陳志明。", LegalStage.Unknown)]               // a request, however far before
    [InlineData("陳志明尚未起訴。", LegalStage.Unknown)]                              // negations and questions are no stage
    [InlineData("陳志明並未被起訴。", LegalStage.Unknown)]
    [InlineData("陳志明沒有遭到起訴。", LegalStage.Unknown)]
    [InlineData("檢方尚在評估是否起訴陳志明。", LegalStage.Unknown)]
    [InlineData("陳志明未遭判刑。", LegalStage.Unknown)]
    [InlineData("檢方對陳志明不予起訴。", LegalStage.Dismissed)]
    [InlineData("陳志明不可能被起訴。", LegalStage.Unknown)]
    [InlineData("檢方擬起訴陳志明。", LegalStage.Unknown)]                             // a plan, a condition, hearsay
    [InlineData("陳志明若遭起訴將辭職。", LegalStage.Unknown)]
    [InlineData("據傳陳志明已遭起訴。", LegalStage.Unknown)]
    [InlineData("陳志明稱「我早就被判刑了」。", LegalStage.Unknown)]                   // quoted speech is a claim
    [InlineData("陳志明說：「李四已被判刑」。", LegalStage.Unknown)]
    [InlineData("陳志明與李四涉詐欺，李四遭起訴。", LegalStage.Unknown)]                // another person's clause
    [InlineData("陳志明涉詐欺；李四遭起訴。", LegalStage.Unknown)]
    [InlineData("陳志明涉詐欺，林姓男子被判處有期徒刑一年。", LegalStage.Unknown)]
    [InlineData("陳志明涉詐欺，陳男遭起訴。", LegalStage.Indicted)]                    // the person's own surname: the person
    [InlineData("陳志明涉詐欺，隨後李四也遭起訴。", LegalStage.Unknown)]
    [InlineData("陳志明涉詐欺，施用毒品遭起訴。", LegalStage.Indicted)]                // a clause opening with a word, not a name
    [InlineData("陳志明涉詐欺，高額詐貸遭起訴。", LegalStage.Indicted)]
    [InlineData("陳志明涉貪，任內涉貪遭起訴。", LegalStage.Indicted)]
    [InlineData("陳志明涉詐欺，他曾涉案遭起訴。", LegalStage.Indicted)]
    [InlineData("陳志明遭起訴，地方法院一審被判處有期徒刑一年。", LegalStage.Convicted)]
    [InlineData("綽號「阿明」的陳志明遭判處有期徒刑一年。", LegalStage.Convicted)]   // a quote does not hide the name around it
    [InlineData("陳志明遭起訴，一審遭判處有期徒刑一年。", LegalStage.Convicted)]          // 一審 is no person
    [InlineData("恐龍法官判處陳志明有期徒刑一年。", LegalStage.Convicted)]              // 恐龍 and 可是 are no modals
    [InlineData("陳志明可是仍遭起訴。", LegalStage.Indicted)]
    [InlineData("陳志明恐遭起訴。", LegalStage.Unknown)]                              // still a possibility…
    [InlineData("陳志明可能遭到起訴。", LegalStage.Unknown)]
    [InlineData("檢方偵辦後，陳志明可能遭到起訴。", LegalStage.Alleged)]               // …so only the investigation counts
    public void legal_stage_from_news(string news, LegalStage expected) =>
        Assert.Equal(expected, Assert.Single(SourceConditions.Determine(Profile("Adverse Media"), false, news)).LegalStage);

    [Fact]
    public void gc_12_stays_indicted()
    {
        var a = CaseAnalyzer.Analyze(TxtCaseReader.Read(GoldenCases.GoldenCaseTests.Folder("GC-12")));
        Assert.Equal(LegalStage.Indicted, Assert.Single(a.Hits).Conditions.Single(c => c.Type == ListType.AdverseMedia).LegalStage);
    }

    // ② never sets aside a predicate offence.

    [Theory]
    [InlineData("〔記者丙／台北報導〕陳志明酒駕肇事逃逸致人於死。", AmlRelevance.Uncertain)]
    [InlineData("〔記者丙／台北報導〕陳志明酒駕撞人致重傷。", AmlRelevance.Uncertain)]
    [InlineData("〔記者丙／台北報導〕陳志明酒駕，警方依公共危險罪移送。", AmlRelevance.Unrelated)]
    [InlineData("〔記者丙／台北報導〕陳志明遭警方約談。同車友人王大同酒駕被捕。", AmlRelevance.Uncertain)] // someone else's conduct
    [InlineData("〔記者丙／台北報導〕陳志明酒駕，並涉竊盜。", AmlRelevance.Uncertain)]
    [InlineData("〔記者丙／台北報導〕陳志明酒駕，另涉違反證券交易法。", AmlRelevance.Uncertain)]
    [InlineData("〔記者丙／台北報導〕陳志明肇事。", AmlRelevance.Uncertain)]                       // 肇事 alone states no conduct
    public void relevance_from_news(string news, AmlRelevance expected) =>
        Assert.Equal(expected, Assert.Single(SourceConditions.Determine(Profile("Adverse Media"), false, news)).AmlRelevance);

    // ① counts an UNKNOWN only for an unmapped category with no PEP flag NO.

    [Fact]
    public void pep_no_and_an_undecided_blacklist_type_give_no_foreign_pep_presumption()
    {
        var hit = Analyze(Profile("Special Interest Person (SIP)", citizenship: "VN"), Flags(FlagValue.No, FlagValue.Yes, FlagValue.No), subject: Subject("VN"));
        Assert.Contains(hit.Conditions, c => c.Type == ListType.Unknown && c.ReasonCodes.Contains(SourceConditions.BlacklistTypeUndecided));
        Assert.DoesNotContain(DispositionPolicy.ForeignPepPresumption, hit.Disposition.ReasonCodes);
    }

    [Fact]
    public void unmapped_category_with_pep_flag_no_gives_no_presumption_and_without_flags_still_does()
    {
        var unmapped = Condition(ListType.Unknown, ConditionStatus.Unknown, basis: ConditionBasis.Unmapped, codes: SourceConditions.UnmappedCategory);
        var pepNo = Condition(ListType.Pep, ConditionStatus.NotHit, basis: ConditionBasis.Flag);
        var s = Subject("VN");
        var p = Profile("Special Interest Person (SIP)", citizenship: "VN");
        Assert.DoesNotContain(DispositionPolicy.ForeignPepPresumption, DispositionPolicy.Recommend(s, p, [unmapped, pepNo], Identity(), false).ReasonCodes);
        Assert.Equal(Recommendation.OnList, DispositionPolicy.Recommend(s, p, [unmapped], Identity(), false).Recommendation);
    }

    // ② sets aside the unrelated adverse media only: NOT_ON_LIST needs every remaining condition NOT_HIT.

    [Fact]
    public void article_22_2_gives_no_recommendation_while_an_unknown_sanction_remains()
    {
        var unrelated = Condition(ListType.AdverseMedia, relevance: AmlRelevance.Unrelated);
        var unknown = DispositionPolicy.Recommend(Subject(), Profile("Sanctions Lists"), [unrelated, Condition(ListType.Sanction, ConditionStatus.Unknown)], Identity(), false);
        Assert.Null(unknown.Recommendation);
        Assert.Equal([DispositionPolicy.AdverseUnrelated, DispositionPolicy.NotDetermined], unknown.ReasonCodes);

        var notHit = DispositionPolicy.Recommend(Subject(), Profile("Sanctions Lists"), [unrelated, Condition(ListType.Sanction, ConditionStatus.NotHit)], Identity(), false);
        Assert.Equal(Recommendation.NotOnList, notHit.Recommendation);
        Assert.Equal([DispositionPolicy.AdverseUnrelated], notHit.ReasonCodes);
    }

    // pending checks: IDENTITY_CHECK for a PEP without DOB that is no RCA; JUDGMENT_PARTY_IDENTITY for a person with news or adverse media.
    [Theory]
    [InlineData("Pep", false, false, "Person", "EMPLOYMENT_RELEVANCE,IDENTITY_CHECK")]
    [InlineData("Pep", true, false, "Person", "EMPLOYMENT_RELEVANCE")]
    [InlineData("Pep,Rca", false, false, "Person", "EMPLOYMENT_RELEVANCE")]
    [InlineData("AdverseMedia", false, true, "Person", "REGION_RELEVANCE,JUDGMENT_PARTY_IDENTITY")]
    [InlineData("AdverseMedia", false, false, "Entity", "REGION_RELEVANCE")]
    [InlineData("Sanction", false, true, "Person", "JUDGMENT_PARTY_IDENTITY")]
    [InlineData("Sanction", false, false, "Person", "")]
    public void pending_checks_table(string types, bool dob, bool news, string recordType, string expected)
    {
        var profile = Profile("Politically Exposed Person") with { DatesOfBirth = dob ? [new PartialDate(1980, 5, 20)] : [], RecordType = recordType };
        var conditions = types.Split(',').Select(t => Condition(Enum.Parse<ListType>(t))).ToList();
        Assert.Equal(expected, string.Join(",", DispositionPolicy.Recommend(Subject(), profile, conditions, Identity(), news).PendingChecks));
    }

    // a NOT_HIT controlled condition is no release control; a NOT_HIT Sanction does not raise the review priority.

    [Fact]
    public void not_hit_pep_beside_a_closed_adverse_media_pair_stays_no_hit()
    {
        var pair = AssessmentPolicy.Assess([Condition(ListType.Pep, ConditionStatus.NotHit), Condition(ListType.AdverseMedia)], Identity(IdentityDecision.NoMatch));
        Assert.Equal((ScreeningAssessment.NoHit, (ReviewPriority?)null), (pair.Result, pair.Priority));
        Assert.Empty(pair.ReasonCodes);
    }

    [Fact]
    public void not_hit_sanction_does_not_raise_priority()
    {
        var pair = AssessmentPolicy.Assess([Condition(ListType.Sanction, ConditionStatus.NotHit), Condition(ListType.AdverseMedia)], Identity());
        Assert.Equal((ScreeningAssessment.Review, ReviewPriority.Low), (pair.Result, pair.Priority));
        var unknown = AssessmentPolicy.Assess([Condition(ListType.Sanction, ConditionStatus.Unknown)], Identity());
        Assert.Equal(ReviewPriority.High, unknown.Priority);
    }

    // monotone risk scoring.

    private static readonly RiskPolicy Policy = new("risk-test", Approved: true,
        new Dictionary<ListType, int> { [ListType.AdverseMedia] = 10, [ListType.Sanction] = 50, [ListType.Pep] = 20 },
        new Dictionary<string, int>(),
        new Dictionary<LegalStage, int> { [LegalStage.Indicted] = 10, [LegalStage.Wanted] = 10 },
        MediumFrom: 15, HighFrom: 100);

    private static RiskAssessment Score(params SourceCondition[] c) => RiskScoring.Assess(c, Identity(), Policy)!;

    [Fact]
    public void unknown_sanction_or_pep_scores_no_lower_than_hit()
    {
        Assert.Equal(Score(Condition(ListType.Sanction)).Score, Score(Condition(ListType.Sanction, ConditionStatus.Unknown)).Score);
        Assert.Equal(Score(Condition(ListType.Pep)).Score, Score(Condition(ListType.Pep, ConditionStatus.Unknown)).Score);
    }

    [Fact]
    public void legal_stage_weighs_on_adverse_media_only()
    {
        Assert.Equal(0, Score(Condition(ListType.OfficialWantedList, stage: LegalStage.Wanted)).Score); // WANTED is weighted, but not here
        Assert.Equal(20, Score(Condition(ListType.AdverseMedia, stage: LegalStage.Indicted)).Score);
    }

    [Fact]
    public void condition_set_aside_by_article_22_2_scores_nothing()
    {
        Assert.Equal(0, Score(Condition(ListType.AdverseMedia, stage: LegalStage.Indicted, relevance: AmlRelevance.Unrelated)).Score);
    }

    [Fact]
    public void contributions_carry_list_and_stage_prefixes()
    {
        Assert.Equal(["list:AdverseMedia+10", "stage:Indicted+10"], Score(Condition(ListType.AdverseMedia, stage: LegalStage.Indicted)).Contributions);
    }

    [Fact]
    public void unknown_condition_other_than_sanction_or_pep_scores_nothing()
    {
        var risk = Score(Condition(ListType.AdverseMedia, ConditionStatus.Unknown, LegalStage.Indicted));
        Assert.Equal((0, RiskLevel.Low), (risk.Score, risk.Level));
        Assert.Empty(risk.Contributions);
    }

    // MediumFrom 15, HighFrom 40: each threshold is reached at its own value.
    [Theory]
    [InlineData(14, RiskLevel.Low)]
    [InlineData(15, RiskLevel.Medium)]
    [InlineData(16, RiskLevel.Medium)]
    [InlineData(39, RiskLevel.Medium)]
    [InlineData(40, RiskLevel.High)]
    [InlineData(41, RiskLevel.High)]
    public void risk_level_turns_at_medium_from_and_high_from(int points, RiskLevel expected)
    {
        var policy = Policy with { ListWeights = new Dictionary<ListType, int> { [ListType.AdverseMedia] = points }, HighFrom = 40 };
        var risk = RiskScoring.Assess([Condition(ListType.AdverseMedia)], Identity(), policy)!;
        Assert.Equal((points, expected), (risk.Score, risk.Level));
    }

    [Fact]
    public void any_raised_sanction_is_high()
    {
        Assert.Equal(RiskLevel.High, Score(Condition(ListType.Sanction)).Level);                         // 50 < HighFrom 100
        Assert.Equal(RiskLevel.High, Score(Condition(ListType.Sanction, ConditionStatus.Unknown)).Level);
        Assert.Equal(RiskLevel.Low, Score(Condition(ListType.Sanction, ConditionStatus.NotHit)).Level);
    }

    // former PEP.

    [Fact]
    public void inactive_pep_is_former_with_an_influence_check_and_still_hit()
    {
        var hit = Analyze(Profile("Politically Exposed Person", "Inactive PEP"));
        var pep = Assert.Single(hit.Conditions);
        Assert.Equal((ConditionStatus.Hit, "FORMER_PEP"), (pep.Status, pep.PepType));
        Assert.Contains(DispositionPolicy.FormerPepInfluence, hit.Disposition.PendingChecks);
    }

    [Fact]
    public void every_role_ended_is_former_and_the_rca_of_a_former_pep_gets_the_check()
    {
        var ended = Analyze(Profile("Politically Exposed Person", roles: [new ProfileRole("部長", "某部", new PartialDate(2010), new PartialDate(2014))]));
        Assert.Equal("FORMER_PEP", Assert.Single(ended.Conditions).PepType);
        Assert.Contains(DispositionPolicy.FormerPepInfluence, ended.Disposition.PendingChecks);

        var rca = Analyze(Profile("Relative or Close Associate (RCA)", "Spouse of Former PEP"));
        Assert.Equal("PEP_FAMILY", Assert.Single(rca.Conditions).PepType);
        Assert.Contains(DispositionPolicy.FormerPepInfluence, rca.Disposition.PendingChecks);

        Assert.DoesNotContain(DispositionPolicy.FormerPepInfluence, Analyze(Profile("Politically Exposed Person")).Disposition.PendingChecks);
    }

    // Analysis date 2026-11-04: a role ends only when the last day its end date stands for is before it. The date is the
    // CaseService clock's (UTC), passed in; a call that gives none uses today.
    [Theory]
    [InlineData(2025, null, null, true)]
    [InlineData(2026, null, null, false)]
    [InlineData(2026, 10, null, true)]
    [InlineData(2026, 11, null, false)]
    [InlineData(2026, 11, 3, true)]
    [InlineData(2026, 11, 4, false)]
    public void a_role_has_ended_only_when_its_end_date_lies_before_the_analysis_date(int year, int? month, int? day, bool former) =>
        Assert.Equal(former, SourceConditions.IsFormerPep(
            Profile("Politically Exposed Person", roles: [new ProfileRole("部長", "某部", new PartialDate(2010), new PartialDate(year, month, day))]),
            ownRoles: true, new DateOnly(2026, 11, 4)));

    [Fact]
    public void a_clause_of_many_keywords_is_read_in_linear_time()
    {
        // The modal was looked for again before every keyword: 200,000 起訴 in one clause took 13 s.
        var started = System.Diagnostics.Stopwatch.StartNew();
        var news = "陳志明" + string.Concat(Enumerable.Repeat("起訴", 200_000));
        Assert.Equal(LegalStage.Indicted, Assert.Single(SourceConditions.Determine(Profile("Adverse Media"), false, news)).LegalStage);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3), $"took {started.Elapsed}");
    }

    [Fact]
    public void unclosed_quotes_are_read_in_linear_time()
    {
        // A backtracking quote pattern rescanned to the end from every unclosed 「.
        var started = System.Diagnostics.Stopwatch.StartNew();
        var news = "陳志明遭起訴" + new string('「', 1_000_000);
        Assert.Equal(LegalStage.Indicted, Assert.Single(SourceConditions.Determine(Profile("Adverse Media"), false, news)).LegalStage);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3), $"took {started.Elapsed}");
    }

    [Fact]
    public void the_analysis_takes_its_date_from_the_caller_not_the_machine_clock()
    {
        var profile = Profile("Politically Exposed Person", roles: [new ProfileRole("部長", "某部", new PartialDate(2010), new PartialDate(2026, 6, 30))]);
        var before = Analyze(profile, asOf: new DateOnly(2026, 6, 30));
        var after = Analyze(profile, asOf: new DateOnly(2026, 7, 1));
        Assert.Equal((null, "FORMER_PEP"), (Assert.Single(before.Conditions).PepType, Assert.Single(after.Conditions).PepType));
        Assert.DoesNotContain(DispositionPolicy.FormerPepInfluence, before.Disposition.PendingChecks);
        Assert.Contains(DispositionPolicy.FormerPepInfluence, after.Disposition.PendingChecks);
    }

    [Fact]
    public void a_pep_with_one_ended_and_one_current_role_is_not_former()
    {
        var asOf = new DateOnly(2026, 11, 4);
        bool Former(params PartialDate?[] ends) => SourceConditions.IsFormerPep(
            Profile("Politically Exposed Person", roles: ends.Select(to => new ProfileRole("部長", "某部", new PartialDate(2010), to)).ToList()), ownRoles: true, asOf);

        Assert.False(Former(new PartialDate(2014), null));
        Assert.False(Former(new PartialDate(2014), new PartialDate(2030)));
        Assert.True(Former(new PartialDate(2014), new PartialDate(2018)));
    }

    // RCA statutory scope (認定標準第 6 條).

    [Theory]
    [InlineData("Cousin")]
    [InlineData("堂兄弟")]
    public void relation_outside_the_statutory_family_is_flagged_and_still_hit(string sub)
    {
        var rca = Assert.Single(Analyze(Profile("Relative or Close Associate (RCA)", sub)).Conditions);
        Assert.Equal(ConditionStatus.Hit, rca.Status);
        Assert.NotEqual("PEP_FAMILY", rca.PepType);
        Assert.Contains(SourceConditions.RcaOutsideStatutoryScope, rca.ReasonCodes);
    }

    [Fact]
    public void spouse_stays_pep_family()
    {
        var rca = Assert.Single(Analyze(Profile("Relative or Close Associate (RCA)", "Spouse")).Conditions);
        Assert.Equal("PEP_FAMILY", rca.PepType);
        Assert.DoesNotContain(SourceConditions.RcaOutsideStatutoryScope, rca.ReasonCodes);
    }

    // every disposition in the analysis JSON is advisory.

    [Fact]
    public void every_disposition_in_the_analysis_json_is_advisory()
    {
        var input = TxtCaseReader.Read(GoldenCases.GoldenCaseTests.Folder("GC-01"));
        var analysis = CaseAnalyzer.Analyze(input);
        foreach (var json in new[] { JsonSerializer.Serialize(analysis, CaseJson.Options), JsonSerializer.Serialize(ExplanationBuilder.Build(input, analysis), CaseJson.Options) })
        {
            var dispositions = JsonDocument.Parse(json).RootElement.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("disposition")).ToList();
            Assert.NotEmpty(dispositions);
            Assert.All(dispositions, d => Assert.True(d.GetProperty("advisory").GetBoolean()));
        }
    }

    // Is RCA? = NO, wanted-list entries and unmapped nationalities.

    [Fact]
    public void An_rca_category_with_is_rca_no_stays_for_review_with_a_conflict()
    {
        var no = SourceConditions.Determine(Profile("Relative or Close Associate (RCA)", "Spouse"), false, null, ListFlags.None with { Rca = FlagValue.No });
        var rca = Assert.Single(no, c => c.Type == ListType.Rca);
        Assert.Equal(ConditionStatus.Unknown, rca.Status);
        Assert.Contains(SourceConditions.FlagCategoryConflict, rca.ReasonCodes);

        var blank = SourceConditions.Determine(Profile("Relative or Close Associate (RCA)", "Spouse"), false, null, ListFlags.None);
        Assert.Equal(ConditionStatus.Hit, Assert.Single(blank, c => c.Type == ListType.Rca).Status);
    }

    [Fact]
    public void An_active_wanted_list_entry_does_not_confirm_a_sanction_measure()
    {
        SourceCondition Sanction(string list) => SourceConditions.Determine(
            Profile("Sanctions Lists", list: new OfficialListEntry("MJIB", list, "Active", null)), false, null).Single(c => c.Type == ListType.Sanction);

        Assert.Equal(ConditionStatus.Hit, Sanction("SDN List").Status);
        var wanted = Sanction("Wanted List");
        Assert.Equal(ConditionStatus.Unknown, wanted.Status);
        Assert.Contains(SourceConditions.SanctionMeasureNotConfirmed, wanted.ReasonCodes);
    }

    [Fact]
    public void An_unmapped_nationality_keeps_the_foreign_pep_presumption_off()
    {
        var pep = Condition(ListType.Pep);
        var profile = Profile("Politically Exposed Person", citizenship: "VN");
        Assert.Equal(Recommendation.OnList, DispositionPolicy.Recommend(Subject("VN"), profile, [pep], Identity(), false).Recommendation);
        var ambiguous = DispositionPolicy.Recommend(Subject("VN") with { Findings = ["AMBIGUOUS:國籍 2"] }, profile, [pep], Identity(), false);
        Assert.DoesNotContain(DispositionPolicy.ForeignPepPresumption, ambiguous.ReasonCodes);
    }
}
