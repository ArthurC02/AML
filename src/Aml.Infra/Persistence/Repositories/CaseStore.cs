using System.Text.Json;
using Aml.Core.Cases;
using Aml.Core.Codes;
using Aml.Core.Explanation;
using Aml.Core.Ports;
using Aml.Core.Retrieval;
using Aml.Core.Screening;
using Dapper;
using Microsoft.Data.Sqlite;
using static Aml.Core.Cases.CaseJson;
using static Aml.Infra.Persistence.SqliteValues;

namespace Aml.Infra.Persistence.Repositories;

public sealed class CaseStore(SqliteDatabase db) : ICaseStore
{
    private sealed record CaseRow(string CaseId, string SubjectRef, string TriggerEventId, string CaseStatus,
        long CurrentRevision, long? EffectiveRevision, string CreatedAt, string UpdatedAt);

    private sealed record AnalysisRow(string CaseId, long Revision, string ExecutionStatus, string CaseResult, string ResultJson, string? ExplanationJson, string CreatedAt);

    private sealed record LineageRow(string InputFilesJson, string ProfilesJson, string SourcePolicyVersion, string CodeCatalogVersion, long SchemaVersion, string AppVersion);

    // Settable properties, not a constructor: the change count is NULL on a first revision, which a constructor match cannot type.
    private sealed class RevisionRow
    {
        public long Revision { get; set; }
        public string CreatedAt { get; set; } = "";
        public string ExecutionStatus { get; set; } = "";
        public string CaseResult { get; set; } = "";
        public bool Effective { get; set; }
        public long? Changes { get; set; }
    }

    private sealed record QualityRow(long Hits, long ComparisonCells, long MissingCells, long UncertainCells, long ConflictCells, long NotEvaluated, string FindingsJson);

    public async Task<CaseRecord> CreateAsync(CaseRecord record, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            await c.ExecuteAsync(new CommandDefinition(db.Sql["Case.Insert"],
                new { caseId = record.CaseId, subjectRef = record.SubjectRef, triggerEventId = record.TriggerEventId, caseStatus = Code(record.Status), now = Time(record.CreatedAt) },
                cancellationToken: ct)).ConfigureAwait(false);
            return record;
        }
        catch (SqliteException e) when (e.SqliteExtendedErrorCode == SqliteDatabase.ConstraintUnique)
        {
            // Idempotent create: the same trigger and subject already have a Case.
            var row = await c.QuerySingleAsync<CaseRow>(new CommandDefinition(db.Sql["Case.GetByTrigger"],
                new { triggerEventId = record.TriggerEventId, subjectRef = record.SubjectRef }, cancellationToken: ct)).ConfigureAwait(false);
            return Map(row);
        }
    }

    public async Task<CaseRecord?> GetAsync(string caseId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        var row = await c.QuerySingleOrDefaultAsync<CaseRow>(new CommandDefinition(db.Sql["Case.GetById"], new { caseId }, cancellationToken: ct)).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<bool> AcceptAnalysisAsync(AnalysisRecord analysis, CaseStatus status, IReadOnlyDictionary<string, string>? files, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var now = Time(analysis.CreatedAt);
        try
        {
            await c.ExecuteAsync(new CommandDefinition(db.Sql["Case.InsertAnalysis"], new
            {
                caseId = analysis.CaseId,
                revision = analysis.Revision,
                executionStatus = Code(analysis.ExecutionStatus),
                caseResult = Code(analysis.CaseResult),
                resultJson = JsonSerializer.Serialize(analysis.Result, CaseJson.Options),
                explanationJson = analysis.Explanation is null ? null : JsonSerializer.Serialize(analysis.Explanation, CaseJson.Options),
                now,
            }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (SqliteException e) when (e.SqliteExtendedErrorCode is SqliteDatabase.ConstraintPrimaryKey or SqliteDatabase.ConstraintUnique)
        {
            return false; // another analysis took this revision number; the caller re-reads and retries
        }

        // Same transaction: the facts, lineage and quality never disagree with the JSON they index.
        await InsertFactsAsync(c, tx, analysis, ct).ConfigureAwait(false);

        // Same transaction: a revision is never stored without the input it was run on.
        if (files is not null)
            await c.ExecuteAsync(new CommandDefinition(db.Sql["Case.InsertInput"],
                new { caseId = analysis.CaseId, revision = analysis.Revision, filesJson = JsonSerializer.Serialize(files), now }, tx, cancellationToken: ct)).ConfigureAwait(false);

        // Only a COMPLETED revision becomes the effective (published) result; otherwise the previous one stays.
        var effective = analysis.ExecutionStatus == ExecutionStatus.Completed ? analysis.Revision : (int?)null;
        var moved = await c.ExecuteAsync(new CommandDefinition(db.Sql["Case.AcceptAnalysis"], new
        {
            caseId = analysis.CaseId,
            revision = analysis.Revision,
            effectiveRevision = effective,
            caseStatus = Code(status),
            now,
        }, tx, cancellationToken: ct)).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false); // a late revision stays stored as history
        return moved == 1;
    }

    public async Task<AnalysisRecord?> GetAnalysisAsync(string caseId, int revision, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        var args = new { caseId, revision };
        var row = await c.QuerySingleOrDefaultAsync<AnalysisRow>(new CommandDefinition(db.Sql["Case.GetAnalysis"], args, cancellationToken: ct)).ConfigureAwait(false);
        if (row is null) return null;
        var l = await c.QuerySingleOrDefaultAsync<LineageRow>(new CommandDefinition(db.Sql["Case.GetLineage"], args, cancellationToken: ct)).ConfigureAwait(false);
        var q = await c.QuerySingleOrDefaultAsync<QualityRow>(new CommandDefinition(db.Sql["Case.GetQuality"], args, cancellationToken: ct)).ConfigureAwait(false);
        double? Rate(long n) => q!.ComparisonCells == 0 ? null : (double)n / q.ComparisonCells;
        return new AnalysisRecord(row.CaseId, (int)row.Revision, ParseCode<ExecutionStatus>(row.ExecutionStatus), ParseCode<ScreeningAssessment>(row.CaseResult),
            Read<CaseAnalysis>(row.ResultJson), row.ExplanationJson is null ? null : Read<CaseExplanation>(row.ExplanationJson),
            l is null ? null : new AnalysisLineage(Read<List<InputFile>>(l.InputFilesJson), Read<List<ProfileUse>>(l.ProfilesJson),
                l.SourcePolicyVersion, l.CodeCatalogVersion, (int)l.SchemaVersion, l.AppVersion),
            q is null ? null : new AnalysisQuality((int)q.Hits, (int)q.ComparisonCells, (int)q.MissingCells, (int)q.UncertainCells, (int)q.ConflictCells,
                Rate(q.MissingCells), Rate(q.UncertainCells), Rate(q.ConflictCells), (int)q.NotEvaluated, Read<Dictionary<string, int>>(q.FindingsJson)),
            ParseTime(row.CreatedAt));
    }

    public async Task<IReadOnlyList<RevisionLine>> RevisionsAsync(string caseId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<RevisionRow>(new CommandDefinition(db.Sql["Case.Revisions"], new { caseId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => new RevisionLine((int)r.Revision, ParseTime(r.CreatedAt), ParseCode<ExecutionStatus>(r.ExecutionStatus),
            ParseCode<ScreeningAssessment>(r.CaseResult), r.Effective, (int?)r.Changes)).ToList();
    }

    // ponytail: "@x IS NULL OR" filters keep one statement; split per filter if the planner skips the indexes at volume.
    public async Task<IReadOnlyList<CaseRecord>> SearchAsync(string? subjectRef, string? triggerEventId, (DateTimeOffset At, string Id)? after, int limit, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<CaseRow>(new CommandDefinition(db.Sql["Case.Search"],
            new { subjectRef, triggerEventId, afterAt = after is { } a ? Time(a.At) : null, afterId = after?.Id, limit }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    // A stored record that no longer reads as its type means the format changed: rebuild the PoC database (no old formats are read).
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, CaseJson.Options)
        ?? throw new InvalidOperationException($"Stored {typeof(T).Name} is empty");

    private async Task InsertFactsAsync(SqliteConnection c, System.Data.Common.DbTransaction tx, AnalysisRecord a, CancellationToken ct)
    {
        Task Run(string id, object args) => c.ExecuteAsync(new CommandDefinition(db.Sql[id], args, tx, cancellationToken: ct));
        var (caseId, revision) = (a.CaseId, a.Revision);
        if (a.Lineage is { } l)
            await Run("Case.InsertLineage", new
            {
                caseId,
                revision,
                schemaVersion = l.SchemaVersion,
                sourcePolicyVersion = l.SourcePolicyVersion,
                codeCatalogVersion = l.CodeCatalogVersion,
                appVersion = l.AppVersion,
                inputFilesJson = JsonSerializer.Serialize(l.InputFiles, CaseJson.Options),
                profilesJson = JsonSerializer.Serialize(l.Profiles, CaseJson.Options),
            }).ConfigureAwait(false);
        if (a.Quality is { } q)
            await Run("Case.InsertQuality", new
            {
                caseId,
                revision,
                hits = q.Hits,
                comparisonCells = q.ComparisonCells,
                missingCells = q.MissingCells,
                uncertainCells = q.UncertainCells,
                conflictCells = q.ConflictCells,
                notEvaluated = q.NotEvaluatedProfiles,
                findingsJson = JsonSerializer.Serialize(q.FindingCounts, CaseJson.Options),
            }).ConfigureAwait(false);

        var hits = a.Result.Hits.Select((h, i) => (Number: i + 1, Hit: h)).ToList();
        await Run("Case.InsertHit", hits.Select(x => new
        {
            caseId,
            revision,
            hitNumber = x.Number,
            profileId = ProfileKeys.IdOf(x.Hit.Profile.ProfileKey),
            conditionTypes = string.Join(",", x.Hit.Conditions.Select(k => Code(k.Type))),
            legalStage = Code(x.Hit.Conditions.Select(k => k.LegalStage).FirstOrDefault(s => s != LegalStage.Unknown)),
            identityDecision = Code(x.Hit.Identity.Decision),
            stage3Row = x.Hit.Identity.Stage3Row,
            hardConflict = x.Hit.Identity.HardConflict,
            pairResult = Code(x.Hit.PairResult),
            reviewPriority = x.Hit.ReviewPriority is { } p ? Code(p) : null,
        }).ToList()).ConfigureAwait(false);
        await Run("Case.InsertEvidence", hits.SelectMany(x => x.Hit.Identity.Evidence.Values.Select(e => new
        {
            caseId,
            revision,
            hitNumber = x.Number,
            evidenceGroup = Code(e.Group),
            direction = Code(e.Direction),
            comparisonStatus = Code(e.Status),
            strength = Code(e.Strength),
            detailCode = e.Detail is null ? null : CodeCatalog.Key(e.Detail),
        })).ToList()).ConfigureAwait(false);
        await Run("Case.InsertReason", hits.SelectMany(x =>
            new (string Kind, IReadOnlyList<string> Codes)[] { ("IDENTITY", x.Hit.Identity.ReasonCodes), ("PAIR", x.Hit.PairReasonCodes), ("DISPOSITION", x.Hit.Disposition.ReasonCodes) }
                .SelectMany(k => k.Codes.Select((code, seq) => new { caseId, revision, hitNumber = x.Number, reasonKind = k.Kind, seq, reasonCode = code })))
            .ToList()).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>?> LatestInputAsync(string caseId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        var json = await c.QuerySingleOrDefaultAsync<string>(new CommandDefinition(db.Sql["Case.LatestInput"], new { caseId }, cancellationToken: ct)).ConfigureAwait(false);
        return json is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(json);
    }

    private sealed record InputRow(string CaseId, string FilesJson);

    // ponytail: reads every Case's latest input; add a case_input_profile index table when Case volume makes this slow.
    public async Task<IReadOnlyList<(string CaseId, IReadOnlyDictionary<string, string> Files)>> LatestInputsAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<InputRow>(new CommandDefinition(db.Sql["Case.LatestInputs"], cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => (r.CaseId,
            (IReadOnlyDictionary<string, string>)JsonSerializer.Deserialize<Dictionary<string, string>>(r.FilesJson)!)).ToList();
    }

    private static CaseRecord Map(CaseRow r) => new(r.CaseId, r.SubjectRef, r.TriggerEventId, ParseCode<CaseStatus>(r.CaseStatus),
        (int)r.CurrentRevision, (int?)r.EffectiveRevision, ParseTime(r.CreatedAt), ParseTime(r.UpdatedAt));
}
