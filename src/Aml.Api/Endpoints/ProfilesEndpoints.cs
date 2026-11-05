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
        .WithDescription("Upload 名單資料_<id>.txt (or .html). An unchanged file keeps its version; a changed one becomes a new version, the old one is kept, and every Case whose latest input used it gets a queued re-analysis job (track it with GET /api/batches/{batchId}). A file whose name and Profile_ID differ, or one Profile in two formats, is rejected. Not for a Case's own files: send those with createCase or reanalyzeCase. Next: getBatchStatus with the returned batchId, when it is not null.")
        .WithName("importProfiles")
        .WithTags("write");

        profiles.MapGet("/search", async Task<Results<Ok<IReadOnlyList<RetrievedCandidate>>, ProblemHttpResult>> (string? name, int? top, ListService service, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return Problems.Of(Problems.InvalidRequest, "name is required.", "Add ?name= with the name to search for.", new { parameter = "name" });
            if (top is < 1 or > MaxTop)
                return Problems.Of(Problems.InvalidRequest, $"top must be 1 to {MaxTop}.", $"Send a top from 1 to {MaxTop}, or leave it out for {DefaultTop}.", new { parameter = "top" });
            return TypedResults.Ok(await service.SearchAsync(name, top ?? DefaultTop, ct));
        })
        .WithSummary("Search the list database by name (INDEX_SEARCH)")
        .WithDescription("Counted by Profile, not alias row; exact alias matches come first and are never cut. The score ranks candidates only; it is never an identity probability. Use it to look a name up in the list database; for the Profiles a Case was screened against use listCaseCandidates. Next: getProfile.")
        .WithName("searchProfiles")
        .WithTags("read");

        profiles.MapGet("/merges", async (ListService service, CancellationToken ct) => TypedResults.Ok(await service.MergesAsync(ct)))
        .WithSummary("Profiles that describe the same person: shared document key or recorded source link, never name similarity")
        .WithDescription("Groups list each accepted merge as an edge with its basis; merges refused for a DOB or gender conflict are listed under rejected. Merges never come from name similarity; to look a name up use searchProfiles instead. Next: getProfile for a Profile of a group.")
        .WithName("listProfileMerges")
        .WithTags("read");

        profiles.MapGet("/{profileId}", async Task<Results<Ok<ProfileSummary>, Ok<ProfileView>, NotFound, ProblemHttpResult>> (
            string profileId, int? version, string? view, ListService service, CancellationToken ct) =>
        {
            if (view is not (null or "summary" or "full"))
                return Problems.Of(Problems.InvalidRequest, $"Unknown view '{view}'.", "Send view=summary or view=full.", new { parameter = "view" });
            if (await service.GetProfileAsync(profileId, version, ct) is not { } p) return TypedResults.NotFound();
            return view == "full" ? TypedResults.Ok(p) : TypedResults.Ok(ProfileSummary.Of(p));
        })
        .Produces<ProfileSummary>()
        // The 200 body depends on view: ProfileSummary (summary) or ProfileView (full). ProfileView is registered here, and its
        // profile refers to the SourceProfile component instead of an inlined copy. ContractTests fails if either reference dangles.
        .AddOpenApiOperationTransformer(async (op, ctx, ct) =>
        {
            var full = (OpenApiSchema)await ctx.GetOrCreateSchemaAsync(typeof(ProfileView), cancellationToken: ct);
            full.Properties!["profile"] = new OpenApiSchemaReference(nameof(SourceProfile), ctx.Document);
            ctx.Document!.AddComponent(nameof(ProfileView), full);
            op.Responses!["200"].Content!["application/json"].Schema = new OpenApiSchema
            {
                OneOf = [new OpenApiSchemaReference(nameof(ProfileSummary), ctx.Document), new OpenApiSchemaReference(nameof(ProfileView), ctx.Document)],
            };
        })
        .WithSummary("One captured Profile (DIRECT_REFERENCE): view=summary (default) without identifier values, view=full as read; the latest version, or ?version=; the ID may carry the DJ: prefix")
        .WithDescription("Use view=summary to see who a Profile is and why it is listed: names, gender, citizenship, categories, roles and the official-list entry, with its version, latest version and content hash. Use view=full only when dates of birth, documents, addresses or sources are needed. To find a Profile by name use searchProfiles instead. Next: listProfileVersions.")
        .WithName("getProfile")
        .WithTags("read");

        profiles.MapGet("/{profileId}/versions", async Task<Results<Ok<IReadOnlyList<ProfileVersionResponse>>, NotFound>> (string profileId, ListService service, CancellationToken ct) =>
            await service.HistoryAsync(profileId, ct) is { Count: > 0 } history
                ? TypedResults.Ok<IReadOnlyList<ProfileVersionResponse>>(history.Select(v => new ProfileVersionResponse(v.ProfileId, ProfileKeys.Prefix + v.ProfileId, v.Version, v.ContentHash, v.CapturedAt)).ToList())
                : TypedResults.NotFound())
        .WithSummary("Every captured version of one Profile, with its content hash")
        .WithDescription("Use it to see when a Profile changed; for the content of a version use getProfile instead. Next: getProfile with ?version=.")
        .WithName("listProfileVersions")
        .WithTags("read");
    }
}

internal sealed record ProfileVersionResponse(string ProfileId, string ProfileKey, int Version, string ContentHash, DateTimeOffset CapturedAt);
