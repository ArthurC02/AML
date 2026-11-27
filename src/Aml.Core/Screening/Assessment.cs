using Aml.Core.Identity;

namespace Aml.Core.Screening;

/// <summary>Declaration order is the case-level precedence, lowest first.</summary>
public enum ScreeningAssessment { NotApplicable, NoHit, Review, Hit, NotEvaluated }

public enum ReviewPriority { High, Normal, Low }

public sealed record PairAssessment(ScreeningAssessment Result, ReviewPriority? Priority, IReadOnlyList<string> ReasonCodes);

/// <summary>
/// Canonical combination matrix (data-contracts.md, ADR-0004), the same for every list type: an identity NO_MATCH gives
/// NO_HIT on a Sanction, PEP or RCA condition as on any other (no blanket human review, the user's decision of 2026-11-27).
/// </summary>
public static class AssessmentPolicy
{
    public const string SourceConditionUnknown = "SOURCE_CONDITION_UNKNOWN";
    public const string AttachedNewsDisagreesWithProfile = "ATTACHED_NEWS_DISAGREES_WITH_PROFILE";

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

        var newsDisagrees = result == ScreeningAssessment.Review && NewsDisagreesWithProfile(identity);
        if (newsDisagrees) reasons.Add(AttachedNewsDisagreesWithProfile);
        return new PairAssessment(result, result == ScreeningAssessment.Review ? Priority(identity, conditions, newsDisagrees) : null, reasons);
    }

    /// <summary>
    /// The attached news states, next to the Profile person's name, a birth date or gender that differs from the customer's
    /// while the Profile's own field agrees: the two sources disagree with each other, so the news may be about someone
    /// else. The identity decision stands on the Profile's fields; the pair says so and is not first in line.
    /// </summary>
    public static bool NewsDisagreesWithProfile(IdentityResult identity) =>
        identity.Evidence.Values.Any(e => e is { Group: Shared.EvidenceGroup.StatedBirthDate or Shared.EvidenceGroup.StatedGender, Status: Shared.ComparisonStatus.Uncertain }
            && e.Detail is Shared.ReasonCodes.NewsBirthDateDifference or Shared.ReasonCodes.NewsGenderDifference);

    private static bool IsRaised(SourceCondition c) => c.Status != ConditionStatus.NotHit;

    private static ReviewPriority Priority(IdentityResult identity, IReadOnlyList<SourceCondition> conditions, bool newsDisagrees) =>
        conditions.Any(c => c.Type == ListType.Sanction && IsRaised(c)) ? ReviewPriority.High
        : identity.Decision == IdentityDecision.ProbableMatch ? newsDisagrees ? ReviewPriority.Normal : ReviewPriority.High
        : identity.Stage3Row == Stage3Rows.PartialSupport ? ReviewPriority.Normal
        : ReviewPriority.Low;
}
