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
    /// <summary>Tries to store a revision; each retry re-reads the Case after another analysis took the number.</summary>
    public const int MaxRevisionAttempts = 3;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Creates the Case and its first analysis. Repeating the same trigger and subject returns the existing Case unchanged.</summary>
    /// <param name="files">The input files, stored with the revision so a later source update can re-analyze them.</param>
    /// <param name="beforeWrite">Runs after the analysis and before the revision is stored (a queued job renews its lease here).</param>
    public async Task<CaseRecord> CreateAsync(string subjectRef, string triggerEventId, CaseInput input, CancellationToken ct,
        IReadOnlyDictionary<string, string>? files = null, Func<Task>? beforeWrite = null)
    {
        var now = clock.GetUtcNow();
        var created = await store.CreateAsync(
            new CaseRecord(Guid.CreateVersion7().ToString(), subjectRef, triggerEventId, CaseStatus.Open, 0, null, now, now), ct).ConfigureAwait(false);
        if (created.CurrentRevision > 0) return created;
        return await AnalyzeAsync(created, input, files, beforeWrite, ct).ConfigureAwait(false);
    }

    /// <summary>New analysis revision for an existing Case (changed inputs or manual re-analysis).</summary>
    public async Task<CaseRecord?> ReanalyzeAsync(string caseId, CaseInput input, CancellationToken ct,
        IReadOnlyDictionary<string, string>? files = null, Func<Task>? beforeWrite = null)
    {
        var existing = await store.GetAsync(caseId, ct).ConfigureAwait(false);
        return existing is null ? null : await AnalyzeAsync(existing, input, files, beforeWrite, ct).ConfigureAwait(false);
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

    // Reads the pair snapshot back from a stored result; a pair or result missing a field contributes nothing.
    private async Task<IReadOnlyList<PairSnapshot>> SnapshotAsync(string caseId, int revision, CancellationToken ct)
    {
        var stored = await store.GetAnalysisAsync(caseId, revision, ct).ConfigureAwait(false);
        if (stored is null) return [];
        using var doc = JsonDocument.Parse(stored.ResultJson);
        if (!doc.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) return [];
        return hits.EnumerateArray().Select(Pair).OfType<PairSnapshot>().ToList();
    }

    private static PairSnapshot? Pair(JsonElement h) =>
        h.TryGetProperty("profile", out var profile) && profile.TryGetProperty("profileKey", out var key)
        && h.TryGetProperty("pairResult", out var result)
        && h.TryGetProperty("identity", out var identity) && identity.TryGetProperty("decision", out var decision)
            ? new PairSnapshot(key.GetString()!, ParseEnum<ScreeningAssessment>(result.GetString()!), ParseEnum<IdentityDecision>(decision.GetString()!),
                Codes(identity, "reasonCodes").Concat(Codes(h, "pairReasonCodes")).ToList())
            : null;

    private static IEnumerable<string> Codes(JsonElement e, string name) =>
        e.TryGetProperty(name, out var codes) && codes.ValueKind == JsonValueKind.Array ? codes.EnumerateArray().Select(x => x.GetString()!) : [];

    private static T ParseEnum<T>(string snake) where T : struct, Enum =>
        Enum.Parse<T>(snake.Replace("_", "", StringComparison.Ordinal), ignoreCase: true);

    private async Task<CaseRecord> AnalyzeAsync(CaseRecord c, CaseInput input, IReadOnlyDictionary<string, string>? files, Func<Task>? beforeWrite, CancellationToken ct)
    {
        using var activity = AmlTelemetry.Source.StartActivity("AnalyzeCase");
        activity?.SetTag("aml.case_id", c.CaseId);
        var started = Stopwatch.GetTimestamp();
        var analysis = CaseAnalyzer.Analyze(input);
        var execution = CaseLifecycle.Execution(analysis);
        var resultJson = JsonSerializer.Serialize(analysis, Json);

        for (var attempt = 1; ; attempt++)
        {
            // The number, status and changes all depend on the Case as last read; a conflict re-reads it and recomputes them.
            var revision = c.CurrentRevision + 1;
            var status = CaseLifecycle.AfterAnalysis(analysis, c.Status);
            // Changes are listed against the last effective (COMPLETED) revision, never a partial or failed one.
            var changes = c.EffectiveRevision is { } effective
                ? RevisionChanges.Compare(await SnapshotAsync(c.CaseId, effective, ct).ConfigureAwait(false), RevisionChanges.Snapshot(analysis))
                : null;
            var record = new AnalysisRecord(c.CaseId, revision, execution, CaseLifecycle.Aggregate(analysis),
                resultJson, JsonSerializer.Serialize(ExplanationBuilder.Build(input, analysis, changes), Json), clock.GetUtcNow());

            // CaseAnalyzer is CPU-bound and ignores the token: a run that overran its deadline is not written.
            ct.ThrowIfCancellationRequested();
            if (beforeWrite is not null) await beforeWrite().ConfigureAwait(false);
            if (await store.AcceptAnalysisAsync(record, status, files, ct).ConfigureAwait(false))
            {
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
            if (attempt == MaxRevisionAttempts)
                throw new InvalidOperationException($"Case {c.CaseId}: revision {revision} was taken by another analysis {attempt} times");
            c = await store.GetAsync(c.CaseId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"Case {c.CaseId} not found");
        }
    }
}
