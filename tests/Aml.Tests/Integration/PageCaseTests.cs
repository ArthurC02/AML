using Aml.Core.Cases;
using Aml.Core.Pages;
using Aml.Core.Retrieval;
using Aml.Core.Screening;
using Aml.Infra.Persistence.Repositories;
using Aml.Infra.Txt;
using Aml.Services;
using Aml.Tests.Synthetic;
using Dapper;

namespace Aml.Tests.Integration;

/// <summary>
/// A Case naming a received page, on SQLite (external-sources.md#received-url-source-2): the page pinned into the Case's
/// input when it is READ, its matching person a hit, the version used kept so a re-analysis of the stored input reads the
/// same text, and a page fetched again used only when an analysis names it again. The page record holds nothing of the Case.
/// </summary>
public sealed class PageCaseTests : ServiceTestBase
{
    private const string Url = "https://news.example.test/a/7";

    private readonly ManualTimeProvider _clock = new(T0);
    private readonly PageStore _pages;
    private readonly PageService _pageService;
    private readonly SyntheticCase _case = SyntheticCaseGenerator.Generate()[0];
    private string _served = "";

    public PageCaseTests()
    {
        _pages = new PageStore(_db);
        _pageService = new PageService(_pages, (_, _) => Task.FromResult(_served.Length == 0
            ? new PageFetch(Url, "text/html", null, null, null, PageProblems.RobotsDisallowed)
            : new PageFetch(Url, "text/html", PageSnapshots.TextHash(_served), "詐欺案", _served)), _clock);
    }

    private string Customer => _case.Subject.Names[0];

    private async Task<PageRecord> FetchAsync(string text, CancellationToken ct)
    {
        _served = text;
        var (queued, _) = await _pageService.SubmitAsync(Url, ct);
        await _pageService.FetchAsync(new PageJob(queued.PageId, Url), queued.JobId!, lastAttempt: true, () => Task.CompletedTask, ct);
        return (await _pageService.GetAsync(queued.PageId, ct))!;
    }

    private static Dictionary<string, string> With(IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, string> pages) =>
        files.Where(f => !PageSnapshots.IsFile(f.Key)).Concat(pages).ToDictionary(f => f.Key, f => f.Value);

    [Fact]
    public async Task A_case_naming_a_read_page_has_its_matching_person_as_a_hit_and_a_reanalysis_reads_the_page_version_it_used()
    {
        var ct = TestContext.Current.CancellationToken;
        var (queued, _) = await _pageService.SubmitAsync(Url, ct);
        Assert.Equal([queued.PageId], (await _pageService.PinAsync([queued.PageId], ct)).NotReady);

        var first = $"〔本報記者／臺北報導〕被告{Customer}涉嫌詐欺，遭臺北地檢署提起公訴。證人張文華到庭作證。";
        var page = await FetchAsync(first, ct);
        var pinned = await _pageService.PinAsync([page.PageId, page.PageId], ct);
        Assert.Equal((PageSnapshots.FileName(page.PageId), 0, 0), (Assert.Single(pinned.Files.Keys), pinned.Unusable.Count, pinned.NotReady.Count));
        var files = With(_case.Files, pinned.Files);
        var c = await _caseService.CreateAsync("s-page", "e-page", TxtCaseReader.ReadFiles(files), ct, files);

        AnalysisRecord Revision(int n) => _cases.GetAnalysisAsync(c.CaseId, n, ct).GetAwaiter().GetResult()!;
        (string Key, string Stage, string Result, string TextHash) PageHit(int n)
        {
            var r = Revision(n);
            var hit = r.Result.Hits.Single(h => ProfileKeys.IsWeb(h.Profile.ProfileKey));
            return (hit.Profile.ProfileKey, CaseJson.Code(hit.Conditions[0].LegalStage), CaseJson.Code(hit.PairResult), Assert.Single(r.Lineage!.Pages!).TextHash);
        }
        Assert.Equal(($"WEB:{page.PageId}-1", "INDICTED", "REVIEW", PageSnapshots.TextHash(first)), PageHit(1));
        Assert.Equal([(Customer, true), ("張文華", false)], Assert.Single(Revision(1).Result.Pages!).Persons.Select(p => (p.Name, p.Candidate)));
        Assert.Contains(Revision(1).Lineage!.InputFiles, f => f.Name == PageSnapshots.FileName(page.PageId));

        // Fetched again two days later with another text: a re-analysis of the stored input (as a changed list Profile queues) reads the first.
        _clock.Advance(TimeSpan.FromDays(2));
        var second = $"〔本報記者／臺北報導〕被告{Customer}詐欺案，臺北地方法院今日宣判，判處{Customer}有期徒刑一年。";
        Assert.Equal(second, (await FetchAsync(second, ct)).Text);
        var stored = (await _cases.LatestInputAsync(c.CaseId, ct))!;
        await _caseService.ReanalyzeAsync(c.CaseId, TxtCaseReader.ReadFiles(stored), ct, stored);
        Assert.Equal(($"WEB:{page.PageId}-1", "INDICTED", "REVIEW", PageSnapshots.TextHash(first)), PageHit(2));

        // An analysis naming the page again pins the version stored now.
        var again = With(stored, (await _pageService.PinAsync([page.PageId], ct)).Files);
        await _caseService.ReanalyzeAsync(c.CaseId, TxtCaseReader.ReadFiles(again), ct, again);
        Assert.Equal(($"WEB:{page.PageId}-1", "CONVICTED", "REVIEW", PageSnapshots.TextHash(second)), PageHit(3));

        // The page record holds nothing of the Case: no Case id, subjectRef or trigger in its row.
        await using var conn = await _db.OpenAsync(ct);
        var row = string.Join("|", (await conn.QuerySingleAsync("SELECT * FROM web_page WHERE page_id = @id", new { id = page.PageId }) as IDictionary<string, object>)!.Values);
        Assert.DoesNotContain(c.CaseId, row, StringComparison.Ordinal);
        Assert.DoesNotContain("s-page", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_unknown_refused_or_still_pending_is_not_pinned_and_says_why()
    {
        var ct = TestContext.Current.CancellationToken;
        var refused = await FetchAsync("", ct);
        var (pending, _) = await _pageService.SubmitAsync("https://news.example.test/b/8", ct);

        var pinned = await _pageService.PinAsync(["0000000000000000", refused.PageId, pending.PageId], ct);

        Assert.Empty(pinned.Files);
        Assert.Equal([new UnusablePage("0000000000000000", "NOT_FOUND", null), new UnusablePage(refused.PageId, "REFUSED", PageProblems.RobotsDisallowed)], pinned.Unusable);
        Assert.Equal([pending.PageId], pinned.NotReady);
    }

    [Fact]
    public async Task A_batch_item_carrying_a_pinned_page_creates_its_case_with_the_page_person_as_a_hit()
    {
        var ct = TestContext.Current.CancellationToken;
        var page = await FetchAsync($"〔本報記者／臺北報導〕被害人{Customer}報案後，警方約談詐欺集團成員張文華，依詐欺罪嫌移送地檢署偵辦。", ct);
        var files = With(_case.Files, (await _pageService.PinAsync([page.PageId], ct)).Files);
        var batch = Batch();
        var submitted = await batch.SubmitAsync([new BatchItemRequest("s-batch", "e-batch", files, [page.PageId])], ct);

        Assert.Equal(2, await batch.RunOnceAsync(ct)); // this item, and the page's own FETCH_PAGE job, which was run by hand above

        var item = Assert.Single((await batch.GetAsync(submitted.BatchId, ct))!.Items);
        var hit = (await _cases.GetAnalysisAsync(item.CaseId!, 1, ct))!.Result.Hits.Single(h => ProfileKeys.IsWeb(h.Profile.ProfileKey));
        // The customer is the victim on this page: the rules read 被害人, so the condition does not hold.
        Assert.Equal((ConditionStatus.NotHit, ScreeningAssessment.NotApplicable), (hit.Conditions[0].Status, hit.PairResult));
    }
}
