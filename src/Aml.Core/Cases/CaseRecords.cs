using Aml.Core.Identity;
using Aml.Core.Screening;

namespace Aml.Core.Cases;

public enum CaseStatus { Open, InReview, Closed }

public enum ExecutionStatus { Pending, Running, Completed, Partial, Failed }

public sealed record CaseRecord(
    string CaseId, string SubjectRef, string TriggerEventId, CaseStatus Status,
    int CurrentRevision, int? EffectiveRevision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record AnalysisRecord(
    string CaseId, int Revision, ExecutionStatus ExecutionStatus, ScreeningAssessment CaseResult, string ResultJson, string? ExplanationJson, DateTimeOffset CreatedAt);

public static class CaseLifecycle
{
    // Hard-conflict rules validated by a Golden Case may close a pair automatically.
    private static readonly HashSet<string> ValidatedAutomaticRules = [Shared.ReasonCodes.ImpossibleAgeAtReportedEvent];

    /// <summary>Case-level aggregate by precedence NOT_EVALUATED > HIT > REVIEW > NO_HIT > NOT_APPLICABLE; zero hits is NO_HIT.</summary>
    public static ScreeningAssessment Aggregate(CaseAnalysis a) =>
        a.NotEvaluatedProfiles.Count > 0 ? ScreeningAssessment.NotEvaluated
        : a.Hits.Count == 0 ? ScreeningAssessment.NoHit
        : a.Hits.Max(h => h.PairResult);

    /// <summary>An incomplete revision is persisted but never published as the effective result.</summary>
    public static ExecutionStatus Execution(CaseAnalysis a) =>
        a.NotEvaluatedProfiles.Count > 0 ? ExecutionStatus.Partial : ExecutionStatus.Completed;

    /// <summary>Status after a COMPLETED analysis (case-management.md, lifecycle table).</summary>
    public static CaseStatus AfterAnalysis(CaseAnalysis a, CaseStatus current) => Aggregate(a) switch
    {
        ScreeningAssessment.NotEvaluated => current,
        ScreeningAssessment.Hit or ScreeningAssessment.Review => CaseStatus.InReview,
        _ when a.Hits.Where(h => h.PairResult == ScreeningAssessment.NoHit)
                     .All(h => h.Identity.Decision == IdentityDecision.NoMatch && ValidatedAutomaticRules.Contains(h.Identity.HardConflict ?? "")) => CaseStatus.Closed,
        _ => CaseStatus.InReview,
    };
}
