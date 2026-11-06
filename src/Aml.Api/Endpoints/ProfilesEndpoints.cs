using Aml.Core.Retrieval;
using Aml.Core.Subjects;
using Aml.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.OpenApi;
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
        .WithDescription("Upload 名單資料_<id>.txt (or .html). An unchanged file keeps its version; a changed one becomes a new version, the old one is kept, and every Case whose latest input used it gets a queued re-analysis job under the returned batchId (GET /api/batches/{batchId}). A file whose name and Profile_ID differ, or one Profile in two formats, is rejected. A Case's own files are sent with POST /api/cases.")
        .WithName("importProfiles")
        .WithTags(Tags.Profiles);

        profiles.MapGet("/search", async Task<Results<Ok<IReadOnlyList<RetrievedCandidate>>, ProblemHttpResult>> (string? name, int? top, ListService service, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return Problems.Of(Problems.InvalidRequest, "name is required.", "Add ?name= with the name to search for.", new { parameter = "name" });
            if (Problems.Top(top) is { } invalid) return invalid;
            return TypedResults.Ok(await service.SearchAsync(name, top ?? DefaultTop, ct));
        })
        .WithSummary("Search the list database by name (INDEX_SEARCH)")
        .WithDescription("Counted by Profile, not alias row; exact alias matches come first and are never cut. The score ranks candidates only; it is never an identity probability.")
        .WithName("searchProfiles")
        .WithTags(Tags.Profiles);

        profiles.MapGet("/merges", async (ListService service, CancellationToken ct) => TypedResults.Ok(await service.MergesAsync(ct)))
        .WithSummary("Profiles that describe the same person: shared document key or recorded source link, never name similarity")
        .WithDescription("Groups list each accepted merge as an edge with its basis; merges refused for a DOB or gender conflict are listed under rejected. Merges never come from name similarity.")
        .WithName("listProfileMerges")
        .WithTags(Tags.Profiles);

        profiles.MapGet("/{profileId}", async Task<Results<Ok<ProfileSummary>, Ok<ProfileView>, NotFound, ProblemHttpResult>> (
            string profileId, int? version, string? view, ListService service, CancellationToken ct) =>
        {
            if (view is not (null or "full" or "summary"))
                return Problems.Of(Problems.InvalidRequest, $"Unknown view '{view}'.", "Send view=full or view=summary.", new { parameter = "view" });
            if (await service.GetProfileAsync(profileId, version, ct) is not { } p) return TypedResults.NotFound();
            return view == "summary" ? TypedResults.Ok(ProfileSummary.Of(p)) : TypedResults.Ok(p);
        })
        .Produces<ProfileSummary>()
        // The 200 body depends on view: ProfileView (full) or ProfileSummary (summary). ProfileView is registered here, and its
        // profile refers to the SourceProfile component instead of an inlined copy. ContractTests fails if either reference dangles.
        .AddOpenApiOperationTransformer(async (op, ctx, ct) =>
        {
            var full = (OpenApiSchema)await ctx.GetOrCreateSchemaAsync(typeof(ProfileView), cancellationToken: ct);
            full.Properties!["profile"] = new OpenApiSchemaReference(nameof(SourceProfile), ctx.Document);
            ctx.Document!.AddComponent(nameof(ProfileView), full);
            op.Responses!["200"].Content!["application/json"].Schema = new OpenApiSchema
            {
                OneOf = [new OpenApiSchemaReference(nameof(ProfileView), ctx.Document), new OpenApiSchemaReference(nameof(ProfileSummary), ctx.Document)],
            };
        })
        .WithSummary("One captured Profile (DIRECT_REFERENCE): view=full (default) as read, view=summary without identifier values; the latest version, or ?version=; the ID may carry the DJ: prefix")
        .WithDescription("view=full returns the Profile as read, with dates of birth, documents, addresses and sources. view=summary gives who a Profile is and why it is listed: names, gender, citizenship, categories, roles and the official-list entry. Both carry the version, latest version and content hash.")
        .WithName("getProfile")
        .WithTags(Tags.Profiles);

        profiles.MapGet("/{profileId}/versions", async Task<Results<Ok<IReadOnlyList<ProfileVersionResponse>>, NotFound>> (string profileId, ListService service, CancellationToken ct) =>
            await service.HistoryAsync(profileId, ct) is { Count: > 0 } history
                ? TypedResults.Ok<IReadOnlyList<ProfileVersionResponse>>(history.Select(v => new ProfileVersionResponse(v.ProfileId, ProfileKeys.KeyOf(v.ProfileId), v.Version, v.ContentHash, v.CapturedAt)).ToList())
                : TypedResults.NotFound())
        .WithSummary("Every captured version of one Profile, with its content hash")
        .WithDescription("When a Profile changed; the content of a version is at GET /api/profiles/{profileId}?version=.")
        .WithName("listProfileVersions")
        .WithTags(Tags.Profiles);
    }
}

internal sealed record ProfileVersionResponse(string ProfileId, string ProfileKey, int Version, string ContentHash, DateTimeOffset CapturedAt);
