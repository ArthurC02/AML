using Aml.Core.Cases;
using Aml.Core.Identity;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Tests.Core;

/// <summary>Combination matrix, Sanction/PEP policies and Phase 1 release controls, on minimal synthetic data.</summary>
public class ScreeningTests
{
    private static readonly PartialDate Dob = new(1980, 5, 20);

    private static ScreenedSubject Subject() =>
        new(SubjectRole.Customer, null, ["陳志明"], Gender.Male, Dob, ["TW"], null, null, null, null, [], []);

    private static SourceProfile Profile(string category, string? sub = null, OfficialListEntry? list = null,
        IReadOnlyList<PartialDate>? dobs = null, IReadOnlyList<ProfileRole>? roles = null) =>
        new("DJ:1", Gender.Male, ["陳志明"], dobs ?? [], ["TW"], [], [new SourceCategory(category, sub)], [], roles ?? [], [], list, [], []);

    private static HitResult Analyze(SourceProfile p, bool isRca = false) =>
        CaseAnalyzer.Analyze(new CaseInput(Subject(), [new CandidateRow("陳志明", "1", null, isRca)],
            new Dictionary<string, SourceProfile> { ["1"] = p }, new Dictionary<string, NewsItem>(), [])).Hits[0];

    [Theory]
    [InlineData(ConditionStatus.Hit, IdentityDecision.NoMatch, ScreeningAssessment.NoHit)]
    [InlineData(ConditionStatus.Hit, IdentityDecision.Review, ScreeningAssessment.Review)]
    [InlineData(ConditionStatus.Hit, IdentityDecision.ProbableMatch, ScreeningAssessment.Review)]
    [InlineData(ConditionStatus.NotHit, IdentityDecision.ProbableMatch, ScreeningAssessment.NotApplicable)]
    [InlineData(ConditionStatus.Unknown, IdentityDecision.Review, ScreeningAssessment.Review)]
    [InlineData(ConditionStatus.Unknown, IdentityDecision.NoMatch, ScreeningAssessment.NoHit)]
    public void Combination_matrix(ConditionStatus status, IdentityDecision identity, ScreeningAssessment expected) =>
        Assert.Equal(expected, AssessmentPolicy.Combine(status, identity));

    [Fact]
    public void Sanction_without_an_active_named_measure_is_unknown_not_hit()
    {
        var c = Assert.Single(Analyze(Profile("Sanctions Lists")).Conditions);
        Assert.Equal(ListType.Sanction, c.Type);
        Assert.Equal(ConditionStatus.Unknown, c.Status);
        Assert.Contains(SourceConditions.SanctionMeasureNotConfirmed, c.ReasonCodes);

        var active = Assert.Single(Analyze(Profile("Sanctions Lists", list: new OfficialListEntry("OFAC", "SDN List", "Active", null))).Conditions);
        Assert.Equal(ConditionStatus.Hit, active.Status);
        Assert.Equal("SDN List", active.Program);
        Assert.Equal("OFAC", active.Jurisdiction);
    }

    [Fact]
    public void Unmapped_category_is_unknown_and_goes_to_review()
    {
        var hit = Analyze(Profile("Special Interest Person (SIP)", "Regulatory Enforcement List"));
        var c = Assert.Single(hit.Conditions);
        Assert.Equal((ListType.Unknown, ConditionStatus.Unknown, ConditionBasis.Unmapped), (c.Type, c.Status, c.Basis));
        Assert.Equal(ScreeningAssessment.Review, hit.PairResult);
        Assert.Contains(AssessmentPolicy.SourceConditionUnknown, hit.PairReasonCodes);
    }

    [Theory]
    [InlineData("Politically Exposed Person", "Family Member", "PEP_FAMILY")]
    [InlineData("Politically Exposed Person", "Close Associate", "PEP_CLOSE_ASSOCIATE")]
    [InlineData("Politically Exposed Person", "Former PEP", "FORMER_PEP")]
    [InlineData("Politically Exposed Person", null, null)]
    [InlineData("Relative or Close Associate (RCA)", "Spouse", "PEP_FAMILY")]
    [InlineData("Relative or Close Associate (RCA)", null, "RCA_UNCONFIRMED")]
    public void Pep_subtype_comes_from_the_subcategory(string category, string? sub, string? expected) =>
        Assert.Equal(expected, Assert.Single(SourceConditions.Determine(Profile(category, sub), false, null)).PepType);

    [Fact]
    public void Pep_and_rca_candidates_stay_in_review_even_after_a_hard_conflict()
    {
        // Chairman in 1990 while the customer was 10: a validated hard conflict, but PEP is under the release control.
        var roles = new[] { new ProfileRole("董事長", "某公司", new PartialDate(1990, 7)) };
        var pep = Analyze(Profile("Politically Exposed Person", "Former PEP", roles: roles));
        Assert.Equal(IdentityDecision.NoMatch, pep.Identity.Decision);
        Assert.Equal(ScreeningAssessment.Review, pep.PairResult);
        Assert.Contains(AssessmentPolicy.ReleaseControlHumanReview, pep.PairReasonCodes);
        Assert.Equal("FORMER_PEP", pep.Conditions[0].PepType);

        var rca = Analyze(Profile("Special Interest Person (SIP)", "Fraud", roles: roles), isRca: true);
        Assert.Contains(rca.Conditions, c => c.Type == ListType.Rca && c.PepType == "RCA_UNCONFIRMED");
        Assert.Equal(ScreeningAssessment.Review, rca.PairResult);

        // The same hard conflict on adverse media alone closes the pair automatically.
        Assert.Equal(ScreeningAssessment.NoHit, Analyze(Profile("Special Interest Person (SIP)", "Fraud", roles: roles)).PairResult);
    }

    [Fact]
    public void Review_priority_follows_the_stage_3_row_and_sanction()
    {
        Assert.Equal(ReviewPriority.High, Analyze(Profile("Special Interest Person (SIP)", "Fraud", dobs: [new PartialDate(1980)])).ReviewPriority); // row 4
        Assert.Equal(ReviewPriority.Low, Analyze(Profile("Special Interest Person (SIP)", "Fraud")).ReviewPriority);                          // row 6
        Assert.Equal(ReviewPriority.High, Analyze(Profile("Sanctions Lists")).ReviewPriority);                                              // Sanction
        Assert.Null(Analyze(Profile("Special Interest Person (SIP)", "Fraud", roles: [new ProfileRole("董事長", "某公司", new PartialDate(1990, 7))])).ReviewPriority);
    }

    [Fact]
    public void Missing_profile_makes_the_revision_not_evaluated_and_partial()
    {
        var input = new CaseInput(Subject(), [new CandidateRow("陳志明", "1", null, false), new CandidateRow("陳志明", "2", null, false)],
            new Dictionary<string, SourceProfile> { ["1"] = Profile("Special Interest Person (SIP)", "Fraud") }, new Dictionary<string, NewsItem>(), []);
        var a = CaseAnalyzer.Analyze(input);

        Assert.Equal(["DJ:2"], a.NotEvaluatedProfiles);
        Assert.Equal(ScreeningAssessment.NotEvaluated, CaseLifecycle.Aggregate(a));
        Assert.Equal(ExecutionStatus.Partial, CaseLifecycle.Execution(a));
        Assert.Equal(CaseStatus.Open, CaseLifecycle.AfterAnalysis(a, CaseStatus.Open));
    }

    private static DispositionRecommendation Recommend(string[] subjectNationality, string[] profileCitizenship, IdentityDecision decision,
        params SourceCondition[] conditions)
    {
        var subject = Subject() with { Nationalities = subjectNationality };
        var profile = Profile("Politically Exposed Person") with { Citizenship = profileCitizenship };
        var identity = new IdentityResult(new Dictionary<EvidenceGroup, Evidence>(), null, 6, decision, []);
        return DispositionPolicy.Recommend(subject, profile, conditions, identity, hasNews: false);
    }

    private static SourceCondition Condition(ListType type, AmlRelevance? relevance = null) =>
        new(type, ConditionStatus.Hit, ConditionBasis.CategoryMapping, [], LegalStage.Unknown, relevance, SourceConditions.PolicyVersion, []);

    [Fact]
    public void Article_22_1_foreign_pep_is_presumed_on_list_without_changing_identity()
    {
        var d = Recommend(["VN"], ["VN"], IdentityDecision.Review, Condition(ListType.Pep));
        Assert.Equal((Recommendation.OnList, DispositionPolicy.ForeignPepPresumption), (d.Recommendation, d.ReasonCodes[0]));
        Assert.Contains(DispositionPolicy.EmploymentRelevance, d.PendingChecks);
        Assert.Contains(DispositionPolicy.IdentityCheck, d.PendingChecks); // PEP without DOB and not RCA
    }

    [Theory]
    [InlineData("TW", "VN")]
    [InlineData("VN", "TW")]
    public void Article_22_1_never_applies_when_either_side_is_taiwanese(string subject, string profile)
    {
        var d = Recommend([subject], [profile], IdentityDecision.Review, Condition(ListType.Pep));
        Assert.Null(d.Recommendation);
        Assert.DoesNotContain(DispositionPolicy.ConfirmNationality, d.PendingChecks);
    }

    [Fact]
    public void Article_22_1_with_missing_or_ambiguous_nationality_needs_confirmation()
    {
        var missing = Recommend([], ["VN"], IdentityDecision.Review, Condition(ListType.Pep));
        Assert.Null(missing.Recommendation);
        Assert.Equal(DispositionPolicy.ConfirmNationality, missing.PendingChecks[0]);
        Assert.Null(Recommend(["VN"], ["MYANMAR (BURMA)"], IdentityDecision.Review, Condition(ListType.Pep)).Recommendation);
    }

    [Fact]
    public void Article_22_2_sets_aside_only_the_unrelated_adverse_media_condition()
    {
        var alone = Recommend(["TW"], ["TW"], IdentityDecision.Review, Condition(ListType.AdverseMedia, AmlRelevance.Unrelated));
        Assert.Equal((Recommendation.NotOnList, DispositionPolicy.AdverseUnrelated), (alone.Recommendation, alone.ReasonCodes[0]));

        // A PEP condition on the same Profile survives: the foreign presumption still applies.
        var withPep = Recommend(["VN"], ["VN"], IdentityDecision.Review,
            Condition(ListType.AdverseMedia, AmlRelevance.Unrelated), Condition(ListType.Pep));
        Assert.Equal(Recommendation.OnList, withPep.Recommendation);
        Assert.Equal([DispositionPolicy.AdverseUnrelated, DispositionPolicy.ForeignPepPresumption], withPep.ReasonCodes);

        var uncertain = Recommend(["TW"], ["TW"], IdentityDecision.Review, Condition(ListType.AdverseMedia, AmlRelevance.Uncertain));
        Assert.Equal((Recommendation?)null, uncertain.Recommendation);
        Assert.Equal([DispositionPolicy.NotDetermined], uncertain.ReasonCodes);
    }

    [Fact]
    public void Identity_no_match_is_not_on_list_and_review_alone_gets_no_recommendation()
    {
        Assert.Equal(Recommendation.NotOnList, Recommend(["TW"], ["TW"], IdentityDecision.NoMatch, Condition(ListType.AdverseMedia)).Recommendation);
        Assert.Null(Recommend(["TW"], ["TW"], IdentityDecision.ProbableMatch, Condition(ListType.AdverseMedia)).Recommendation);
    }
}
