using Aml.Core.Retrieval;
using Aml.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using static Aml.Api.Endpoints.Uploads;

namespace Aml.Api.Endpoints;

/// <summary>The list database: Profile import, search, versions and merges. Routes take the Profile ID; responses add the profileKey (DJ:&lt;id&gt;).</summary>
internal static class ProfilesEndpoints
{
    public const int DefaultTop = 10;
    public const int MaxTop = 100;

    public static void MapProfiles(this IEndpointRouteBuilder app)
    {
        var profiles = app.MapGroup("/api/profiles").DisableAntiforgery();

        profiles.MapPost("/import", async Task<Results<Ok<ImportReport>, ProblemHttpResult>> (IFormFileCollection files, ListService service, CancellationToken ct) =>
        {
            var (contents, error) = await ReadFilesAsync(files, ct);
            return contents is null ? error! : TypedResults.Ok(await service.ImportAsync(contents, ct));
        })
        .WithSummary("Capture Profiles into the list database; a changed Profile queues re-analysis of the Cases that used it")
        .WithDescription("Upload 名單資料_<id>.txt (or .html). An unchanged file keeps its version; a changed one becomes a new version, the old one is kept, and every Case whose latest input used it gets a queued re-analysis job (track it with GET /api/batches/{batchId}). A file whose name and Profile_ID differ, or one Profile in two formats, is rejected.");

        profiles.MapGet("/search", async Task<Results<Ok<IReadOnlyList<RetrievedCandidate>>, ProblemHttpResult>> (string? name, int? top, ListService service, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return Problems.Of(Problems.InvalidRequest, "name is required.", "Add ?name= with the name to search for.", new { parameter = "name" });
            if (top is < 1 or > MaxTop)
                return Problems.Of(Problems.InvalidRequest, $"top must be 1 to {MaxTop}.", $"Send a top from 1 to {MaxTop}, or leave it out for {DefaultTop}.", new { parameter = "top" });
            return TypedResults.Ok(await service.SearchAsync(name, top ?? DefaultTop, ct));
        })
        .WithSummary("Search the list database by name (INDEX_SEARCH)")
        .WithDescription("Counted by Profile, not alias row; exact alias matches come first and are never cut. The score ranks candidates only; it is never an identity probability.");

        profiles.MapGet("/merges", async (ListService service, CancellationToken ct) => TypedResults.Ok(await service.MergesAsync(ct)))
        .WithSummary("Profiles that describe the same person: shared document key or recorded source link, never name similarity")
        .WithDescription("Groups list each accepted merge as an edge with its basis; merges refused for a DOB or gender conflict are listed under rejected.");

        profiles.MapGet("/{profileId}", async Task<Results<Ok<ProfileView>, NotFound>> (string profileId, int? version, ListService service, CancellationToken ct) =>
            await service.GetProfileAsync(profileId, version, ct) is { } p ? TypedResults.Ok(p) : TypedResults.NotFound())
        .WithSummary("One Profile as captured (DIRECT_REFERENCE): the latest version, or ?version=; the ID may carry the DJ: prefix");

        profiles.MapGet("/{profileId}/versions", async Task<Results<Ok<IReadOnlyList<ProfileVersionResponse>>, NotFound>> (string profileId, ListService service, CancellationToken ct) =>
            await service.HistoryAsync(profileId, ct) is { Count: > 0 } history
                ? TypedResults.Ok<IReadOnlyList<ProfileVersionResponse>>(history.Select(v => new ProfileVersionResponse(v.ProfileId, "DJ:" + v.ProfileId, v.Version, v.ContentHash, v.CapturedAt)).ToList())
                : TypedResults.NotFound())
        .WithSummary("Every captured version of one Profile, with its content hash");
    }
}

internal sealed record ProfileVersionResponse(string ProfileId, string ProfileKey, int Version, string ContentHash, DateTimeOffset CapturedAt);
