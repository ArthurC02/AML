using Aml.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aml.Api.Endpoints;

/// <summary>Batch submission, progress and re-run.</summary>
internal static class BatchesEndpoints
{
    public static void MapBatches(this IEndpointRouteBuilder app)
    {
        var batches = app.MapGroup("/api/batches");

        batches.MapPost("/", async Task<Results<Accepted<BatchSubmitted>, BadRequest<string>>> (BatchItemRequest[] items, BatchService service, CancellationToken ct) =>
        {
            if (items.Length == 0) return TypedResults.BadRequest("No items");
            // A UTF-8 byte count is never below the character count, so the cheap length check runs first.
            if (items.Any(i => i is null || string.IsNullOrWhiteSpace(i.SubjectRef) || string.IsNullOrWhiteSpace(i.TriggerEventId)
                || i.Files is null || i.Files.Count == 0
                || i.Files.Any(f => !Uploads.Accepted(f.Key) || f.Value is null || f.Value.Length > Uploads.MaxBytes || System.Text.Encoding.UTF8.GetByteCount(f.Value) > Uploads.MaxBytes)))
                return TypedResults.BadRequest("Every item needs subjectRef, triggerEventId and .txt, .html or .htm files up to 5 MB each");
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
