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
/// <param name="lists">The list database, for the lineage of each Profile used; without it every Profile is recorded as CASE_FILES.</param>
/// <param name="exportText">Turns a Profile file into the export text its content hash is taken on (as the list database does).</param>
public sealed class CaseService(ICaseStore store, TimeProvider clock, IListStore? lists = null, Func<string, string>? exportText = null)
{
    /// <summary>Tries to store a revision; each retry re-reads the Case after another analysis took the number.</summary>
    public const int MaxRevisionAttempts = 3;

    /// <summary>
    /// Creates the Case and its first analysis. Repeating the same trigger and subject returns the existing Case unchanged;
    /// a Case left at revision 0 by an interrupted attempt gets its first analysis, and two concurrent creates store one.
    /// </summary>
    /// <param name="files">The input files, stored with the revision so a later source update can re-analyze them.</param>
    /// <param name="beforeWrite">Runs after the analysis and before the revision is stored (a queued job renews its lease here).</param>
    public async Task<CaseRecord> CreateAsync(string subjectRef, string triggerEventId, CaseInput input, CancellationToken ct,
        IReadOnlyDictionary<string, string>? files = null, Func<Task>? beforeWrite = null) =>
        (await CreateOrGetAsync(subjectRef, triggerEventId, input, ct, files, beforeWrite).ConfigureAwait(false)).Case;

    /// <summary>
    /// <see cref="CreateAsync"/>, also saying whether this call inserted the Case. False for an existing Case, one resumed
    /// from revision 0, or one a concurrent request inserted first.
    /// </summary>
    public async Task<(CaseRecord Case, bool Inserted)> CreateOrGetAsync(string subjectRef, string triggerEventId, CaseInput input, CancellationToken ct,
        IReadOnlyDictionary<string, string>? files = null, Func<Task>? beforeWrite = null)
    {
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7().ToString();
        var created = await store.CreateAsync(new CaseRecord(id, subjectRef, triggerEventId, CaseStatus.Open, 0, null, now, now), ct).ConfigureAwait(false);
        var inserted = created.CaseId == id;
        if (created.CurrentRevision > 0) return (created, inserted);
        return (await AnalyzeAsync(created, input, files, beforeWrite, ct, firstOnly: true).ConfigureAwait(false), inserted);
    }

    /// <summary>New analysis revision for an existing Case (changed inputs or manual re-analysis).</summary>
    public async Task<CaseRecord?> ReanalyzeAsync(string caseId, CaseInput input, CancellationToken ct,
        IReadOnlyDictionary<string, string>? files = null, Func<Task>? beforeWrite = null)
    {
        var existing = await store.GetAsync(caseId, ct).ConfigureAwait(false);
        return existing is null ? null : await AnalyzeAsync(existing, input, files, beforeWrite, ct).ConfigureAwait(false);
    }

    public Task<CaseRecord?> GetAsync(string caseId, CancellationToken ct) => store.GetAsync(caseId, ct);

    public Task<AnalysisRecord?> GetAnalysisAsync(string caseId, int revision, CancellationToken ct) => store.GetAnalysisAsync(caseId, revision, ct);

    /// <summary>The Case with its effective revision summarized; null for an unknown Case.</summary>
    public async Task<CaseSummary?> GetSummaryAsync(string caseId, CancellationToken ct)
    {
        var c = await store.GetAsync(caseId, ct).ConfigureAwait(false);
        if (c is null) return null;
        var effective = c.EffectiveRevision is { } rev ? await store.GetAnalysisAsync(caseId, rev, ct).ConfigureAwait(false) : null;
        return CaseViews.Summary(c, effective);
    }

    /// <summary>One hit of the given revision, or of the effective one; null when the Case, revision or hit does not exist.</summary>
    public async Task<(int Revision, HitExplanation Hit)?> GetHitAsync(string caseId, int hitNumber, int? revision, CancellationToken ct)
    {
        var rev = revision ?? (await store.GetAsync(caseId, ct).ConfigureAwait(false))?.EffectiveRevision;
        if (rev is not { } r) return null;
        var hit = (await store.GetAnalysisAsync(caseId, r, ct).ConfigureAwait(false))?.Explanation?.Hits.FirstOrDefault(h => h.HitNumber == hitNumber);
        return hit is null ? null : (r, hit);
    }

    /// <summary>Every revision of the Case; null for an unknown Case.</summary>
    public async Task<IReadOnlyList<RevisionLine>?> GetRevisionsAsync(string caseId, CancellationToken ct) =>
        await store.GetAsync(caseId, ct).ConfigureAwait(false) is null ? null : await store.RevisionsAsync(caseId, ct).ConfigureAwait(false);

    /// <summary>One page of Cases matching the filters, after the given key.</summary>
    public async Task<CasePage> SearchAsync(string? subjectRef, string? triggerEventId, int pageSize, (DateTimeOffset At, string Id)? after, CancellationToken ct)
    {
        var rows = await store.SearchAsync(subjectRef, triggerEventId, after, pageSize + 1, ct).ConfigureAwait(false);
        var page = rows.Take(pageSize).ToList();
        return new CasePage(page, rows.Count > pageSize ? PageToken.Encode(page[^1].CreatedAt, page[^1].CaseId) : null);
    }

    private async Task<IReadOnlyList<PairSnapshot>> SnapshotAsync(string caseId, int revision, CancellationToken ct) =>
        await store.GetAnalysisAsync(caseId, revision, ct).ConfigureAwait(false) is { } stored ? RevisionChanges.Snapshot(stored.Result) : [];

    // Each Profile the analysis used: the latest list version with the same content, or only the Case's own file.
    private async Task<IReadOnlyList<ProfileUse>> ProfileUsesAsync(CaseInput input, IReadOnlyDictionary<string, string>? files, CancellationToken ct)
    {
        var uses = new List<ProfileUse>();
        foreach (var id in input.Profiles.Keys.Order(StringComparer.Ordinal))
        {
            var content = files?.FirstOrDefault(f => ProfileFiles.IdOf(f.Key) == id).Value;
            var hash = content is null ? null : ProfileFiles.Hash(content, exportText);
            var version = hash is null || lists is null ? null
                : (await lists.HistoryAsync(id, ct).ConfigureAwait(false)).LastOrDefault(v => v.ContentHash == hash);
            uses.Add(version is null
                ? new ProfileUse(id, AnalysisLineage.CaseFiles, null, hash)
                : new ProfileUse(id, AnalysisLineage.ListDatabase, version.Version, hash));
        }
        return uses;
    }

    /// <param name="firstOnly">Create path: when another create stored revision 1 first, that Case is returned instead of a second revision.</param>
    private async Task<CaseRecord> AnalyzeAsync(CaseRecord c, CaseInput input, IReadOnlyDictionary<string, string>? files, Func<Task>? beforeWrite,
        CancellationToken ct, bool firstOnly = false)
    {
        using var activity = AmlTelemetry.Source.StartActivity("AnalyzeCase");
        activity?.SetTag("aml.case_id", c.CaseId);
        var started = Stopwatch.GetTimestamp();
        var analysis = CaseAnalyzer.Analyze(input);
        var execution = CaseLifecycle.Execution(analysis);
        var lineage = AnalysisLineage.Of(input, await ProfileUsesAsync(input, files, ct).ConfigureAwait(false));

        for (var attempt = 1; ; attempt++)
        {
            // The number, status and changes all depend on the Case as last read; a conflict re-reads it and recomputes them.
            var revision = c.CurrentRevision + 1;
            var status = CaseLifecycle.AfterAnalysis(analysis, c.Status);
            // Changes are listed against the last effective (COMPLETED) revision, never a partial or failed one.
            var changes = c.EffectiveRevision is { } effective
                ? RevisionChanges.Compare(await SnapshotAsync(c.CaseId, effective, ct).ConfigureAwait(false), RevisionChanges.Snapshot(analysis))
                : null;
            var explanation = ExplanationBuilder.Build(input, analysis, changes);
            var quality = AnalysisQuality.Of(explanation.Hits, input.Subject.Findings, analysis.NotEvaluatedProfiles.Count);
            var record = new AnalysisRecord(c.CaseId, revision, execution, CaseLifecycle.Aggregate(analysis),
                analysis, explanation, lineage, quality, clock.GetUtcNow());

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
            if (firstOnly && c.CurrentRevision > 0) return c;
        }
    }
}
