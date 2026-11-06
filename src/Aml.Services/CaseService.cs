using System.Diagnostics;
using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Core.Ports;
using Aml.Core.Subjects;

namespace Aml.Services;

/// <summary>Sole writer of Case state. Runs AnalyzeCase synchronously.</summary>
/// <remarks>ponytail: POST /api/cases analyzes inline; high-volume callers use POST /api/batches.</remarks>
public sealed class CaseService(ICaseStore store, TimeProvider clock, IListStore? lists = null, Func<string, string>? exportText = null)
{
    public const int MaxRevisionAttempts = 3;

    /// <summary>
    /// Idempotent on (trigger, subject): an existing Case is returned unchanged, one left at revision 0 by an interrupted attempt
    /// gets its first analysis, and two concurrent creates store one.
    /// </summary>
    /// <param name="caseId">The id of a new Case; a fresh v7 Guid when null. A batch item passes its job's id, so a re-run of
    /// the job after a lost write stores the same Case.</param>
    public async Task<CaseRecord> CreateAsync(string subjectRef, string triggerEventId, CaseInput input, CancellationToken ct,
        IReadOnlyDictionary<string, string>? files = null, Func<Task>? beforeWrite = null, string? caseId = null) =>
        (await CreateOrGetAsync(subjectRef, triggerEventId, input, ct, files, beforeWrite, caseId).ConfigureAwait(false)).Case;

    public async Task<(CaseRecord Case, bool Inserted)> CreateOrGetAsync(string subjectRef, string triggerEventId, CaseInput input, CancellationToken ct,
        IReadOnlyDictionary<string, string>? files = null, Func<Task>? beforeWrite = null, string? caseId = null)
    {
        var now = clock.GetUtcNow();
        var id = caseId ?? Guid.CreateVersion7().ToString();
        var record = new CaseRecord(id, subjectRef, triggerEventId, CaseStatus.Open, 0, null, now, now);
        var created = await store.CreateAsync(record, ct).ConfigureAwait(false);
        var inserted = created == record; // not the id: a replayed batch item finds its Case stored under the id it passed
        if (created.CurrentRevision > 0) return (created, inserted);
        return (await AnalyzeAsync(created, input, files, beforeWrite, ct, yieldToConcurrentFirstRevision: true).ConfigureAwait(false), inserted);
    }

    public async Task<CaseRecord?> ReanalyzeAsync(string caseId, CaseInput input, CancellationToken ct,
        IReadOnlyDictionary<string, string>? files = null, Func<Task>? beforeWrite = null)
    {
        var existing = await store.GetAsync(caseId, ct).ConfigureAwait(false);
        return existing is null ? null : await AnalyzeAsync(existing, input, files, beforeWrite, ct).ConfigureAwait(false);
    }

    public Task<CaseRecord?> GetAsync(string caseId, CancellationToken ct) => store.GetAsync(caseId, ct);

    public Task<AnalysisRecord?> GetAnalysisAsync(string caseId, int revision, CancellationToken ct) => store.GetAnalysisAsync(caseId, revision, ct);

    public async Task<CaseSummary?> GetSummaryAsync(string caseId, CancellationToken ct) =>
        await GetDetailAsync(caseId, ct).ConfigureAwait(false) is { } d ? CaseViews.Summary(d.Case, d.Effective) : null;

    public async Task<CaseDetail?> GetDetailAsync(string caseId, CancellationToken ct)
    {
        var c = await store.GetAsync(caseId, ct).ConfigureAwait(false);
        if (c is null) return null;
        var effective = c.EffectiveRevision is { } rev ? await store.GetAnalysisAsync(caseId, rev, ct).ConfigureAwait(false) : null;
        return new CaseDetail(c, effective, CaseViews.RevisionsLink(caseId));
    }

    public async Task<(int Revision, HitExplanation Hit)?> GetHitAsync(string caseId, int hitNumber, int? revision, CancellationToken ct)
    {
        var rev = revision ?? (await store.GetAsync(caseId, ct).ConfigureAwait(false))?.EffectiveRevision;
        if (rev is not { } r) return null;
        var hit = (await store.GetAnalysisAsync(caseId, r, ct).ConfigureAwait(false))?.Explanation?.Hits.FirstOrDefault(h => h.HitNumber == hitNumber);
        return hit is null ? null : (r, hit);
    }

    public async Task<IReadOnlyList<RevisionLine>?> GetRevisionsAsync(string caseId, CancellationToken ct) =>
        await store.GetAsync(caseId, ct).ConfigureAwait(false) is null ? null : await store.RevisionsAsync(caseId, ct).ConfigureAwait(false);

    public async Task<CasePage> SearchAsync(string? subjectRef, string? triggerEventId, int pageSize, (DateTimeOffset At, string Id)? after, CancellationToken ct)
    {
        var rows = await store.SearchAsync(subjectRef, triggerEventId, after, pageSize + 1, ct).ConfigureAwait(false);
        var (page, next) = PageToken.Page(rows, pageSize, c => (c.CreatedAt, c.CaseId));
        return new CasePage(page, next);
    }

    private async Task<IReadOnlyList<PairSnapshot>> SnapshotAsync(string caseId, int revision, CancellationToken ct) =>
        await store.GetAnalysisAsync(caseId, revision, ct).ConfigureAwait(false) is { } stored ? RevisionChanges.Snapshot(stored.Result) : [];

    private async Task<IReadOnlyList<ProfileUse>> ProfileUsesAsync(CaseInput input, IReadOnlyDictionary<string, string>? files, CancellationToken ct)
    {
        var uses = new List<ProfileUse>();
        foreach (var id in input.Profiles.Keys.Order(StringComparer.Ordinal))
        {
            var content = files?.FirstOrDefault(f => ProfileFiles.IdOf(f.Key) == id).Value;
            var hash = content is null ? null : ProfileFiles.Hash(content, exportText);
            var listVersionWithSameContent = hash is null || lists is null ? null
                : (await lists.HistoryAsync(id, ct).ConfigureAwait(false)).LastOrDefault(v => v.ContentHash == hash);
            uses.Add(listVersionWithSameContent is null
                ? new ProfileUse(id, AnalysisLineage.CaseFiles, null, hash)
                : new ProfileUse(id, AnalysisLineage.ListDatabase, listVersionWithSameContent.Version, hash));
        }
        return uses;
    }

    private async Task<CaseRecord> AnalyzeAsync(CaseRecord current, CaseInput input, IReadOnlyDictionary<string, string>? files, Func<Task>? beforeWrite,
        CancellationToken ct, bool yieldToConcurrentFirstRevision = false)
    {
        using var activity = AmlTelemetry.Source.StartActivity("AnalyzeCase");
        activity?.SetTag("aml.case_id", current.CaseId);
        var started = Stopwatch.GetTimestamp();
        var analysis = CaseAnalyzer.Analyze(input);
        var execution = CaseLifecycle.Execution(analysis);
        var lineage = AnalysisLineage.Of(input, await ProfileUsesAsync(input, files, ct).ConfigureAwait(false));

        for (var attempt = 1; ; attempt++)
        {
            var revision = current.CurrentRevision + 1;
            var status = CaseLifecycle.AfterAnalysis(analysis, current.Status);
            var changes = await ChangesSinceEffectiveRevisionAsync(current, analysis, ct).ConfigureAwait(false);
            var explanation = ExplanationBuilder.Build(input, analysis, changes);
            var quality = AnalysisQuality.Of(explanation.Hits, input.Subject.Findings, analysis.NotEvaluatedProfiles.Count);
            var record = new AnalysisRecord(current.CaseId, revision, execution, CaseLifecycle.Aggregate(analysis),
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
                return current with
                {
                    CurrentRevision = revision,
                    EffectiveRevision = execution == ExecutionStatus.Completed ? revision : current.EffectiveRevision,
                    Status = status,
                    UpdatedAt = record.CreatedAt,
                };
            }
            if (attempt == MaxRevisionAttempts)
                throw new InvalidOperationException($"Case {current.CaseId}: revision {revision} was taken by another analysis {attempt} times");
            current = await store.GetAsync(current.CaseId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"Case {current.CaseId} not found");
            if (yieldToConcurrentFirstRevision && current.CurrentRevision > 0) return current;
        }
    }

    private async Task<IReadOnlyList<PairChange>?> ChangesSinceEffectiveRevisionAsync(CaseRecord current, CaseAnalysis analysis, CancellationToken ct) =>
        current.EffectiveRevision is { } effective
            ? RevisionChanges.Compare(await SnapshotAsync(current.CaseId, effective, ct).ConfigureAwait(false), RevisionChanges.Snapshot(analysis))
            : null;
}
