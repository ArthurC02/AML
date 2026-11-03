using Aml.Core.Identity;

namespace Aml.Core.Screening;

/// <summary>Pair and case result. Declaration order is the case-level precedence, lowest first.</summary>
public enum ScreeningAssessment { NotApplicable, NoHit, Review, Hit, NotEvaluated }

public enum ReviewPriority { High, Normal, Low }

public sealed record PairAssessment(ScreeningAssessment Result, ReviewPriority? Priority, IReadOnlyList<string> ReasonCodes);

/// <summary>Canonical combination matrix (data-contracts.md, ADR-0004) plus the Phase 1 release controls.</summary>
public static class AssessmentPolicy
{
    public const string ReleaseControlHumanReview = "RELEASE_CONTROL_HUMAN_REVIEW";
    public const string SourceConditionUnknown = "SOURCE_CONDITION_UNKNOWN";

    /// <summary>One source condition × identity decision.</summary>
    public static ScreeningAssessment Combine(ConditionStatus status, IdentityDecision identity) => status switch
    {
        ConditionStatus.NotHit => ScreeningAssessment.NotApplicable,
        // Phase 1 has no identity MATCH, so a system HIT never occurs; PROBABLE_MATCH surfaces as REVIEW.
        ConditionStatus.Hit => identity == IdentityDecision.NoMatch ? ScreeningAssessment.NoHit : ScreeningAssessment.Review,
        _ => identity == IdentityDecision.NoMatch ? ScreeningAssessment.NoHit : ScreeningAssessment.Review,
    };

    public static PairAssessment Assess(IReadOnlyList<SourceCondition> conditions, IdentityResult identity)
    {
        var reasons = new List<string>();
        var result = conditions.Count == 0
            ? ScreeningAssessment.NotApplicable
            : conditions.Max(c => Combine(c.Status, identity.Decision));
        if (conditions.Any(c => c.Status == ConditionStatus.Unknown)) reasons.Add(SourceConditionUnknown);

        // Release control: PEP, RCA and Sanction candidates always go to human review (until GC-04 / GC-05 are validated).
        var controlled = conditions.Any(c => c.Type is ListType.Pep or ListType.Rca or ListType.Sanction && c.Status != ConditionStatus.NotHit);
        if (controlled && result == ScreeningAssessment.NoHit)
        {
            result = ScreeningAssessment.Review;
            reasons.Add(ReleaseControlHumanReview);
        }

        return new PairAssessment(result, result == ScreeningAssessment.Review ? Priority(identity, conditions) : null, reasons);
    }

    // Stage 3 rows 2–4 (PROBABLE_MATCH) and Sanction first, row 5 next, rows 1 and 6 and everything else last.
    private static ReviewPriority Priority(IdentityResult identity, IReadOnlyList<SourceCondition> conditions) =>
        identity.Decision == IdentityDecision.ProbableMatch || conditions.Any(c => c.Type == ListType.Sanction) ? ReviewPriority.High
        : identity.Stage3Row == 5 ? ReviewPriority.Normal
        : ReviewPriority.Low;
}
