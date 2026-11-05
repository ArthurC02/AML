using Aml.Core.Cases;
using Aml.Core.Ports;
using Aml.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aml.Api.Endpoints;

/// <summary>Batch submission, progress and re-run.</summary>
internal static class BatchesEndpoints
{
    public static void MapBatches(this IEndpointRouteBuilder app)
    {
        var batches = app.MapGroup("/api/batches");

        batches.MapPost("/", async Task<Results<Accepted<BatchSubmitted>, ProblemHttpResult>> (BatchItemRequest[] items, BatchService service, CancellationToken ct) =>
        {
            const string fix = "Give every item subjectRef, triggerEventId and files: file name → text of a .txt, .html or .htm file up to 5 MB.";
            if (items.Length == 0) return Problems.Of(Problems.InvalidRequest, "No items", fix);
            var invalid = items.Select((i, n) => (Item: n, Valid: Uploads.ValidItem(i))).Where(x => !x.Valid).Select(x => new { item = x.Item }).ToArray();
            if (invalid.Length > 0)
                return Problems.Of(Problems.InvalidRequest, "Every item needs subjectRef, triggerEventId and .txt, .html or .htm files up to 5 MB each", fix, invalid);
            var b = await service.SubmitAsync(items, ct);
            return TypedResults.Accepted($"/api/batches/{b.BatchId}", b);
        })
        .WithSummary("Queue many Cases for analysis")
        .WithDescription("Each item has subjectRef, triggerEventId and files (file name → text content), the same inputs as POST /api/cases. Items already queued under the same triggerEventId and subjectRef are listed as duplicates and not queued again. Use it for many Cases at once; for one Case use createCase. Returns 202 with batchId. Next: getBatchStatus.")
        .WithName("submitBatch")
        .WithTags("write");

        batches.MapGet("/{batchId}", async Task<Results<Ok<BatchStatus>, NotFound, ProblemHttpResult>> (
            string batchId, string? view, string? status, int? pageSize, string? pageToken, BatchService service, CancellationToken ct) =>
        {
            if (view is not (null or "summary" or "full"))
                return Problems.Of(Problems.InvalidRequest, $"Unknown view '{view}'.", "Send view=summary (items not yet succeeded) or view=full (every item).", new { parameter = "view" });
            // The status as the API writes it (DEAD_LETTER); the enum name is accepted too.
            JobStatus? only = null;
            if (status is not null)
            {
                var name = Enum.GetNames<JobStatus>().FirstOrDefault(n => n.Equals(status.Replace("_", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));
                if (name is null)
                    return Problems.Of(Problems.InvalidRequest, $"Unknown status '{status}'.", "Send status=PENDING, RUNNING, SUCCEEDED or DEAD_LETTER.", new { parameter = "status" });
                only = Enum.Parse<JobStatus>(name);
            }
            var size = pageSize ?? PageToken.DefaultSize;
            if (size is < 1 or > PageToken.MaxSize)
                return Problems.Of(Problems.InvalidRequest, $"pageSize must be 1 to {PageToken.MaxSize}.", $"Send a pageSize from 1 to {PageToken.MaxSize}, or leave it out for {PageToken.DefaultSize}.", new { parameter = "pageSize" });
            (DateTimeOffset, string)? after = null;
            if (!string.IsNullOrEmpty(pageToken))
            {
                if (!PageToken.TryDecode(pageToken, out var key))
                    return Problems.Of(Problems.InvalidRequest, "pageToken was not issued by this API.", "Send the nextPageToken of the previous page unchanged, or leave it out for the first page.", new { parameter = "pageToken" });
                after = key;
            }
            var b = await service.GetAsync(batchId, ct, view == "full" ? BatchView.Full : BatchView.Summary, only, size, after);
            return b is null ? TypedResults.NotFound() : TypedResults.Ok(b);
        })
        .WithSummary("Batch progress: counts per status and one page of items with their Case, revision, attempts and error code")
        .WithDescription("Use it to follow a submitted batch or the re-analyses a Profile import queued; for one Case's result use getCaseSummary instead. view=summary (default) lists the items not yet succeeded; view=full lists every item; status= lists one status. Items come oldest first, pageSize (default 20, at most 100) at a time, with nextPageToken when more follow. Next: getCaseSummary for a succeeded item, rerunBatch for dead-lettered items once the cause is fixed.")
        .WithName("getBatchStatus")
        .WithTags("operations");

        batches.MapPost("/{batchId}/rerun", async Task<Results<Ok<RerunResponse>, NotFound>> (string batchId, BatchService service, CancellationToken ct) =>
            await service.GetAsync(batchId, ct, pageSize: 1) is null ? TypedResults.NotFound() : TypedResults.Ok(new RerunResponse(await service.RerunAsync(batchId, ct))))
        .WithSummary("Operator re-run: dead-lettered items of the batch go back to the queue with a fresh retry budget")
        .WithDescription("Use it only after fixing the cause of a transient failure (TIMEOUT, LEASE_EXPIRED, EXCEPTION:<type>); every dead-lettered item is requeued, and INVALID_INPUT and INVALID_PAYLOAD fail the same way again; for INVALID_INPUT send the fixed files with createCase instead. Returns how many items were requeued. Next: getBatchStatus; getCode explains an error code.")
        .WithName("rerunBatch")
        .WithTags("operations");
    }
}

internal sealed record RerunResponse(int Requeued);
