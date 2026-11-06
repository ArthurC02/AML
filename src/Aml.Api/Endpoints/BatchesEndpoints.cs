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
            var invalid = items.Select((item, index) => (Index: index, Valid: Uploads.ValidItem(item))).Where(x => !x.Valid).Select(x => new { item = x.Index }).ToArray();
            if (invalid.Length > 0)
                return Problems.Of(Problems.InvalidRequest, "Every item needs subjectRef, triggerEventId and .txt, .html or .htm files up to 5 MB each", fix, invalid);
            var submitted = await service.SubmitAsync(items, ct);
            return TypedResults.Accepted($"/api/batches/{submitted.BatchId}", submitted);
        })
        .WithSummary("Queue many Cases for analysis")
        .WithDescription("Each item has subjectRef, triggerEventId and files (file name → text content), the same inputs as POST /api/cases. Items already queued under the same triggerEventId and subjectRef are listed as duplicates and not queued again. Returns 202 with batchId; progress is at GET /api/batches/{batchId}.")
        .WithName("submitBatch")
        .WithTags(Tags.Batches);

        batches.MapGet("/{batchId}", async Task<Results<Ok<BatchStatus>, NotFound, ProblemHttpResult>> (
            string batchId, string? view, string? status, int? pageSize, string? pageToken, BatchService service, CancellationToken ct) =>
        {
            if (view is not (null or "summary" or "full"))
                return Problems.Of(Problems.InvalidRequest, $"Unknown view '{view}'.", "Send view=summary (items not yet succeeded) or view=full (every item).", new { parameter = "view" });
            // The status as the API writes it (DEAD_LETTER); the enum name is accepted too.
            JobStatus? only = null;
            if (status is not null)
            {
                if (!CaseJson.TryParseCode<JobStatus>(status, out var parsed))
                    return Problems.Of(Problems.InvalidRequest, $"Unknown status '{status}'.", "Send status=PENDING, RUNNING, SUCCEEDED or DEAD_LETTER.", new { parameter = "status" });
                only = parsed;
            }
            if (Problems.Paging(pageSize, pageToken, out var size, out var after) is { } paging) return paging;
            var batch = await service.GetAsync(batchId, ct, view == "full" ? BatchView.Full : BatchView.Summary, only, size, after);
            return batch is null ? TypedResults.NotFound() : TypedResults.Ok(batch);
        })
        .WithSummary("Batch progress: counts per status and one page of items with their Case, revision, attempts and error code")
        .WithDescription("The progress of a submitted batch or of the re-analyses a Profile import queued. view=summary (default) lists the items not yet succeeded; view=full lists every item; status= lists one status. Items come oldest first, pageSize (default 20, at most 100) at a time, with nextPageToken when more follow.")
        .WithName("getBatchStatus")
        .WithTags(Tags.Batches);

        batches.MapPost("/{batchId}/rerun", async Task<Results<Ok<RerunResponse>, NotFound>> (string batchId, BatchService service, CancellationToken ct) =>
            await service.GetAsync(batchId, ct, pageSize: 1) is null ? TypedResults.NotFound() : TypedResults.Ok(new RerunResponse(await service.RerunAsync(batchId, ct))))
        .WithSummary("Operator re-run: dead-lettered items of the batch go back to the queue with a fresh retry budget")
        .WithDescription("For transient failures (TIMEOUT, LEASE_EXPIRED, EXCEPTION:<type>) after their cause is fixed. Every dead-lettered item is requeued; INVALID_INPUT and INVALID_PAYLOAD fail the same way again, so fixed files for INVALID_INPUT go to POST /api/cases. Returns how many items were requeued.")
        .WithName("rerunBatch")
        .WithTags(Tags.Batches);
    }
}

internal sealed record RerunResponse(int Requeued);
