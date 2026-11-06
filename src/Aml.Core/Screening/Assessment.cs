using Aml.Core.Identity;

namespace Aml.Core.Screening;

/// <summary>Declaration order is the case-level precedence, lowest first.</summary>
public enum ScreeningAssessment { NotApplicable, NoHit, Review, Hit, NotEvaluated }

public enum ReviewPriority { High, Normal, Low }

public sealed record PairAssessment(ScreeningAssessment Result, ReviewPriority? Priority, IReadOnlyList<string> ReasonCodes);

/// <summary>Canonical combination matrix (data-contracts.md, ADR-0004) plus the Phase 1 release controls.</summary>
public static class AssessmentPolicy
{
    public const string ReleaseControlHumanReview = "RELEASE_CONTROL_HUMAN_REVIEW";
    public const string SourceConditionUnknown = "SOURCE_CONDITION_UNKNOWN";

    private const int PartialSupportOnlyStage3Row = 5;

    public static ScreeningAssessment Combine(ConditionStatus status, IdentityDecision identity) => status switch
    {
        ConditionStatus.NotHit => ScreeningAssessment.NotApplicable,
        // Phase 1 has no identity MATCH, so a system HIT never occurs; PROBABLE_MATCH surfaces as REVIEW.
        _ => identity == IdentityDecision.NoMatch ? ScreeningAssessment.NoHit : ScreeningAssessment.Review,
    };

    public static PairAssessment Assess(IReadOnlyList<SourceCondition> conditions, IdentityResult identity)
    {
        var reasons = new List<string>();
        var result = conditions.Count == 0
            ? ScreeningAssessment.NotApplicable
            : conditions.Max(c => Combine(c.Status, identity.Decision));
        if (conditions.Any(c => c.Status == ConditionStatus.Unknown)) reasons.Add(SourceConditionUnknown);

        if (result == ScreeningAssessment.NoHit && conditions.Any(IsUnderReleaseControl))
        {
            result = ScreeningAssessment.Review;
            reasons.Add(ReleaseControlHumanReview);
        }

        return new PairAssessment(result, result == ScreeningAssessment.Review ? Priority(identity, conditions) : null, reasons);
    }

    // Always human review until GC-04 / GC-05 are validated.
    private static bool IsUnderReleaseControl(SourceCondition c) =>
        c.Type is ListType.Pep or ListType.Rca or ListType.Sanction && IsRaised(c);

    private static bool IsRaised(SourceCondition c) => c.Status != ConditionStatus.NotHit;

    private static ReviewPriority Priority(IdentityResult identity, IReadOnlyList<SourceCondition> conditions) =>
        identity.Decision == IdentityDecision.ProbableMatch
            || conditions.Any(c => c.Type == ListType.Sanction && IsRaised(c)) ? ReviewPriority.High
        : identity.Stage3Row == PartialSupportOnlyStage3Row ? ReviewPriority.Normal
        : ReviewPriority.Low;
}
