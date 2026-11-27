using System.Globalization;
using System.Text.Json;
using Aml.Core.Cases;
using Aml.Core.Pages;
using Aml.Core.Ports;

namespace Aml.Services;

/// <summary>The FETCH_PAGE job's payload: the page and its canonical URL, never customer data.</summary>
public sealed record PageJob(string PageId, string Url);

/// <summary>A page a Case names that cannot be used: NOT_FOUND, or REFUSED / FAILED with the page's problem.</summary>
public sealed record UnusablePage(string PageId, string Status, string? Problem);

/// <summary>The snapshot files of the READ pages a Case names (name → content), and the pages that stop it.</summary>
public sealed record PinnedPages(IReadOnlyDictionary<string, string> Files, IReadOnlyList<UnusablePage> Unusable, IReadOnlyList<string> NotReady);

/// <summary>
/// Pages received by URL (external-sources.md#received-url-source-2): a page read within <see cref="MaxAge"/> is returned
/// as stored; otherwise a FETCH_PAGE job fetches and reads it. The job retries a transient problem and a missing stage
/// answer while attempts are left; the last attempt stores the outcome (FAILED, or the reading with the missing indicator).
/// </summary>
/// <param name="fetch">The page at a canonical URL as text, through the URL guard (Infra WebPages.ReadAsync).</param>
public sealed class PageService(IPageStore store, Func<Uri, CancellationToken, Task<PageFetch>> fetch, TimeProvider clock,
    INewsModel? newsModel = null, IEnumerable<IStageReader>? stageReaders = null, Func<IStageReader, IStageReader>? observeStageReader = null)
{
    public const string FetchPage = "FETCH_PAGE";

    /// <summary>How long a stored outcome (READ, REFUSED or FAILED) is reused; the job key holds the date, so a page is fetched at most once a day.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    private readonly IStageReader[] stageModels = [.. stageReaders ?? []];

    /// <summary>The page as stored when its outcome is fresh (Queued false); else the page queued, or already queued today, for a fetch.</summary>
    public async Task<(PageRecord Page, bool Queued)> SubmitAsync(string canonicalUrl, CancellationToken ct)
    {
        var pageId = PageUrl.PageId(canonicalUrl);
        var now = clock.GetUtcNow();
        var stored = await store.GetAsync(pageId, ct).ConfigureAwait(false);
        if (stored is { Status: not PageStatus.Pending, FetchedAt: { } at } && now - at < MaxAge) return (stored, false);
        var day = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var job = new NewJob(Guid.CreateVersion7().ToString(), FetchPage, Guid.CreateVersion7().ToString(), $"{FetchPage}:{pageId}:{day}",
            JsonSerializer.Serialize(new PageJob(pageId, canonicalUrl), CaseJson.Options));
        return (await store.QueueAsync(new PageRecord(pageId, canonicalUrl, PageStatus.Pending, now, job.JobId, job.BatchId), job, ct).ConfigureAwait(false), true);
    }

    public Task<PageRecord?> GetAsync(string pageId, CancellationToken ct) => store.GetAsync(pageId, ct);

    /// <summary>
    /// The pages a Case names, each READ page as its snapshot file for the Case's input (the version the revision uses).
    /// A page unknown, REFUSED or FAILED is unusable; one still PENDING is not ready. Nothing of the Case reaches the page record.
    /// </summary>
    public async Task<PinnedPages> PinAsync(IEnumerable<string> pageIds, CancellationToken ct)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var unusable = new List<UnusablePage>();
        var notReady = new List<string>();
        foreach (var id in pageIds.Distinct(StringComparer.Ordinal))
            switch (await store.GetAsync(id, ct).ConfigureAwait(false))
            {
                case null: unusable.Add(new(id, NotFound, null)); break;
                case { Status: PageStatus.Pending }: notReady.Add(id); break;
                case { Status: PageStatus.Read } page: files[PageSnapshots.FileName(id)] = PageSnapshots.Write(page); break;
                case var page: unusable.Add(new(id, CaseJson.Code(page.Status), page.Problem)); break;
            }
        return new(files, unusable, notReady);
    }

    private const string NotFound = "NOT_FOUND";

    /// <summary>Runs one FETCH_PAGE job: fetch, read, store. Throws <see cref="PageFetchRetryException"/> or
    /// <see cref="StageAnswerMissingException"/> to be retried, unless this is the last attempt.</summary>
    public async Task FetchAsync(PageJob job, string jobId, bool lastAttempt, Func<Task> beforeWrite, CancellationToken ct)
    {
        var fetched = await fetch(new Uri(job.Url), ct).ConfigureAwait(false);
        if (fetched.Transient && !lastAttempt) throw new PageFetchRetryException(fetched.Problem!);
        // A budgeted reader (the language model) gets a fresh budget for this page, as for one analysis of a Case.
        var readers = stageModels.Select(r => r is BudgetedStageReader b ? b.ForAnalysis(clock) : r)
            .Select(r => observeStageReader is null ? r : observeStageReader(r)).ToList();
        var reading = fetched.Text is { } text ? PageReadings.Read(job.PageId, text, newsModel, readers) : null;
        if (reading is not null && !lastAttempt && PageReadings.StageAnswerMissing(reading)) throw new StageAnswerMissingException();
        var stored = await store.GetAsync(job.PageId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"Page {job.PageId} is not stored");
        var fetchedAt = clock.GetUtcNow();
        var (publishedOn, publishedFrom) = PageReadings.Published(fetched, fetchedAt);
        var outcome = stored with
        {
            JobId = jobId,
            Status = reading is not null ? PageStatus.Read : fetched.Transient ? PageStatus.Failed : PageStatus.Refused,
            Problem = fetched.Problem,
            FinalUrl = fetched.FinalUrl,
            MediaType = fetched.MediaType,
            Title = fetched.Title,
            FetchedAt = fetchedAt,
            ContentHash = fetched.ContentHash,
            PublishedOn = reading is null ? null : publishedOn,
            PublishedOnSource = reading is null ? null : publishedFrom,
            Text = fetched.Text,
            Persons = reading?.Persons,
            OtherEvents = reading?.OtherEvents,
            Model = reading?.Model,
        };
        ct.ThrowIfCancellationRequested();
        await beforeWrite().ConfigureAwait(false);
        // False: the page was queued again under another job since; that job stores its own outcome.
        await store.SaveAsync(outcome, ct).ConfigureAwait(false);
    }

    /// <summary>The job was dead-lettered (TIMEOUT, EXCEPTION:&lt;type&gt;): the page is stored FAILED with that code, never left PENDING.</summary>
    public async Task FailAsync(PageJob job, string jobId, string errorCode)
    {
        // No token: the job's outcome is already recorded, and a stopping host still records the page's.
        if (await store.GetAsync(job.PageId, CancellationToken.None).ConfigureAwait(false) is not { } stored) return;
        await store.SaveAsync(new PageRecord(stored.PageId, stored.Url, PageStatus.Failed, stored.RequestedAt, jobId, stored.BatchId, errorCode, FetchedAt: clock.GetUtcNow()),
            CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>A transient page problem (5xx, 429, robots.txt unavailable, unreachable) with attempts left; the message is the problem code, the job's error code.</summary>
public sealed class PageFetchRetryException : Exception
{
    public PageFetchRetryException() : base(PageProblems.Unreachable) { }

    public PageFetchRetryException(string problem) : base(problem) { }

    public PageFetchRetryException(string message, Exception inner) : base(message, inner) { }
}
