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
            // A UTF-8 byte count is never below the character count, so the cheap length check runs first.
            var invalid = items.Select((i, n) => (Item: n, Valid: i is not null && !string.IsNullOrWhiteSpace(i.SubjectRef) && !string.IsNullOrWhiteSpace(i.TriggerEventId)
                    && i.Files is { Count: > 0 }
                    && i.Files.All(f => Uploads.Accepted(f.Key) && f.Value is not null && f.Value.Length <= Uploads.MaxBytes && System.Text.Encoding.UTF8.GetByteCount(f.Value) <= Uploads.MaxBytes)))
                .Where(x => !x.Valid).Select(x => new { item = x.Item }).ToArray();
            if (invalid.Length > 0)
                return Problems.Of(Problems.InvalidRequest, "Every item needs subjectRef, triggerEventId and .txt, .html or .htm files up to 5 MB each", fix, invalid);
            var b = await service.SubmitAsync(items, ct);
            return TypedResults.Accepted($"/api/batches/{b.BatchId}", b);
        })
        .WithSummary("Queue many Cases for analysis")
        .WithDescription("Each item has subjectRef, triggerEventId and files (file name → text content), the same inputs as POST /api/cases. Items already queued under the same triggerEventId and subjectRef are listed as duplicates and not queued again.");

        batches.MapGet("/{batchId}", async Task<Results<Ok<BatchStatus>, NotFound>> (string batchId, BatchService service, CancellationToken ct) =>
            await service.GetAsync(batchId, ct) is { } b ? TypedResults.Ok(b) : TypedResults.NotFound())
        .WithSummary("Batch progress: counts per status and each item with its Case, revision, attempts and error code");

        batches.MapPost("/{batchId}/rerun", async Task<Results<Ok<RerunResponse>, NotFound>> (string batchId, BatchService service, CancellationToken ct) =>
            await service.GetAsync(batchId, ct) is null ? TypedResults.NotFound() : TypedResults.Ok(new RerunResponse(await service.RerunAsync(batchId, ct))))
        .WithSummary("Operator re-run: dead-lettered items of the batch go back to the queue with a fresh retry budget");
    }
}

internal sealed record RerunResponse(int Requeued);
