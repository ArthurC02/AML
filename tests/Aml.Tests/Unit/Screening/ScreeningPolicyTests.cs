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

    private static HitResult Analyze(SourceProfile p, ListFlags? flags = null, bool isRca = false, ScreenedSubject? subject = null) =>
        CaseAnalyzer.Analyze(new CaseInput(subject ?? Subject(), [new CandidateRow("陳志明", "1", null, isRca, flags)],
            new Dictionary<string, SourceProfile> { ["1"] = p }, new Dictionary<string, NewsItem>(), [])).Hits[0];

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

    // a NOT_HIT Sanction does not raise the review priority.

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
        foreach (var json in new[] { JsonSerializer.Serialize(analysis, CaseService.Json), JsonSerializer.Serialize(ExplanationBuilder.Build(input, analysis), CaseService.Json) })
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
