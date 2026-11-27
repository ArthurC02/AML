using Aml.Core.Pages;
using Aml.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aml.Api.Endpoints;

internal static class PagesEndpoints
{
    public static void MapPages(this IEndpointRouteBuilder app)
    {
        var pages = app.MapGroup("/api/pages");

        pages.MapPost("/", async Task<Results<Ok<PageRecord>, Accepted<PageQueued>, ProblemHttpResult>> (PageRequest request, PageService service, CancellationToken ct) =>
        {
            var (canonical, broken) = PageUrl.Read(request.Url);
            if (canonical is null)
                return Problems.Of(Problems.InvalidRequest, $"The URL cannot be fetched: {broken}.",
                    $"Send {{\"url\": \"…\"}} with an absolute http or https URL, without a user name or password, on port 80 or 443, of at most {PageUrl.MaxLength} characters.",
                    new { parameter = "url", rule = broken });
            var (page, queued) = await service.SubmitAsync(canonical, ct);
            return queued
                ? TypedResults.Accepted($"/api/pages/{page.PageId}", new PageQueued(page.PageId, page.Url, page.Status, page.JobId, page.BatchId))
                : TypedResults.Ok(PageReadings.Summary(page));
        })
        .WithSummary("Receive a URL: the page as stored when fetched within a day, else queue a FETCH_PAGE job")
        .WithDescription("The URL is canonicalised (lower-case host, no default port, no fragment, no utm_* parameter); pageId is the first 16 hex digits of its SHA-256. A page fetched within a day returns 200 with its summary view. Otherwise 202 with Location /api/pages/{pageId}, the page PENDING and its FETCH_PAGE job (jobId, batchId; GET /api/batches/{batchId}). The job fetches through the URL guard (every request and redirect hop must resolve to public addresses only, port 80 or 443, no credentials), follows robots.txt and reads the main text; the job payload holds the page id and URL only. A URL that is not absolute http(s), holds a user name or password, uses another port or is over 2,048 characters is INVALID_REQUEST.")
        .WithName("fetchPage")
        .WithTags(Tags.Sources);

        pages.MapGet("/{pageId}", async Task<Results<Ok<PageRecord>, NotFound, ProblemHttpResult>> (string pageId, string? view, PageService service, CancellationToken ct) =>
        {
            if (view is not (null or "summary" or "full"))
                return Problems.Of(Problems.InvalidRequest, $"Unknown view '{view}'.", "Send view=summary (default) or view=full.", new { parameter = "view" });
            if (await service.GetAsync(pageId, ct) is not { } page) return TypedResults.NotFound();
            return TypedResults.Ok(view == "full" ? page : PageReadings.Summary(page));
        })
        .WithSummary("A received page: status (PENDING, READ, REFUSED or FAILED with its problem) and, once READ, the persons it names")
        .WithDescription("Each person is read by the rules of the news digest, and by the news models and stage models when configured: profileKey (WEB:<pageId>-<n>), name and the forms written, roles, party, every mention's offset in the text, and the legal stage with its basis (and each stage reader when models vote). view=summary (default) leaves out the text, the identifiers written next to a name, the stated age, birth dates and gender, the events and the readers' quotes; view=full adds them, each identifier and event with its offset. A page decides nothing: it is not matched against anyone here. A problem is a PAGE_PROBLEM code in /api/codes.")
        .WithName("getPage")
        .WithTags(Tags.Sources);
    }
}

internal sealed record PageRequest(string? Url);

internal sealed record PageQueued(string PageId, string Url, PageStatus Status, string? JobId, string? BatchId);
