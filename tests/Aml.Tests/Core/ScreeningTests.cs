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
}
