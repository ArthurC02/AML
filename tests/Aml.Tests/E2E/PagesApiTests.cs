using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Aml.Infra.Web;
using Aml.Tests.Synthetic;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aml.Tests.E2E;

/// <summary>
/// A received URL through the API (external-sources.md#received-url-source-2): POST /api/pages queues the fetch, the job
/// worker reads the page, GET /api/pages/{pageId} returns its persons. The web is served from memory: the host's fetcher is
/// replaced by one over a stub handler and a host table.
/// </summary>
public sealed class PagesApiTests(ApiHost api) : IClassFixture<ApiHost>
{
    private const string Page = "<html><body><article><h1>詐欺案起訴</h1><p>被告王大明（男，45歲）涉嫌詐欺，遭臺北地檢署提起公訴。</p><p>證人李小華到庭。</p></article></body></html>";

    [Fact]
    public async Task A_received_url_is_fetched_by_a_job_and_its_persons_are_returned_once_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var stub = new WebStub(u => u.AbsolutePath == "/robots.txt" ? (HttpStatusCode.NotFound, "text/plain", []) : (HttpStatusCode.OK, "text/html", Encoding.UTF8.GetBytes(Page)));
        await using var host = Offline(stub);
        using var client = host.CreateClient();

        var posted = await client.PostAsJsonAsync("/api/pages", new { url = "https://News.Example.test/case/7?utm_source=feed#top" }, ct);
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        var queued = JsonNode.Parse(await posted.Content.ReadAsStringAsync(ct))!;
        var pageId = (string)queued["pageId"]!;
        Assert.Equal(("https://news.example.test/case/7", "PENDING", $"/api/pages/{pageId}"), ((string)queued["url"]!, (string)queued["status"]!, posted.Headers.Location!.ToString()));
        Assert.NotNull((string?)queued["jobId"]);

        var full = await Wait.UntilAsync(async () => JsonNode.Parse(await client.GetStringAsync($"/api/pages/{pageId}?view=full", ct))!,
            p => (string?)p["status"] != "PENDING", "the page to be read", ct);
        Assert.Equal(("READ", "詐欺案起訴"), ((string)full["status"]!, (string)full["title"]!));
        var wang = full["persons"]![0]!;
        Assert.Equal(($"WEB:{pageId}-1", "王大明", "INDICTED", 45, "MALE"),
            ((string)wang["profileKey"]!, (string)wang["name"]!, (string)wang["stage"]!, (int)wang["statedAge"]!, (string)wang["statedGender"]!));
        Assert.Equal(["王大明", "李小華"], full["persons"]!.AsArray().Select(p => (string)p!["name"]!));
        Assert.Equal(["/robots.txt", "/case/7"], stub.Requested);

        // The summary (default) leaves out the text and what is stated next to a name; a second POST the same day gets it as stored.
        var summary = JsonNode.Parse(await client.GetStringAsync($"/api/pages/{pageId}", ct))!;
        Assert.Null(summary["text"]);
        Assert.Equal((0, (JsonNode?)null), (summary["persons"]![0]!["identifiers"]!.AsArray().Count, summary["persons"]![0]!["statedAge"]));
        var again = await client.PostAsJsonAsync("/api/pages", new { url = "https://news.example.test/case/7" }, ct);
        Assert.Equal((HttpStatusCode.OK, "READ"), (again.StatusCode, (string?)JsonNode.Parse(await again.Content.ReadAsStringAsync(ct))!["status"]));
        Assert.Equal(2, stub.Requested.Count);
    }

    [Theory]
    [InlineData("https://user:secret@news.example.test/a", "a user name or password in the URL")]
    [InlineData("https://news.example.test:8443/a", "a port other than 80 or 443")]
    [InlineData("file:///etc/passwd", "not http or https")]
    [InlineData("/relative/path", "not an absolute URL")]
    public async Task A_url_that_cannot_be_fetched_is_an_invalid_request_and_an_unknown_page_is_not_found(string url, string rule)
    {
        var ct = TestContext.Current.CancellationToken;
        var stub = new WebStub(_ => (HttpStatusCode.OK, "text/html", Encoding.UTF8.GetBytes(Page)));
        await using var host = Offline(stub);
        using var client = host.CreateClient();
        var response = await client.PostAsJsonAsync("/api/pages", new { url }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        Assert.Equal(("/api/codes/INVALID_REQUEST", "url", rule), ((string)problem["type"]!, (string)problem["errors"]![0]!["parameter"]!, (string)problem["errors"]![0]!["rule"]!));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/pages/0000000000000000", ct)).StatusCode);
        Assert.Empty(stub.Requested);
    }

    [Fact]
    public async Task A_case_naming_a_page_waits_for_it_to_be_read_then_has_the_customer_on_it_as_a_hit()
    {
        var ct = TestContext.Current.CancellationToken;
        var customer = SyntheticCaseGenerator.Generate()[0];
        var name = customer.Subject.Names[0];
        var article = $"<html><body><article><p>〔本報記者／臺北報導〕被告{name}涉嫌詐欺，遭臺北地檢署提起公訴。</p><p>證人陳志明到庭。</p></article></body></html>";
        using var gate = new ManualResetEventSlim();
        var stub = new WebStub(u =>
        {
            if (u.AbsolutePath == "/robots.txt") return (HttpStatusCode.NotFound, "text/plain", []);
            gate.Wait(TimeSpan.FromSeconds(30)); // the page stays PENDING until the test lets the fetch answer
            return (HttpStatusCode.OK, "text/html", Encoding.UTF8.GetBytes(article));
        });
        await using var host = Offline(stub);
        using var client = host.CreateClient();
        var pageId = (string)JsonNode.Parse(await (await client.PostAsJsonAsync("/api/pages", new { url = "https://news.example.test/case/9" }, ct)).Content.ReadAsStringAsync(ct))!["pageId"]!;
        object Item(string trigger, params string[] pageIds) => new { subjectRef = "s-page", triggerEventId = trigger, files = customer.Files, pageIds };

        var early = await client.PostAsJsonAsync("/api/cases", Item("e-1", pageId), ct);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        var notReady = JsonNode.Parse(await early.Content.ReadAsStringAsync(ct))!;
        Assert.Equal(("/api/codes/PAGE_NOT_READY", pageId, "PENDING"), ((string)notReady["type"]!, (string)notReady["errors"]![0]!["pageId"]!, (string)notReady["errors"]![0]!["status"]!));

        gate.Set();
        await Wait.UntilAsync(async () => JsonNode.Parse(await client.GetStringAsync($"/api/pages/{pageId}", ct))!, p => (string?)p["status"] == "READ", "the page to be read", ct);
        var created = await client.PostAsJsonAsync("/api/cases", Item("e-1", pageId), ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var detail = JsonNode.Parse(await client.GetStringAsync(created.Headers.Location!.ToString(), ct))!["effective"]!;
        var hit = detail["result"]!["hits"]!.AsArray().Single(h => ((string)h!["profile"]!["profileKey"]!).StartsWith("WEB:", StringComparison.Ordinal))!;
        Assert.Equal(($"WEB:{pageId}-1", "ADVERSE_MEDIA", "PAGE_READING", "INDICTED", "REVIEW"), ((string)hit["profile"]!["profileKey"]!, (string)hit["conditions"]![0]!["type"]!,
            (string)hit["conditions"]![0]!["basis"]!, (string)hit["conditions"]![0]!["legalStage"]!, (string)hit["pairResult"]!));
        Assert.Equal([("SOURCE_PAGE", true, name), ("SOURCE_PAGE", false, "陳志明")],
            detail["result"]!["pages"]![0]!["persons"]!.AsArray().Select(p => ((string)p!["mode"]!, (bool)p["candidate"]!, (string)p["name"]!)));
        Assert.Equal(pageId, (string)detail["lineage"]!["pages"]![0]!["pageId"]!);

        // A re-analysis upload names the page again as a repeated form field.
        using var form = new MultipartFormDataContent();
        foreach (var (file, text) in customer.Files) form.Add(new StringContent(text), "files", file);
        form.Add(new StringContent(pageId), "pageIds");
        var caseId = (string)JsonNode.Parse(await created.Content.ReadAsStringAsync(ct))!["caseId"]!;
        var reanalysed = await client.PostAsync($"/api/cases/{caseId}/analyses", form, ct);
        Assert.Equal((HttpStatusCode.OK, 2), (reanalysed.StatusCode, (int)JsonNode.Parse(await reanalysed.Content.ReadAsStringAsync(ct))!["currentRevision"]!));
        Assert.Equal(pageId, (string)JsonNode.Parse(await client.GetStringAsync($"/api/cases/{caseId}/revisions/2", ct))!["lineage"]!["pages"]![0]!["pageId"]!);

        // A batch item names the page the same way; its job carries the page as submitted.
        var batchId = (string)JsonNode.Parse(await (await client.PostAsJsonAsync("/api/batches", new[] { Item("e-3", pageId) }, ct)).Content.ReadAsStringAsync(ct))!["batchId"]!;
        var job = await Wait.UntilAsync(async () => JsonNode.Parse(await client.GetStringAsync($"/api/batches/{batchId}?view=full", ct))!["items"]![0]!,
            i => (string?)i["status"] == "SUCCEEDED", "the batch item to succeed", ct);
        var batched = JsonNode.Parse(await client.GetStringAsync($"/api/cases/{(string)job["caseId"]!}", ct))!["effective"]!["result"]!["hits"]!.AsArray();
        Assert.Contains(batched, h => (string?)h!["profile"]!["profileKey"] == $"WEB:{pageId}-1");

        var unknown = await client.PostAsJsonAsync("/api/cases", Item("e-2", "0000000000000000"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var unusable = JsonNode.Parse(await unknown.Content.ReadAsStringAsync(ct))!;
        Assert.Equal(("/api/codes/PAGE_UNUSABLE", "NOT_FOUND"), ((string)unusable["type"]!, (string)unusable["errors"]![0]!["status"]!));
    }

    // Every host of this class fetches from the stub: the shared host's own worker, with the system DNS, is never started.
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Offline(WebStub stub) =>
        api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(new PoliteFetcher("test-agent", TimeSpan.Zero, WebStub.Dns(), stub))));
}
