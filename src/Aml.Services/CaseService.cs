using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Core.Identity;
using Aml.Core.Screening;
using Aml.Core.Ports;
using Aml.Core.Subjects;

namespace Aml.Services;

/// <summary>Sole writer of Case state. Runs AnalyzeCase synchronously.</summary>
/// <remarks>ponytail: synchronous analysis; route through the job table (BatchService) when volume needs it.</remarks>
public sealed class CaseService(ICaseStore store, TimeProvider clock)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Creates the Case and its first analysis. Repeating the same trigger and subject returns the existing Case unchanged.</summary>
    public async Task<CaseRecord> CreateAsync(string subjectRef, string triggerEventId, CaseInput input, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var created = await store.CreateAsync(
            new CaseRecord(Guid.CreateVersion7().ToString(), subjectRef, triggerEventId, CaseStatus.Open, 0, null, now, now), ct).ConfigureAwait(false);
        if (created.CurrentRevision > 0) return created;
        return await AnalyzeAsync(created, input, ct).ConfigureAwait(false);
    }

    /// <summary>New analysis revision for an existing Case (changed inputs or manual re-analysis).</summary>
    public async Task<CaseRecord?> ReanalyzeAsync(string caseId, CaseInput input, CancellationToken ct)
    {
        var existing = await store.GetAsync(caseId, ct).ConfigureAwait(false);
        return existing is null ? null : await AnalyzeAsync(existing, input, ct).ConfigureAwait(false);
    }

    public Task<CaseRecord?> GetAsync(string caseId, CancellationToken ct) => store.GetAsync(caseId, ct);

    /// <summary>Explanation of the effective revision.</summary>
    public async Task<string?> GetExplanationAsync(string caseId, CancellationToken ct)
    {
        var c = await store.GetAsync(caseId, ct).ConfigureAwait(false);
        if (c?.EffectiveRevision is not { } rev) return null;
        return (await store.GetAnalysisAsync(caseId, rev, ct).ConfigureAwait(false))?.ExplanationJson;
    }

    public Task<AnalysisRecord?> GetAnalysisAsync(string caseId, int revision, CancellationToken ct) => store.GetAnalysisAsync(caseId, revision, ct);

    // Reads the pair snapshot back from the previous revision's stored result.
    private async Task<IReadOnlyList<PairSnapshot>> PreviousSnapshotAsync(CaseRecord c, CancellationToken ct)
    {
        var previous = await store.GetAnalysisAsync(c.CaseId, c.CurrentRevision, ct).ConfigureAwait(false);
        if (previous is null) return [];
        using var doc = JsonDocument.Parse(previous.ResultJson);
        return doc.RootElement.GetProperty("hits").EnumerateArray().Select(h => new PairSnapshot(
            h.GetProperty("profile").GetProperty("profileKey").GetString()!,
            ParseEnum<ScreeningAssessment>(h.GetProperty("pairResult").GetString()!),
            ParseEnum<IdentityDecision>(h.GetProperty("identity").GetProperty("decision").GetString()!),
            h.GetProperty("identity").GetProperty("reasonCodes").EnumerateArray().Select(x => x.GetString()!)
                .Concat(h.GetProperty("pairReasonCodes").EnumerateArray().Select(x => x.GetString()!)).ToList())).ToList();
    }

    private static T ParseEnum<T>(string snake) where T : struct, Enum =>
        Enum.Parse<T>(snake.Replace("_", "", StringComparison.Ordinal), ignoreCase: true);

    private async Task<CaseRecord> AnalyzeAsync(CaseRecord c, CaseInput input, CancellationToken ct)
    {
        using var activity = AmlTelemetry.Source.StartActivity("AnalyzeCase");
        activity?.SetTag("aml.case_id", c.CaseId);
        var started = Stopwatch.GetTimestamp();
        var analysis = CaseAnalyzer.Analyze(input);
        var revision = c.CurrentRevision + 1;
        var status = CaseLifecycle.AfterAnalysis(analysis, c.Status);
        var execution = CaseLifecycle.Execution(analysis);
        var changes = c.CurrentRevision == 0 ? null
            : RevisionChanges.Compare(await PreviousSnapshotAsync(c, ct).ConfigureAwait(false), RevisionChanges.Snapshot(analysis));
        var record = new AnalysisRecord(c.CaseId, revision, execution, CaseLifecycle.Aggregate(analysis),
            JsonSerializer.Serialize(analysis, Json), JsonSerializer.Serialize(ExplanationBuilder.Build(input, analysis, changes), Json), clock.GetUtcNow());

        if (!await store.AcceptAnalysisAsync(record, status, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Case {c.CaseId} already has a revision newer than {revision}");
        AmlTelemetry.AnalysisDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        AmlTelemetry.Analyses.Add(1, new("case_result", record.CaseResult.ToString()), new("execution_status", execution.ToString()));
        activity?.SetTag("aml.revision", revision);
        activity?.SetTag("aml.case_result", record.CaseResult.ToString());
        return c with
        {
            CurrentRevision = revision,
            EffectiveRevision = execution == ExecutionStatus.Completed ? revision : c.EffectiveRevision,
            Status = status,
            UpdatedAt = record.CreatedAt,
        };
    }
}
