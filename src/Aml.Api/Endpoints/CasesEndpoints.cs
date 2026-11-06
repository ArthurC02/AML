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
        .WithDescription("Send multipart form fields subjectRef, triggerEventId and files (案件資訊*.txt, 名單資料_<id>.txt, the customer or related-party files and any 新聞資訊_<id>.txt, or the same pages as HTML), or JSON {subjectRef, triggerEventId, files: {name: text}} as one batch item. One Case per alert (trigger event and subject): repeating the same triggerEventId and subjectRef returns the existing Case with 200. Returns the Case summary with links to its hits and its full revision. Example: GC-03 gives result REVIEW with one hit, DJ:90000031, identityDecision PROBABLE_MATCH.")
        .WithExample(Examples.CaseSummary, "201", "200")
        .WithName("createCase")
        .WithTags(Tags.Cases);

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
        .WithDescription("Send the full set of export files again, as multipart, as for POST /api/cases, when the bank files of an existing Case changed. A changed list Profile needs no call: POST /api/profiles/import queues that re-analysis. A revision that is not complete does not replace the effective result.")
        .WithName("reanalyzeCase")
        .WithTags(Tags.Cases);

        cases.MapGet("/", async Task<Results<JsonHttpResult<CasePage>, ProblemHttpResult>> (
            string? subjectRef, string? triggerEventId, int? pageSize, string? pageToken, CaseService service, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(subjectRef) && string.IsNullOrEmpty(triggerEventId))
                return Problems.Of(Problems.InvalidRequest, "Give subjectRef, triggerEventId or both.", "Add ?subjectRef= or ?triggerEventId=.", new { parameter = "subjectRef" });
            if (Problems.Paging(pageSize, pageToken, out var size, out var after) is { } paging) return paging;
            return View(await service.SearchAsync(NullIfEmpty(subjectRef), NullIfEmpty(triggerEventId), size, after, ct));
        })
        .Produces<CasePage>()
        .WithSummary("Find Cases by subject and/or trigger event, oldest first, one page at a time")
        .WithDescription("Give subjectRef, triggerEventId or both. Returns one page of Cases and nextPageToken when more follow; pass it back as pageToken.")
        .WithName("findCases")
        .WithTags(Tags.Cases);

        cases.MapGet("/{caseId}", async Task<Results<JsonHttpResult<CaseDetail>, JsonHttpResult<CaseSummary>, NotFound, ProblemHttpResult>> (
            string caseId, string? view, CaseService service, CancellationToken ct) =>
        {
            if (view is not (null or "full" or "summary"))
                return Problems.Of(Problems.InvalidRequest, $"Unknown view '{view}'.", "Send view=full or view=summary.", new { parameter = "view" });
            if (view == "summary")
                return await service.GetSummaryAsync(caseId, ct) is { } s ? View(s) : TypedResults.NotFound();
            return await service.GetDetailAsync(caseId, ct) is { } d ? View(d) : TypedResults.NotFound();
        })
        .Produces<CaseDetail>()
        .WithOkOneOfByView(nameof(CaseDetail), nameof(CaseSummary))
        .WithSummary("One Case: view=full (default) with the full record of its effective revision, view=summary with the result, a conclusion and one line per hit")
        .WithDescription("view=full returns the Case and the full record of its effective revision: result, explanation, lineage and data quality, with identifier values. view=summary holds no identifier values and, of personal data, only names; hits are ordered by review priority, at most 10 are listed and the rest counted in omitted. Example (view=summary): GC-03 gives result REVIEW with one hit, DJ:90000031, identityDecision PROBABLE_MATCH.")
        .WithViewExample(Examples.CaseSummary, "summary")
        .WithName("getCase")
        .WithTags(Tags.Cases);

        cases.MapGet("/{caseId}/hits/{hit:int}", async Task<Results<JsonHttpResult<HitExplanation>, JsonHttpResult<HitSummary>, NotFound, ProblemHttpResult>> (
            string caseId, int hit, string? view, int? revision, CaseService service, CancellationToken ct) =>
        {
            if (view is not (null or "detail" or "summary"))
                return Problems.Of(Problems.InvalidRequest, $"Unknown view '{view}'.", "Send view=detail or view=summary.", new { parameter = "view" });
            if (await service.GetHitAsync(caseId, hit, revision, ct) is not var (rev, h)) return TypedResults.NotFound();
            return view == "summary" ? View(CaseViews.Hit(caseId, rev, h)) : View(h);
        })
        .Produces<HitSummary>()
        .WithOkOneOfByView(nameof(HitExplanation), nameof(HitSummary))
        .WithSummary("One hit of the effective revision (or ?revision=): view=detail (default) with the full explanation, view=summary without compared values")
        .WithDescription("view=detail returns the hit explanation with the compared subject and Profile values. view=summary gives the decisive evidence, reason codes and wouldChangeIf without values. wouldChangeIf names the stage 3 row a comparable document or full DOB would move the identity decision to; it never states an external result. Codes are explained at /api/codes/{code}. Example (view=summary): GC-03 hit 1 has sourceConditions [OFFICIAL_WANTED_LIST], identityDecision PROBABLE_MATCH at stage 3 row 4 and pairResult REVIEW.")
        .WithViewExample(Examples.HitSummary, "summary")
        .WithName("getHit")
        .WithTags(Tags.Cases);

        cases.MapGet("/{caseId}/candidates", async Task<Results<Ok<IReadOnlyList<RetrievedCandidate>>, NotFound, ProblemHttpResult>> (
            string caseId, string? source, int? top, ListService lists, CancellationToken ct) =>
        {
            if (source is not (null or "table" or "previous"))
                return Problems.Of(Problems.InvalidRequest, $"Unknown source '{source}'.", "Send source=table (the Case's candidate table) or source=previous (the Profiles of its latest input).", new { parameter = "source" });
            if (Problems.Top(top) is { } invalid) return invalid;
            var found = await lists.CaseCandidatesAsync(caseId, source == "previous" ? CandidateSource.LatestInputProfileFiles : CandidateSource.CaseTable, top ?? ProfilesEndpoints.DefaultTop, ct);
            return found is null ? TypedResults.NotFound() : TypedResults.Ok(found);
        })
        .WithSummary("A Case's candidate Profiles: source=table (CONTROLLED_SET, its own candidate table, the default) or source=previous (PREVIOUS_LINK, the Profiles of its latest input)")
        .WithDescription("The Profiles a Case was screened against. truncated marks a list cut by top.")
        .WithName("listCaseCandidates")
        .WithTags(Tags.Cases);

        cases.MapGet("/{caseId}/revisions", async Task<Results<JsonHttpResult<IReadOnlyList<RevisionLine>>, NotFound>> (string caseId, CaseService service, CancellationToken ct) =>
            await service.GetRevisionsAsync(caseId, ct) is { } list ? View(list) : TypedResults.NotFound())
        .Produces<IReadOnlyList<RevisionLine>>()
        .WithSummary("Every analysis revision: time, execution status, result, whether it is effective and how many changes it made")
        .WithDescription("The analysis history of a Case and which revision is effective.")
        .WithName("listRevisions")
        .WithTags(Tags.Cases);

        cases.MapGet("/{caseId}/revisions/{revision:int}", async Task<Results<JsonHttpResult<AnalysisRecord>, NotFound>> (string caseId, int revision, CaseService service, CancellationToken ct) =>
            await service.GetAnalysisAsync(caseId, revision, ct) is { } a ? View(a) : TypedResults.NotFound())
        .Produces<AnalysisRecord>()
        .WithSummary("The full record of one revision: result, explanation, lineage and data quality")
        .WithDescription("The audit record: nothing is left out, so it holds identifier values. Lineage gives the input file hashes, the Profile versions and the rule versions. Field meanings are in /api/schemas/revision, codes at /api/codes/{code}.")
        .WithName("getRevision")
        .WithTags(Tags.Cases);
    }

    // Each schema is referenced, not registered: registering one here inlines its nested types. ContractTests fails if a reference dangles.
    private static RouteHandlerBuilder WithOkOneOfByView(this RouteHandlerBuilder builder, string defaultViewSchema, string summaryViewSchema) =>
        builder.AddOpenApiOperationTransformer((op, ctx, ct) =>
        {
            op.Responses!["200"].Content!["application/json"].Schema = new OpenApiSchema
            {
                OneOf = [new OpenApiSchemaReference(defaultViewSchema, ctx.Document), new OpenApiSchemaReference(summaryViewSchema, ctx.Document)],
            };
            return Task.CompletedTask;
        });

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static readonly string ItemFix = $"Send subjectRef, triggerEventId and files: .txt, .html or .htm files up to {MaxSizeText} each.";

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

    // The JSON files are text, hashed as their UTF-8 encoding.
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
            return (null, null, null, Problems.Of(Problems.InvalidRequest, $"subjectRef, triggerEventId and .txt, .html or .htm files up to {MaxSizeText} each are required.", ItemFix));
        return (item, TxtCaseReader.ReadFiles(item!.Files), item.Files, null);
    }

    private static JsonHttpResult<T> View<T>(T value, int status = StatusCodes.Status200OK) => TypedResults.Json(value, CaseJson.Options, statusCode: status);
}

/// <summary>OpenAPI description only.</summary>
internal sealed record CaseUploadForm(string SubjectRef, string TriggerEventId, IFormFileCollection Files);
