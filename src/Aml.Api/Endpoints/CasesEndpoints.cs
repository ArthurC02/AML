using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using static Aml.Api.Endpoints.Uploads;

namespace Aml.Api.Endpoints;

/// <summary>Case creation, re-analysis and the stored results.</summary>
internal static class CasesEndpoints
{
    public static void MapCases(this IEndpointRouteBuilder app)
    {
        var cases = app.MapGroup("/api/cases").DisableAntiforgery(); // token-based API, no cookies

        cases.MapPost("/", async Task<Results<JsonHttpResult<CaseSummary>, ProblemHttpResult>> (
            [FromForm] string subjectRef, [FromForm] string triggerEventId, IFormFileCollection files, CaseService service, HttpContext http, CancellationToken ct) =>
        {
            var (input, contents, error) = await ReadUploadAsync(files, ct);
            if (input is null) return error!;
            var c = await service.CreateAsync(subjectRef, triggerEventId, input, ct, contents);
            http.Response.Headers.Location = $"/api/cases/{c.CaseId}";
            return View((await service.GetSummaryAsync(c.CaseId, ct))!, StatusCodes.Status201Created);
        })
        .Produces<CaseSummary>(StatusCodes.Status201Created)
        .WithSummary("Create a Case from the .txt export files (or the same pages as HTML) and run the first analysis")
        .WithDescription("Upload 案件資訊*.txt, 名單資料_<id>.txt, the customer or related-party files and any 新聞資訊_<id>.txt. Repeating the same triggerEventId and subjectRef returns the existing Case. Returns the Case summary.");

        cases.MapPost("/{caseId}/analyses", async Task<Results<JsonHttpResult<CaseSummary>, NotFound, ProblemHttpResult>> (
            string caseId, IFormFileCollection files, CaseService service, CancellationToken ct) =>
        {
            var (input, contents, error) = await ReadUploadAsync(files, ct);
            if (input is null) return error!;
            var c = await service.ReanalyzeAsync(caseId, input, ct, contents);
            return c is null ? TypedResults.NotFound() : View((await service.GetSummaryAsync(c.CaseId, ct))!);
        })
        .Produces<CaseSummary>()
        .WithSummary("Run a new analysis revision with updated input files; returns the Case summary");

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
        .WithSummary("Find Cases by subject and/or trigger event, oldest first, one page at a time");

        cases.MapGet("/{caseId}", async Task<Results<JsonHttpResult<CaseSummary>, NotFound>> (string caseId, CaseService service, CancellationToken ct) =>
            await service.GetSummaryAsync(caseId, ct) is { } s ? View(s) : TypedResults.NotFound())
        .Produces<CaseSummary>()
        .WithSummary("What the Case concluded: result, conclusion and one line per hit from the effective revision, with links to the hits and the full revision");

        cases.MapGet("/{caseId}/hits/{hit:int}", async Task<Results<JsonHttpResult<HitSummary>, JsonHttpResult<HitExplanation>, NotFound, ProblemHttpResult>> (
            string caseId, int hit, string? view, int? revision, CaseService service, CancellationToken ct) =>
        {
            if (view is not (null or "summary" or "detail"))
                return Problems.Of(Problems.InvalidRequest, $"Unknown view '{view}'.", "Send view=summary or view=detail.", new { parameter = "view" });
            if (await service.GetHitAsync(caseId, hit, revision, ct) is not var (rev, h)) return TypedResults.NotFound();
            return view == "detail" ? View(h) : View(CaseViews.Hit(caseId, rev, h));
        })
        .Produces<HitSummary>()
        .WithSummary("One hit of the effective revision (or ?revision=): view=summary (default) without compared values, view=detail with the full explanation (HitExplanation)");

        cases.MapGet("/{caseId}/revisions", async Task<Results<JsonHttpResult<IReadOnlyList<RevisionLine>>, NotFound>> (string caseId, CaseService service, CancellationToken ct) =>
            await service.GetRevisionsAsync(caseId, ct) is { } list ? View(list) : TypedResults.NotFound())
        .Produces<IReadOnlyList<RevisionLine>>()
        .WithSummary("Every analysis revision: time, execution status, result, whether it is effective and how many changes it made");

        cases.MapGet("/{caseId}/revisions/{revision:int}", async Task<Results<JsonHttpResult<AnalysisRecord>, NotFound>> (string caseId, int revision, CaseService service, CancellationToken ct) =>
            await service.GetAnalysisAsync(caseId, revision, ct) is { } a ? View(a) : TypedResults.NotFound())
        .Produces<AnalysisRecord>()
        .WithSummary("The full record of one revision: result, explanation, lineage and data quality");

        cases.MapGet("/{caseId}/analyses/{revision:int}", async Task<Results<JsonHttpResult<CaseAnalysis>, NotFound>> (string caseId, int revision, CaseService service, CancellationToken ct) =>
            await service.GetAnalysisAsync(caseId, revision, ct) is { } a
                ? TypedResults.Json(a.Result, CaseJson.Options)
                : TypedResults.NotFound())
        .WithSummary("Analysis result: hits, source conditions, evidence, identity decision and reason codes");

        cases.MapGet("/{caseId}/explanation", async Task<Results<JsonHttpResult<CaseExplanation>, NotFound>> (string caseId, CaseService service, CancellationToken ct) =>
            await service.GetExplanationAsync(caseId, ct) is { } explanation
                ? TypedResults.Json(explanation, CaseJson.Options)
                : TypedResults.NotFound())
        .WithSummary("Reviewer and audit view of the effective revision: hit reason, comparison table, evidence, reason codes and a five-part summary");
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // Views and stored records are written as their schemas describe them: nulls left out.
    private static JsonHttpResult<T> View<T>(T value, int status = StatusCodes.Status200OK) => TypedResults.Json(value, CaseJson.Options, statusCode: status);
}
