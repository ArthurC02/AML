using System.Text.Json;
using System.Text.Json.Serialization;
using Aml.Core.Cases;
using Aml.Core.Explanation;
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

    private async Task<CaseRecord> AnalyzeAsync(CaseRecord c, CaseInput input, CancellationToken ct)
    {
        var analysis = CaseAnalyzer.Analyze(input);
        var revision = c.CurrentRevision + 1;
        var status = CaseLifecycle.AfterAnalysis(analysis);
        var record = new AnalysisRecord(c.CaseId, revision, ExecutionStatus.Completed, CaseLifecycle.Aggregate(analysis),
            JsonSerializer.Serialize(analysis, Json), JsonSerializer.Serialize(ExplanationBuilder.Build(input, analysis), Json), clock.GetUtcNow());

        if (!await store.AcceptAnalysisAsync(record, status, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Case {c.CaseId} already has a revision newer than {revision}");
        return c with { CurrentRevision = revision, EffectiveRevision = revision, Status = status, UpdatedAt = record.CreatedAt };
    }
}
