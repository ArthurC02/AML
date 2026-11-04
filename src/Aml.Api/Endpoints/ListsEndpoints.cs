using Aml.Core.Retrieval;
using Aml.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using static Aml.Api.Endpoints.Uploads;

namespace Aml.Api.Endpoints;

/// <summary>List database: Profile import, versions, retrieval and merges.</summary>
internal static class ListsEndpoints
{
    public static void MapLists(this IEndpointRouteBuilder app)
    {
        var lists = app.MapGroup("/api/lists").DisableAntiforgery();

        lists.MapPost("/profiles", async Task<Results<Ok<ImportReport>, BadRequest<string>>> (IFormFileCollection files, ListService service, CancellationToken ct) =>
        {
            var (contents, error) = await ReadFilesAsync(files, ct);
            return contents is null ? TypedResults.BadRequest(error) : TypedResults.Ok(await service.ImportAsync(contents, ct));
        })
        .WithSummary("Capture Profiles into the list database; a changed Profile queues re-analysis of the Cases that used it")
        .WithDescription("Upload 名單資料_<id>.txt (or .html). An unchanged file keeps its version; a changed one becomes a new version, the old one is kept, and every Case whose latest input used it gets a queued re-analysis job (track it with GET /api/batches/{batchId}). A file whose name and Profile_ID differ, or one Profile in two formats, is rejected.");

        lists.MapGet("/profiles/{profileId}/versions", async Task<Results<Ok<IReadOnlyList<ProfileVersionResponse>>, NotFound>> (string profileId, ListService service, CancellationToken ct) =>
            await service.HistoryAsync(profileId, ct) is { Count: > 0 } history
                ? TypedResults.Ok<IReadOnlyList<ProfileVersionResponse>>(history.Select(v => new ProfileVersionResponse(v.ProfileId, v.Version, v.ContentHash, v.CapturedAt)).ToList())
                : TypedResults.NotFound())
        .WithSummary("Every captured version of one Profile, with its content hash");

        lists.MapGet("/candidates", async Task<Results<Ok<IReadOnlyList<RetrievedCandidate>>, BadRequest<string>>> (
            RetrievalMode mode, string? name, string? profileId, string? caseId, int? top, ListService service, CancellationToken ct) =>
        {
            try
            {
                return TypedResults.Ok(await service.RetrieveAsync(mode, name, profileId, caseId, Math.Clamp(top ?? 10, 1, 100), ct));
            }
            catch (ArgumentException e)
            {
                return TypedResults.BadRequest(e.Message);
            }
        })
        .WithSummary("Retrieve candidate Profiles: INDEX_SEARCH (name), DIRECT_REFERENCE (profileId), CONTROLLED_SET or PREVIOUS_LINK (caseId)")
        .WithDescription("Counted by Profile, not alias row. The score ranks candidates only; it is never an identity probability.");

        lists.MapGet("/merges", async (ListService service, CancellationToken ct) => TypedResults.Ok(await service.MergesAsync(ct)))
        .WithSummary("Profiles that describe the same person: shared document key or recorded source link, never name similarity")
        .WithDescription("Groups list each accepted merge as an edge with its basis; merges refused for a DOB or gender conflict are listed under rejected.");
    }
}

internal sealed record ProfileVersionResponse(string ProfileId, int Version, string ContentHash, DateTimeOffset CapturedAt);
