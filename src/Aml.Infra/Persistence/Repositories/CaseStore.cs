using System.Globalization;
using Aml.Core.Cases;
using Aml.Core.Ports;
using Aml.Core.Screening;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Aml.Infra.Persistence.Repositories;

public sealed class CaseStore(SqliteDatabase db) : ICaseStore
{
    private const int SqliteConstraintUnique = 2067;

    private sealed record CaseRow(string CaseId, string SubjectRef, string TriggerEventId, string CaseStatus,
        long CurrentRevision, long? EffectiveRevision, string CreatedAt, string UpdatedAt);

    private sealed record AnalysisRow(string CaseId, long Revision, string ExecutionStatus, string CaseResult, string ResultJson, string? ExplanationJson, string CreatedAt);

    public async Task<CaseRecord> CreateAsync(CaseRecord record, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            await c.ExecuteAsync(new CommandDefinition(db.Sql["Case.Insert"],
                new { caseId = record.CaseId, subjectRef = record.SubjectRef, triggerEventId = record.TriggerEventId, caseStatus = Enum(record.Status), now = Time(record.CreatedAt) },
                cancellationToken: ct)).ConfigureAwait(false);
            return record;
        }
        catch (SqliteException e) when (e.SqliteExtendedErrorCode == SqliteConstraintUnique)
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

    public async Task<bool> AcceptAnalysisAsync(AnalysisRecord analysis, CaseStatus status, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var now = Time(analysis.CreatedAt);
        await c.ExecuteAsync(new CommandDefinition(db.Sql["Case.InsertAnalysis"], new
        {
            caseId = analysis.CaseId, revision = analysis.Revision, executionStatus = Enum(analysis.ExecutionStatus),
            caseResult = Enum(analysis.CaseResult), resultJson = analysis.ResultJson, explanationJson = analysis.ExplanationJson, now,
        }, tx, cancellationToken: ct)).ConfigureAwait(false);

        // Only a COMPLETED revision becomes the effective (published) result; otherwise the previous one stays.
        var effective = analysis.ExecutionStatus == ExecutionStatus.Completed ? analysis.Revision : (int?)null;
        var moved = await c.ExecuteAsync(new CommandDefinition(db.Sql["Case.AcceptAnalysis"], new
        {
            caseId = analysis.CaseId, revision = analysis.Revision, effectiveRevision = effective, caseStatus = Enum(status), now,
        }, tx, cancellationToken: ct)).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false); // a late revision stays stored as history
        return moved == 1;
    }

    public async Task<AnalysisRecord?> GetAnalysisAsync(string caseId, int revision, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        var row = await c.QuerySingleOrDefaultAsync<AnalysisRow>(new CommandDefinition(db.Sql["Case.GetAnalysis"], new { caseId, revision }, cancellationToken: ct)).ConfigureAwait(false);
        return row is null ? null : new AnalysisRecord(row.CaseId, (int)row.Revision,
            Parse<ExecutionStatus>(row.ExecutionStatus), Parse<ScreeningAssessment>(row.CaseResult), row.ResultJson, row.ExplanationJson, ParseTime(row.CreatedAt));
    }

    private static CaseRecord Map(CaseRow r) => new(r.CaseId, r.SubjectRef, r.TriggerEventId, Parse<CaseStatus>(r.CaseStatus),
        (int)r.CurrentRevision, (int?)r.EffectiveRevision, ParseTime(r.CreatedAt), ParseTime(r.UpdatedAt));

    // Enums stored as UPPER_SNAKE strings, e.g. InReview → IN_REVIEW.
    internal static string Enum<T>(T value) where T : struct, System.Enum =>
        string.Concat(value.ToString().Select((ch, i) => i > 0 && char.IsUpper(ch) ? "_" + ch : ch.ToString())).ToUpperInvariant();

    internal static T Parse<T>(string s) where T : struct, System.Enum =>
        System.Enum.Parse<T>(s.Replace("_", "", StringComparison.Ordinal), ignoreCase: true);

    internal static string Time(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseTime(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
