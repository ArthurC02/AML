using System.ComponentModel;
using Aml.Core.Explanation;
using Aml.Core.Identity;
using Aml.Core.Screening;

namespace Aml.Core.Cases;

public enum CaseStatus { Open, InReview, Closed }

public enum ExecutionStatus { Pending, Running, Completed, Partial, Failed }

public sealed record CaseRecord(
    [property: Description("案件編號")] string CaseId,
    [property: Description("受檢對象參考碼：呼叫端的不透明參考，不得為客戶編號或證件號碼")] string SubjectRef,
    [property: Description("觸發事件編號")] string TriggerEventId,
    [property: Description("案件狀態")] CaseStatus Status,
    [property: Description("最新 revision")] int CurrentRevision,
    [property: Description("生效 revision；尚無完整分析時為 null")] int? EffectiveRevision,
    [property: Description("建立時間")] DateTimeOffset CreatedAt,
    [property: Description("更新時間")] DateTimeOffset UpdatedAt);

public sealed record AnalysisRecord(
    [property: Description("案件編號")] string CaseId,
    [property: Description("revision 編號")] int Revision,
    [property: Description("執行狀態；PARTIAL 不會成為生效 revision")] ExecutionStatus ExecutionStatus,
    [property: Description("案件層級結果")] ScreeningAssessment CaseResult,
    [property: Description("分析結果")] CaseAnalysis Result,
    [property: Description("解釋")] CaseExplanation? Explanation,
    [property: Description("沿革：輸入檔、名單版本與規則版本")] AnalysisLineage? Lineage,
    [property: Description("資料品質指標")] AnalysisQuality? Quality,
    [property: Description("分析時間")] DateTimeOffset CreatedAt);

public static class CaseLifecycle
{
    // ScreeningAssessment is declared in precedence order, so the most severe pair result is the Max.
    public static ScreeningAssessment Aggregate(CaseAnalysis a) =>
        IsIncomplete(a) ? ScreeningAssessment.NotEvaluated
        : a.Hits.Count == 0 ? ScreeningAssessment.NoHit
        : a.Hits.Max(h => h.PairResult);

    public static ExecutionStatus Execution(CaseAnalysis a) =>
        IsIncomplete(a) ? ExecutionStatus.Partial : ExecutionStatus.Completed;

    /// <summary>Status after a COMPLETED analysis (case-management.md, lifecycle table).</summary>
    public static CaseStatus AfterAnalysis(CaseAnalysis a, CaseStatus current) => Aggregate(a) switch
    {
        ScreeningAssessment.NotEvaluated => current,
        ScreeningAssessment.Hit or ScreeningAssessment.Review => CaseStatus.InReview,
        _ when EveryNoHitExcludedByValidatedHardConflict(a) => CaseStatus.Closed,
        _ => CaseStatus.InReview,
    };

    private static bool IsIncomplete(CaseAnalysis a) => a.NotEvaluatedProfiles.Count > 0;

    private static bool EveryNoHitExcludedByValidatedHardConflict(CaseAnalysis a) =>
        a.Hits.Where(h => h.PairResult == ScreeningAssessment.NoHit)
            .All(h => h.Identity.Decision == IdentityDecision.NoMatch && HardConflictGate.ValidatedReasonCodes.Contains(h.Identity.HardConflict ?? ""));
}
