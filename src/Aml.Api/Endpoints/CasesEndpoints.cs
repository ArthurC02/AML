using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Text.Json;
using Microsoft.OpenApi;
using Aml.Core.Retrieval;
using Aml.Core.Subjects;
using Aml.Infra.Txt;
using static Aml.Api.Endpoints.Uploads;

namespace Aml.Api.Endpoints;

/// <summary>Case creation, re-analysis and the stored results.</summary>
internal static class CasesEndpoints
{
    public static void MapCases(this IEndpointRouteBuilder app)
    {
        var cases = app.MapGroup("/api/cases").DisableAntiforgery(); // token-based API, no cookies

        cases.MapPost("/", async Task<Results<JsonHttpResult<CaseSummary>, ProblemHttpResult>> (HttpRequest request, CaseService service, CancellationToken ct) =>
        {
            var (item, input, contents, error) = request.HasFormContentType ? await ReadFormAsync(request, ct) : await ReadJsonAsync(request, ct);
            if (input is null) return error!;
            var (c, inserted) = await service.CreateOrGetAsync(item!.SubjectRef, item.TriggerEventId, input, ct, contents);
            var summary = (await service.GetSummaryAsync(c.CaseId, ct))!;
            if (!inserted) return View(summary);
            request.HttpContext.Response.Headers.Location = $"/api/cases/{c.CaseId}";
            return View(summary, StatusCodes.Status201Created);
        })
        .AddOpenApiOperationTransformer(async (op, ctx, ct) => op.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["multipart/form-data"] = new() { Schema = await ctx.GetOrCreateSchemaAsync(typeof(CaseUploadForm), cancellationToken: ct) },
                ["application/json"] = new() { Schema = await ctx.GetOrCreateSchemaAsync(typeof(BatchItemRequest), cancellationToken: ct) },
            },
        })
        .Produces<CaseSummary>(StatusCodes.Status201Created)
        .Produces<CaseSummary>()
        .WithSummary("Create a Case from the export files and run the first analysis; 201 when created, 200 when it already existed")
        .WithDescription("Send multipart form fields subjectRef, triggerEventId and files (案件資訊*.txt, 名單資料_<id>.txt, the customer or related-party files and any 新聞資訊_<id>.txt, or the same pages as HTML), or JSON {subjectRef, triggerEventId, files: {name: text}} as one batch item. Repeating the same triggerEventId and subjectRef returns the existing Case with 200. Returns the Case summary. Use it once per alert (trigger event and subject). For new files of an existing Case use reanalyzeCase; for many Cases use submitBatch. Next: getHit through hits[].link, or getRevision through revision. Example: GC-03 gives result REVIEW with one hit, DJ:90000031, identityDecision PROBABLE_MATCH.")
        .WithName("createCase")
        .WithTags("write");

        cases.MapPost("/{caseId}/analyses", async Task<Results<JsonHttpResult<CaseSummary>, NotFound, ProblemHttpResult>> (
            string caseId, IFormFileCollection files, CaseService service, CancellationToken ct) =>
        {
            var (input, contents, error) = await ReadUploadAsync(files, ct);
            if (input is null) return error!;
            var c = await service.ReanalyzeAsync(caseId, input, ct, contents);
            return c is null ? TypedResults.NotFound() : View((await service.GetSummaryAsync(c.CaseId, ct))!);
        })
        .Produces<CaseSummary>()
        .WithSummary("Run a new analysis revision with updated input files; returns the Case summary")
        .WithDescription("Send the full set of export files again, as multipart, as for createCase. Use it when the bank files of an existing Case changed; a changed list Profile is re-analysed by itself after importProfiles. A revision that is not complete does not replace the effective result. Next: listRevisions.")
        .WithName("reanalyzeCase")
        .WithTags("write");

        cases.MapGet("/", async Task<Results<JsonHttpResult<CasePage>, ProblemHttpResult>> (
            string? subjectRef, string? triggerEventId, int? pageSize, string? pageToken, CaseService service, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(subjectRef) && string.IsNullOrEmpty(triggerEventId))
                return Problems.Of(Problems.InvalidRequest, "Give subjectRef, triggerEventId or both.", "Add ?subjectRef= or ?triggerEventId=.", new { parameter = "subjectRef" });
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
            return View(await service.SearchAsync(NullIfEmpty(subjectRef), NullIfEmpty(triggerEventId), size, after, ct));
        })
        .Produces<CasePage>()
        .WithSummary("Find Cases by subject and/or trigger event, oldest first, one page at a time")
        .WithDescription("Use it to find the caseId of a subject or trigger event; give subjectRef, triggerEventId or both. Returns one page of Cases and nextPageToken when more follow; pass it back as pageToken. Next: getCaseSummary.")
        .WithName("findCases")
        .WithTags("read");

        cases.MapGet("/{caseId}", async Task<Results<JsonHttpResult<CaseSummary>, NotFound>> (string caseId, CaseService service, CancellationToken ct) =>
            await service.GetSummaryAsync(caseId, ct) is { } s ? View(s) : TypedResults.NotFound())
        .Produces<CaseSummary>()
        .WithSummary("What the Case concluded: result, conclusion and one line per hit from the effective revision, with links to the hits and the full revision")
        .WithDescription("Start here to learn what a Case concluded. It holds no identifier values and, of personal data, only names. Hits are ordered by review priority; at most 10 are listed and the rest counted in omitted. Next: getHit for why a hit was decided, getRevision for the full record. Example: GC-03 gives result REVIEW with one hit, DJ:90000031, identityDecision PROBABLE_MATCH.")
        .WithName("getCaseSummary")
        .WithTags("read");

        cases.MapGet("/{caseId}/hits/{hit:int}", async Task<Results<JsonHttpResult<HitSummary>, JsonHttpResult<HitExplanation>, NotFound, ProblemHttpResult>> (
            string caseId, int hit, string? view, int? revision, CaseService service, CancellationToken ct) =>
        {
            if (view is not (null or "summary" or "detail"))
                return Problems.Of(Problems.InvalidRequest, $"Unknown view '{view}'.", "Send view=summary or view=detail.", new { parameter = "view" });
            if (await service.GetHitAsync(caseId, hit, revision, ct) is not var (rev, h)) return TypedResults.NotFound();
            return view == "detail" ? View(h) : View(CaseViews.Hit(caseId, rev, h));
        })
        .Produces<HitSummary>()
        .WithSummary("One hit of the effective revision (or ?revision=): view=summary (default) without compared values, view=detail with the full explanation (HitExplanation)")
        .WithDescription("Use view=summary to learn why the hit was decided: decisive evidence, reason codes and wouldChangeIf. Use view=detail only when the compared subject and Profile values are needed. wouldChangeIf names the stage 3 row a comparable document or full DOB would move the identity decision to; it never states an external result. Example: GC-03 hit 1 has sourceConditions [OFFICIAL_WANTED_LIST], identityDecision PROBABLE_MATCH at stage 3 row 4 and pairResult REVIEW. Next: getCode for any code.")
        .WithName("getHit")
        .WithTags("read");

        cases.MapGet("/{caseId}/candidates", async Task<Results<Ok<IReadOnlyList<RetrievedCandidate>>, NotFound, ProblemHttpResult>> (
            string caseId, string? source, int? top, ListService lists, CancellationToken ct) =>
        {
            if (source is not (null or "table" or "previous"))
                return Problems.Of(Problems.InvalidRequest, $"Unknown source '{source}'.", "Send source=table (the Case's candidate table) or source=previous (the Profiles of its latest input).", new { parameter = "source" });
            if (top is < 1 or > ProfilesEndpoints.MaxTop)
                return Problems.Of(Problems.InvalidRequest, $"top must be 1 to {ProfilesEndpoints.MaxTop}.", $"Send a top from 1 to {ProfilesEndpoints.MaxTop}, or leave it out for {ProfilesEndpoints.DefaultTop}.", new { parameter = "top" });
            var found = await lists.CaseCandidatesAsync(caseId, source == "previous" ? CandidateSource.Previous : CandidateSource.Table, top ?? ProfilesEndpoints.DefaultTop, ct);
            return found is null ? TypedResults.NotFound() : TypedResults.Ok(found);
        })
        .WithSummary("A Case's candidate Profiles: source=table (CONTROLLED_SET, its own candidate table, the default) or source=previous (PREVIOUS_LINK, the Profiles of its latest input)")
        .WithDescription("Use it to see which Profiles a Case was screened against; to search the whole list database use searchProfiles. truncated marks a list cut by top. Next: getProfile.")
        .WithName("listCaseCandidates")
        .WithTags("read");

        cases.MapGet("/{caseId}/revisions", async Task<Results<JsonHttpResult<IReadOnlyList<RevisionLine>>, NotFound>> (string caseId, CaseService service, CancellationToken ct) =>
            await service.GetRevisionsAsync(caseId, ct) is { } list ? View(list) : TypedResults.NotFound())
        .Produces<IReadOnlyList<RevisionLine>>()
        .WithSummary("Every analysis revision: time, execution status, result, whether it is effective and how many changes it made")
        .WithDescription("Use it to see the history of a Case and which revision is effective. Next: getRevision.")
        .WithName("listRevisions")
        .WithTags("read");

        cases.MapGet("/{caseId}/revisions/{revision:int}", async Task<Results<JsonHttpResult<AnalysisRecord>, NotFound>> (string caseId, int revision, CaseService service, CancellationToken ct) =>
            await service.GetAnalysisAsync(caseId, revision, ct) is { } a ? View(a) : TypedResults.NotFound())
        .Produces<AnalysisRecord>()
        .WithSummary("The full record of one revision: result, explanation, lineage and data quality")
        .WithDescription("The audit record: nothing is left out, so it is large and holds identifier values. Prefer getCaseSummary and getHit unless the full record is needed. Lineage gives the input file hashes, the Profile versions and the rule versions. Next: getCode for any code, getSchema (revision) for the meaning of a field.")
        .WithName("getRevision")
        .WithTags("read");
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private const string ItemFix = "Send subjectRef, triggerEventId and files: .txt, .html or .htm files up to 5 MB each.";

    // Multipart: the file bytes decide the encoding, as for any upload.
    private static async Task<(BatchItemRequest? Item, CaseInput? Input, IReadOnlyDictionary<string, string>? Contents, ProblemHttpResult? Error)> ReadFormAsync(HttpRequest request, CancellationToken ct)
    {
        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(ct);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return (null, null, null, Problems.Of(Problems.InvalidRequest, "The body is not a readable multipart form.", ItemFix));
        }
        var (subjectRef, triggerEventId) = (form["subjectRef"].ToString(), form["triggerEventId"].ToString());
        if (form["subjectRef"].Count != 1 || form["triggerEventId"].Count != 1 || string.IsNullOrWhiteSpace(subjectRef) || string.IsNullOrWhiteSpace(triggerEventId))
            return (null, null, null, Problems.Of(Problems.InvalidRequest, "subjectRef and triggerEventId are required.", ItemFix, new { parameter = "subjectRef" }, new { parameter = "triggerEventId" }));
        var (input, contents, error) = await ReadUploadAsync(form.Files, ct);
        return (new BatchItemRequest(subjectRef, triggerEventId, contents ?? []), input, contents, error);
    }

    // JSON: one batch item; the files are text, hashed as their UTF-8 encoding.
    private static async Task<(BatchItemRequest? Item, CaseInput? Input, IReadOnlyDictionary<string, string>? Contents, ProblemHttpResult? Error)> ReadJsonAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasJsonContentType())
            return (null, null, null, Problems.Of(Problems.InvalidRequest, "Send multipart/form-data or application/json.", ItemFix));
        BatchItemRequest? item;
        try
        {
            item = await request.ReadFromJsonAsync<BatchItemRequest>(ct);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return (null, null, null, Problems.Of(Problems.InvalidRequest, "The body is not a JSON object with subjectRef, triggerEventId and files.", ItemFix));
        }
        if (!ValidItem(item))
            return (null, null, null, Problems.Of(Problems.InvalidRequest, "subjectRef, triggerEventId and .txt, .html or .htm files up to 5 MB each are required.", ItemFix));
        try
        {
            return (item, TxtCaseReader.ReadFiles(item!.Files), item.Files, null);
        }
        catch (CaseInputException e)
        {
            return (null, null, null, Problems.Input(e));
        }
    }

    // Views and stored records are written as their schemas describe them: nulls left out.
    private static JsonHttpResult<T> View<T>(T value, int status = StatusCodes.Status200OK) => TypedResults.Json(value, CaseJson.Options, statusCode: status);
}

/// <summary>The multipart form of POST /api/cases (OpenAPI description only).</summary>
internal sealed record CaseUploadForm(string SubjectRef, string TriggerEventId, IFormFileCollection Files);
