using Aml.Core.Identity;

namespace Aml.Core.Cases;

public enum CaseStatus { Open, InReview, Closed, Reopened }

public enum ExecutionStatus { Pending, Running, Completed, Partial, Failed }

/// <summary>Case-level aggregate, by precedence NOT_EVALUATED > HIT > REVIEW > NO_HIT > NOT_APPLICABLE.</summary>
public enum CaseResult { NotApplicable, NoHit, Review, Hit, NotEvaluated }

public sealed record CaseRecord(
    string CaseId, string SubjectRef, string TriggerEventId, CaseStatus Status,
    int CurrentRevision, int? EffectiveRevision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record AnalysisRecord(
    string CaseId, int Revision, ExecutionStatus ExecutionStatus, CaseResult CaseResult, string ResultJson, DateTimeOffset CreatedAt);

public static class CaseLifecycle
{
    // Hard-conflict rules validated by a Golden Case may close a pair automatically.
    private static readonly HashSet<string> ValidatedAutomaticRules = [Shared.ReasonCodes.ImpossibleAgeAtReportedEvent];

    public static CaseResult Aggregate(CaseAnalysis a) =>
        a.Hits.Count == 0 ? CaseResult.NoHit
        : a.Hits.Any(h => h.PairResult == PairResult.Review) ? CaseResult.Review
        : CaseResult.NoHit;

    /// <summary>Status after a COMPLETED analysis (case-management.md, lifecycle table).</summary>
    public static CaseStatus AfterAnalysis(CaseAnalysis a) =>
        Aggregate(a) == CaseResult.NoHit
        && a.Hits.All(h => h.Identity.Decision == IdentityDecision.NoMatch && ValidatedAutomaticRules.Contains(h.Identity.HardConflict ?? ""))
            ? CaseStatus.Closed
            : CaseStatus.InReview;
}
