using System.Text.Json;
using Aml.Core.Cases;
using Aml.Core.Pages;
using Aml.Core.Ports;
using Aml.Core.Shared;
using Aml.Infra.Jobs;
using Dapper;
using Microsoft.Data.Sqlite;
using static Aml.Infra.Persistence.SqliteValues;

namespace Aml.Infra.Persistence.Repositories;

public sealed class PageStore(SqliteDatabase db) : IPageStore
{
    private sealed record Row(string PageId, string CanonicalUrl, string Status, string? Problem, string RequestedAt, string? JobId, string? BatchId,
        string? FinalUrl, string? MediaType, string? Title, string? FetchedAt, string? ContentHash, string? MainText, string? ReadingJson,
        string? PublishedOn, string? PublishedOnSource);

    public async Task<PageRecord?> GetAsync(string pageId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        return await GetAsync(c, null, pageId, ct).ConfigureAwait(false);
    }

    private async Task<PageRecord?> GetAsync(SqliteConnection c, SqliteTransaction? tx, string pageId, CancellationToken ct)
    {
        var row = await c.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(db.Sql["Extraction.GetPage"], new { pageId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (row is null) return null;
        var reading = row.ReadingJson is null ? null : JsonSerializer.Deserialize<PageReading>(row.ReadingJson, CaseJson.Options);
        return new PageRecord(row.PageId, row.CanonicalUrl, CaseJson.ParseCode<PageStatus>(row.Status), ParseTime(row.RequestedAt), row.JobId, row.BatchId,
            row.Problem, row.FinalUrl, row.MediaType, row.Title, row.FetchedAt is null ? null : ParseTime(row.FetchedAt), row.ContentHash, row.MainText,
            reading?.Persons, reading?.OtherEvents, reading?.Model, PartialDate.Parse(row.PublishedOn),
            row.PublishedOnSource is null ? null : CaseJson.ParseCode<PublicationSource>(row.PublishedOnSource));
    }

    // Transactional outbox, as a Profile capture: the page's state and its job commit together, or neither does.
    public Task<PageRecord> QueueAsync(PageRecord page, NewJob job, CancellationToken ct) =>
        db.WriteAsync(async (c, tx) =>
        {
            var now = FormatTime(page.RequestedAt);
            if (!await JobStore.TryEnqueueAsync(db, c, tx, job, now, ct).ConfigureAwait(false))
                return await GetAsync(c, tx, page.PageId, ct).ConfigureAwait(false) ?? page;
            var args = new { pageId = page.PageId, canonicalUrl = page.Url, now, jobId = job.JobId, batchId = job.BatchId };
            if (await c.ExecuteAsync(new CommandDefinition(db.Sql["Extraction.QueuePage"], args, tx, cancellationToken: ct)).ConfigureAwait(false) == 0)
                await c.ExecuteAsync(new CommandDefinition(db.Sql["Extraction.InsertPage"], args, tx, cancellationToken: ct)).ConfigureAwait(false);
            return page with { Status = PageStatus.Pending, Problem = null, JobId = job.JobId, BatchId = job.BatchId };
        }, ct);

    public Task<bool> SaveAsync(PageRecord page, CancellationToken ct) =>
        db.WriteAsync(async (c, tx) => await c.ExecuteAsync(new CommandDefinition(db.Sql["Extraction.SavePage"], new
        {
            pageId = page.PageId,
            jobId = page.JobId,
            status = CaseJson.Code(page.Status),
            problem = page.Problem,
            finalUrl = page.FinalUrl,
            mediaType = page.MediaType,
            title = page.Title,
            fetchedAt = page.FetchedAt is { } at ? FormatTime(at) : null,
            contentHash = page.ContentHash,
            mainText = page.Text,
            publishedOn = page.PublishedOn?.ToString(),
            publishedOnSource = page.PublishedOnSource is { } source ? CaseJson.Code(source) : null,
            readingJson = page.Persons is null ? null : JsonSerializer.Serialize(new PageReading(page.Persons, page.OtherEvents ?? [], page.Model), CaseJson.Options),
        }, tx, cancellationToken: ct)).ConfigureAwait(false) == 1, ct);
}
