using System.Net;
using System.Text;
using System.Text.Json;
using Aml.Core.Cases;
using Aml.Core.Pages;
using Aml.Core.Ports;
using Aml.Core.Screening;
using Aml.Infra.Persistence.Repositories;
using Aml.Infra.Web;
using Aml.Services;
using Dapper;

namespace Aml.Tests.Integration;

/// <summary>
/// Pages received by URL on SQLite (external-sources.md#received-url-source-2): the page record and its FETCH_PAGE job
/// queued together, the job run by the batch worker through the polite fetcher (pages from memory), transient problems
/// retried and the last attempt stored, a fresh page reused.
/// </summary>
public sealed class PageTests : ServiceTestBase
{
    private const string Url = "https://news.example.test/a/1";
    private const string Article = "<html><body><article><p>被告王大明（男，45歲）涉嫌詐欺，遭臺北地檢署提起公訴。</p></article></body></html>";

    private readonly PageStore _pages;
    private readonly ManualTimeProvider _clock = new(T0);
    private readonly Dictionary<string, (HttpStatusCode Status, string Type, byte[] Body)> _web = new(StringComparer.Ordinal);
    private readonly WebStub _stub;
    private readonly PoliteFetcher _fetcher;

    public PageTests()
    {
        _pages = new PageStore(_db);
        _stub = new WebStub(u => u.AbsolutePath == "/robots.txt" ? (HttpStatusCode.NotFound, "text/plain", [])
            : _web.TryGetValue(u.AbsoluteUri, out var reply) ? reply : (HttpStatusCode.NotFound, "text/html", []));
        _fetcher = new PoliteFetcher("test-agent", TimeSpan.Zero, WebStub.Dns(), _stub, _clock);
        Serve(Url, HttpStatusCode.OK, Article);
    }

    private void Serve(string url, HttpStatusCode status, string html) => _web[url] = (status, "text/html", Encoding.UTF8.GetBytes(html));

    private PageService Pages(params IStageReader[] stageReaders) =>
        new(_pages, (url, ct) => WebPages.ReadAsync(_fetcher, url, ct), _clock, stageReaders: stageReaders);

    private BatchService Worker(PageService pages, int maxAttempts = 3) =>
        new(_jobs, _caseService, Aml.Infra.Txt.TxtCaseReader.ReadFiles, new BatchOptions { MaxAttempts = maxAttempts, RetryDelay = TimeSpan.Zero }, _clock, _listService, pages);

    [Fact]
    public async Task A_received_url_is_queued_with_its_job_then_read_and_stored_with_its_persons()
    {
        var ct = TestContext.Current.CancellationToken;
        var pages = Pages();
        var (queued, isQueued) = await pages.SubmitAsync(Url, ct);
        Assert.True(isQueued);
        Assert.Equal((PageUrl.PageId(Url), Url, PageStatus.Pending), (queued.PageId, queued.Url, queued.Status));

        // The payload names the page and its URL, nothing else.
        await using (var c = await _db.OpenAsync(ct))
            Assert.Equal($"{{\"pageId\":\"{queued.PageId}\",\"url\":\"{Url}\"}}", await c.QuerySingleAsync<string>("SELECT payload FROM job WHERE job_id = @id", new { id = queued.JobId }));

        Assert.Equal(1, await Worker(pages).RunOnceAsync(ct));
        var page = (await pages.GetAsync(queued.PageId, ct))!;
        Assert.Equal((PageStatus.Read, (string?)null, Url, "text/html", T0), (page.Status, page.Problem, page.FinalUrl, page.MediaType, page.FetchedAt));
        Assert.Equal("被告王大明（男，45歲）涉嫌詐欺，遭臺北地檢署提起公訴。", page.Text);
        var wang = Assert.Single(page.Persons!);
        Assert.Equal(($"WEB:{queued.PageId}-1", "王大明", 45, LegalStage.Indicted), (wang.ProfileKey, wang.Name, wang.StatedAge, wang.Stage));
        // Stored and read back whole.
        Assert.Equal(JsonSerializer.Serialize(PageReadings.Read(queued.PageId, page.Text!, null, []), CaseJson.Options),
            JsonSerializer.Serialize(new PageReading(page.Persons!, page.OtherEvents!, page.Model), CaseJson.Options));

        var item = Assert.Single((await Worker(pages).GetAsync(queued.BatchId!, ct))!.Items);
        Assert.Equal((JobStatus.Succeeded, (string?)null, (int?)null), (item.Status, item.CaseId, item.AnalysisRevision));
    }

    [Theory]
    // T0 is 2026-11-04: a date after the fetch is invalid, so MISSING, as is none; never the fetch time.
    [InlineData("<meta property='article:published_time' content='2026-11-03T08:00:00+08:00'>", "2026-11-03", "METADATA")]
    [InlineData("<meta property='article:published_time' content='2026-11-09'>", null, "MISSING")]
    [InlineData("", null, "MISSING")]
    // No publication date stated: the update date stands in and is stored and read back as UPDATE_DATE.
    [InlineData("<meta property='article:modified_time' content='2026-11-02'>", "2026-11-02", "UPDATE_DATE")]
    public async Task A_page_s_publication_date_is_stored_with_its_source_and_one_after_the_fetch_is_missing(string head, string? on, string source)
    {
        var ct = TestContext.Current.CancellationToken;
        Serve(Url, HttpStatusCode.OK, Article.Replace("<body>", $"<head>{head}</head><body>", StringComparison.Ordinal));
        var pages = Pages();
        var (queued, _) = await pages.SubmitAsync(Url, ct);

        Assert.Equal(1, await Worker(pages).RunOnceAsync(ct));

        var page = (await pages.GetAsync(queued.PageId, ct))!;
        Assert.Equal((on, (PublicationSource?)CaseJson.ParseCode<PublicationSource>(source), T0), (page.PublishedOn?.ToString(), page.PublishedOnSource, page.FetchedAt));
    }

    [Fact]
    public async Task A_fresh_page_is_reused_a_page_queued_today_is_not_queued_twice_and_a_stale_one_is_fetched_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var pages = Pages();
        var first = (await pages.SubmitAsync(Url, ct)).Page;
        var again = await pages.SubmitAsync(Url, ct);
        Assert.Equal((first.JobId, true), (again.Page.JobId, again.Queued));

        await Worker(pages).RunOnceAsync(ct);
        _clock.Advance(PageService.MaxAge - TimeSpan.FromSeconds(1));
        var fresh = await pages.SubmitAsync(Url, ct);
        Assert.Equal((PageStatus.Read, false), (fresh.Page.Status, fresh.Queued));

        _clock.Advance(TimeSpan.FromSeconds(1));
        var stale = await pages.SubmitAsync(Url, ct);
        Assert.Equal((PageStatus.Pending, true), (stale.Page.Status, stale.Queued));
        Assert.NotEqual(first.JobId, stale.Page.JobId);
        await using var c = await _db.OpenAsync(ct);
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM job WHERE job_type = 'FETCH_PAGE'"));
    }

    [Fact]
    public async Task A_transient_problem_is_retried_and_the_last_attempt_stores_the_page_failed()
    {
        var ct = TestContext.Current.CancellationToken;
        Serve(Url, HttpStatusCode.ServiceUnavailable, "");
        var pages = Pages();
        var queued = (await pages.SubmitAsync(Url, ct)).Page;
        var worker = Worker(pages, maxAttempts: 2);

        await worker.RunOnceAsync(ct);
        var item = Assert.Single((await worker.GetAsync(queued.BatchId!, ct))!.Items);
        Assert.Equal((JobStatus.Pending, 1, "HTTP_STATUS:503"), (item.Status, item.AttemptCount, item.ErrorCode));
        Assert.Equal(PageStatus.Pending, (await pages.GetAsync(queued.PageId, ct))!.Status);

        await worker.RunOnceAsync(ct);
        var page = (await pages.GetAsync(queued.PageId, ct))!;
        Assert.Equal((PageStatus.Failed, "HTTP_STATUS:503", (IReadOnlyList<PagePerson>?)null), (page.Status, page.Problem, page.Persons));
        Assert.Equal(JobStatus.Succeeded, Assert.Single((await worker.GetAsync(queued.BatchId!, ct))!.Items).Status);
    }

    [Fact]
    public async Task A_permanent_problem_refuses_the_page_at_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var pages = Pages();
        var queued = (await pages.SubmitAsync("https://news.example.test/redirect", ct)).Page;
        _web["https://news.example.test/redirect"] = (HttpStatusCode.Found, "location:http://169.254.169.254/latest/meta-data/", []);

        await Worker(pages).RunOnceAsync(ct);
        var page = (await pages.GetAsync(queued.PageId, ct))!;
        Assert.Equal((PageStatus.Refused, "URL_NOT_ALLOWED", (string?)null), (page.Status, page.Problem, page.Text));
        Assert.DoesNotContain(_stub.RequestedUrls, u => u.Host == "169.254.169.254");
        var item = Assert.Single((await Worker(pages).GetAsync(queued.BatchId!, ct))!.Items);
        Assert.Equal((JobStatus.Succeeded, 1), (item.Status, item.AttemptCount));
    }

    [Fact]
    public async Task A_missing_stage_answer_is_retried_and_the_last_attempt_stores_the_reading_with_the_indicator()
    {
        var ct = TestContext.Current.CancellationToken;
        var pages = Pages(new Answer("classifier", LegalStage.Indicted), new Answer("llm", null));
        var queued = (await pages.SubmitAsync(Url, ct)).Page;
        var worker = Worker(pages, maxAttempts: 2);

        await worker.RunOnceAsync(ct);
        Assert.Equal("STAGE_NO_ANSWER", Assert.Single((await worker.GetAsync(queued.BatchId!, ct))!.Items).ErrorCode);
        Assert.Equal(PageStatus.Pending, (await pages.GetAsync(queued.PageId, ct))!.Status);

        await worker.RunOnceAsync(ct);
        var wang = Assert.Single((await pages.GetAsync(queued.PageId, ct))!.Persons!);
        Assert.Equal((LegalStage.Unknown, "STAGE_READER_NO_ANSWER"), (wang.Stage, Assert.Single(wang.StageReasonCodes)));
    }

    [Fact]
    public async Task An_outcome_of_a_job_the_page_is_no_longer_queued_under_is_not_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        var queued = (await Pages().SubmitAsync(Url, ct)).Page;
        Assert.False(await _pages.SaveAsync(queued with { JobId = "another", Status = PageStatus.Refused, Problem = "ROBOTS_DISALLOWED" }, ct));
        Assert.True(await _pages.SaveAsync(queued with { Status = PageStatus.Refused, Problem = "ROBOTS_DISALLOWED", FetchedAt = T0 }, ct));
        var page = (await _pages.GetAsync(queued.PageId, ct))!;
        Assert.Equal((PageStatus.Refused, "ROBOTS_DISALLOWED"), (page.Status, page.Problem));
    }

    /// <summary>A job dead-lettered on its last attempt (timeout, exception) stores its page FAILED with the job's error code, never PENDING.</summary>
    [Theory]
    [InlineData(true, "TIMEOUT")]
    [InlineData(false, "EXCEPTION:InvalidOperationException")]
    public async Task A_page_whose_job_dies_on_its_last_attempt_is_stored_failed_with_the_job_error(bool timesOut, string code)
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new BatchOptions { MaxAttempts = 1, RetryDelay = TimeSpan.Zero };
        var pages = new PageService(_pages, (_, token) =>
        {
            if (!timesOut) throw new InvalidOperationException("reader broke");
            _clock.Advance(options.Timeout);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new PageFetch(null, null, null, null, null));
        }, _clock);
        var queued = (await pages.SubmitAsync(Url, ct)).Page;

        await new BatchService(_jobs, _caseService, Aml.Infra.Txt.TxtCaseReader.ReadFiles, options, _clock, _listService, pages).RunOnceAsync(ct);
        var item = Assert.Single(await _jobs.ListItemsAsync(queued.BatchId!, ct));
        Assert.Equal((JobStatus.DeadLetter, code), (item.Status, item.ErrorCode));
        var page = (await pages.GetAsync(queued.PageId, ct))!;
        Assert.Equal((PageStatus.Failed, code, queued.JobId, _clock.GetUtcNow()), (page.Status, page.Problem, page.JobId, page.FetchedAt));
    }

    [Fact]
    public async Task A_fetch_page_job_whose_payload_cannot_be_read_is_dead_lettered()
    {
        var ct = TestContext.Current.CancellationToken;
        await _jobs.EnqueueAsync("j1", PageService.FetchPage, "b1", "k1", "{\"pageId\":null}", T0, ct);
        await Worker(Pages()).RunOnceAsync(ct);
        var item = Assert.Single(await _jobs.ListItemsAsync("b1", ct));
        Assert.Equal((JobStatus.DeadLetter, "INVALID_PAYLOAD"), (item.Status, item.ErrorCode));
    }

    private sealed class Answer(string name, LegalStage? stage) : IStageReader
    {
        public string Name => name;

        public StageAnswer? Read(string text, IReadOnlyList<string> names) => stage is { } s ? new StageAnswer(s) : null;
    }
}
