using Aml.Core.Ports;
using Aml.Infra.Jobs;
using Dapper;

namespace Aml.Infra.Persistence.Repositories;

public sealed class ListStore(SqliteDatabase db) : IListStore
{
    private sealed record Row(string ProfileId, long Version, string ContentHash, string SourceText, string CapturedAt);

    public async Task<(ListProfileRecord Current, bool Changed, IReadOnlyList<string> QueuedJobIds)> CaptureAsync(
        string profileId, string contentHash, string sourceText, DateTimeOffset now, Func<int, IReadOnlyList<NewJob>> jobsFor, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var current = await c.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(db.Sql["Extraction.GetLatestProfile"], new { profileId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (current?.ContentHash == contentHash) return (Map(current), false, []);

        var version = (int)(current?.Version ?? 0) + 1;
        var at = SqliteValues.FormatTime(now);
        await c.ExecuteAsync(new CommandDefinition(db.Sql["Extraction.InsertProfile"],
            new { profileId, version, contentHash, sourceText, now = at }, tx, cancellationToken: ct)).ConfigureAwait(false);

        // Transactional outbox: the new version and the work it causes commit together, or neither does.
        var queued = new List<string>();
        foreach (var job in jobsFor(version))
            if (await JobStore.TryEnqueueAsync(db, c, tx, job, at, ct).ConfigureAwait(false)) queued.Add(job.JobId);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return (new ListProfileRecord(profileId, version, contentHash, sourceText, now), true, queued);
    }

    public async Task<ListProfileRecord?> GetAsync(string profileId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        var row = await c.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(db.Sql["Extraction.GetLatestProfile"], new { profileId }, cancellationToken: ct)).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<ListProfileRecord>> LatestAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        return (await c.QueryAsync<Row>(new CommandDefinition(db.Sql["Extraction.LatestProfiles"], cancellationToken: ct)).ConfigureAwait(false)).Select(Map).ToList();
    }

    public async Task<IReadOnlyList<ListProfileRecord>> HistoryAsync(string profileId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        return (await c.QueryAsync<Row>(new CommandDefinition(db.Sql["Extraction.ProfileHistory"], new { profileId }, cancellationToken: ct)).ConfigureAwait(false)).Select(Map).ToList();
    }

    public async Task<long> VersionAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        return await c.ExecuteScalarAsync<long>(new CommandDefinition(db.Sql["Extraction.ListVersion"], cancellationToken: ct)).ConfigureAwait(false);
    }

    private static ListProfileRecord Map(Row r) => new(r.ProfileId, (int)r.Version, r.ContentHash, r.SourceText, SqliteValues.ParseTime(r.CapturedAt));
}
