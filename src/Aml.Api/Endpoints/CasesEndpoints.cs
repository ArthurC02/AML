using Aml.Core.Cases;
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

        cases.MapPost("/", async Task<Results<Created<CaseResponse>, BadRequest<string>>> (
            [FromForm] string subjectRef, [FromForm] string triggerEventId, IFormFileCollection files, CaseService service, CancellationToken ct) =>
        {
            var (input, contents, error) = await ReadUploadAsync(files, ct);
            if (input is null) return TypedResults.BadRequest(error);
            var c = await service.CreateAsync(subjectRef, triggerEventId, input, ct, contents);
            return TypedResults.Created($"/api/cases/{c.CaseId}", CaseResponse.From(c));
        })
        .WithSummary("Create a Case from the .txt export files (or the same pages as HTML) and run the first analysis")
        .WithDescription("Upload 案件資訊*.txt, 名單資料_<id>.txt, the customer or related-party files and any 新聞資訊_<id>.txt. Repeating the same triggerEventId and subjectRef returns the existing Case.");

        cases.MapPost("/{caseId}/analyses", async Task<Results<Ok<CaseResponse>, NotFound, BadRequest<string>>> (
            string caseId, IFormFileCollection files, CaseService service, CancellationToken ct) =>
        {
            var (input, contents, error) = await ReadUploadAsync(files, ct);
            if (input is null) return TypedResults.BadRequest(error);
            var c = await service.ReanalyzeAsync(caseId, input, ct, contents);
            return c is null ? TypedResults.NotFound() : TypedResults.Ok(CaseResponse.From(c));
        })
        .WithSummary("Run a new analysis revision with updated input files");

        cases.MapGet("/{caseId}", async Task<Results<Ok<CaseResponse>, NotFound>> (string caseId, CaseService service, CancellationToken ct) =>
            await service.GetAsync(caseId, ct) is { } c ? TypedResults.Ok(CaseResponse.From(c)) : TypedResults.NotFound());

        cases.MapGet("/{caseId}/analyses/{revision:int}", async Task<Results<ContentHttpResult, NotFound>> (string caseId, int revision, CaseService service, CancellationToken ct) =>
            await service.GetAnalysisAsync(caseId, revision, ct) is { } a
                ? TypedResults.Content(a.ResultJson, "application/json")
                : TypedResults.NotFound())
        .WithSummary("Analysis result: hits, source conditions, evidence, identity decision and reason codes");

        cases.MapGet("/{caseId}/explanation", async Task<Results<ContentHttpResult, NotFound>> (string caseId, CaseService service, CancellationToken ct) =>
            await service.GetExplanationAsync(caseId, ct) is { } json
                ? TypedResults.Content(json, "application/json")
                : TypedResults.NotFound())
        .WithSummary("Reviewer and audit view of the effective revision: hit reason, comparison table, evidence, reason codes and a five-part summary");
    }
}

internal sealed record CaseResponse(string CaseId, string SubjectRef, string TriggerEventId, CaseStatus Status, int CurrentRevision, int? EffectiveRevision)
{
    public static CaseResponse From(CaseRecord c) =>
        new(c.CaseId, c.SubjectRef, c.TriggerEventId, c.Status, c.CurrentRevision, c.EffectiveRevision);
}
