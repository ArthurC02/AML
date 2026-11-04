using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Aml.Tests.E2E;

public sealed class CasesApiTests(ApiHost api) : IClassFixture<ApiHost>
{

    private static MultipartFormDataContent Upload(string gc, string trigger)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("subject-" + gc), "subjectRef" },
            { new StringContent(trigger), "triggerEventId" },
        };
        foreach (var path in Directory.GetFiles(GoldenCases.GoldenCaseTests.Folder(gc), "*.txt"))
            form.Add(new ByteArrayContent(File.ReadAllBytes(path)), "files", Path.GetFileName(path));
        return form;
    }

    [Theory]
    [InlineData("GC-11", "IN_REVIEW", "PROBABLE_MATCH")]
    [InlineData("GC-12", "IN_REVIEW", "REVIEW")]
    [InlineData("GC-01", "CLOSED", "NO_MATCH")]
    public async Task Upload_creates_case_and_stores_analysis(string gc, string status, string identityDecision)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = api.CreateClient();

        using var created = await client.PostAsync("/api/cases", Upload(gc, "evt-" + gc), ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var c = await created.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(status, c.GetProperty("status").GetString());
        Assert.Equal(1, c.GetProperty("currentRevision").GetInt32());

        var caseId = c.GetProperty("caseId").GetString();
        var analysis = await client.GetFromJsonAsync<JsonElement>($"/api/cases/{caseId}/analyses/1", ct);
        var hit = analysis.GetProperty("hits")[0];
        Assert.Equal(identityDecision, hit.GetProperty("identity").GetProperty("decision").GetString());

        var explanation = await client.GetFromJsonAsync<JsonElement>($"/api/cases/{caseId}/explanation", ct);
        var first = explanation.GetProperty("hits")[0];
        Assert.Equal(9, first.GetProperty("comparison").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(first.GetProperty("summary").GetProperty("conclusion").GetString()));

        // Same trigger and subject: the existing Case comes back, no new revision.
        using var repeated = await client.PostAsync("/api/cases", Upload(gc, "evt-" + gc), ct);
        var again = await repeated.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(caseId, again.GetProperty("caseId").GetString());
        Assert.Equal(1, again.GetProperty("currentRevision").GetInt32());
    }

    private static MultipartFormDataContent Files(string gc, Func<string, string, string>? edit = null)
    {
        var form = new MultipartFormDataContent();
        foreach (var path in Directory.GetFiles(GoldenCases.GoldenCaseTests.Folder(gc), "*.txt"))
        {
            var name = Path.GetFileName(path);
            var text = File.ReadAllText(path);
            form.Add(new StringContent(edit?.Invoke(name, text) ?? text), "files", name);
        }
        return form;
    }

    /// <summary>GC-08 capability: a source update creates a new revision whose changes are listed; an unchanged re-run lists none.</summary>
    [Fact]
    public async Task Reanalysis_lists_changes_from_the_previous_revision()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = api.CreateClient();
        using var created = await client.PostAsync("/api/cases", Upload("GC-11", "evt-reanalysis"), ct);
        var caseId = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("caseId").GetString();

        // Unchanged inputs: revision 2, no changes.
        using var same = await client.PostAsync($"/api/cases/{caseId}/analyses", Files("GC-11"), ct);
        Assert.Equal(2, (await same.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("currentRevision").GetInt32());
        var stable = await client.GetFromJsonAsync<JsonElement>($"/api/cases/{caseId}/explanation", ct);
        Assert.Equal(0, stable.GetProperty("changesFromPreviousRevision").GetArrayLength());

        // Source update: the Profile's birth years become 1960 only → DOB conflict, identity PROBABLE_MATCH → REVIEW.
        using var updated = await client.PostAsync($"/api/cases/{caseId}/analyses", Files("GC-11", (name, text) =>
            name.StartsWith("名單資料", StringComparison.Ordinal)
                ? text.Replace("1	Date of Birth	1973", "1	Date of Birth	1960", StringComparison.Ordinal)
                      .Replace("2	Date of Birth	1972", "2	Date of Birth	1960", StringComparison.Ordinal)
                : text), ct);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var changed = await client.GetFromJsonAsync<JsonElement>($"/api/cases/{caseId}/explanation", ct);
        var changes = changed.GetProperty("changesFromPreviousRevision").EnumerateArray().ToList();
        Assert.Contains(changes, c => c.GetProperty("kind").GetString() == "IDENTITY_CHANGED"
            && c.GetProperty("from").GetString() == "PROBABLE_MATCH" && c.GetProperty("to").GetString() == "REVIEW");

        // Revision 2 is kept unchanged as history.
        var rev2 = await client.GetFromJsonAsync<JsonElement>($"/api/cases/{caseId}/analyses/2", ct);
        Assert.Equal("PROBABLE_MATCH", rev2.GetProperty("hits")[0].GetProperty("identity").GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Rejects_non_txt_and_incomplete_uploads()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = api.CreateClient();

        using var exe = new MultipartFormDataContent
        {
            { new StringContent("s"), "subjectRef" }, { new StringContent("t"), "triggerEventId" },
            { new ByteArrayContent([1, 2, 3]), "files", "evil.exe" },
        };
        using var refused = await client.PostAsync("/api/cases", exe, ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("Only .txt, .html or .htm files up to 5 MB are accepted", await refused.Content.ReadFromJsonAsync<string>(ct));

        using var incomplete = new MultipartFormDataContent
        {
            { new StringContent("s"), "subjectRef" }, { new StringContent("t"), "triggerEventId" },
            { new StringContent("x"), "files", "notes.txt" },
        };
        using var unreadable = await client.PostAsync("/api/cases", incomplete, ct);
        Assert.Equal(HttpStatusCode.BadRequest, unreadable.StatusCode);
        Assert.Equal("\"Input files are incomplete or not in the expected .txt layout\"", await unreadable.Content.ReadAsStringAsync(ct));
    }

    [Theory]
    [InlineData("GET", "/api/cases/unknown", "none", HttpStatusCode.NotFound)]
    [InlineData("GET", "/api/cases/unknown/analyses/1", "none", HttpStatusCode.NotFound)]
    [InlineData("GET", "/api/cases/unknown/explanation", "none", HttpStatusCode.NotFound)]
    [InlineData("POST", "/api/cases/unknown/analyses", "case-files", HttpStatusCode.NotFound)]
    [InlineData("GET", "/api/batches/unknown", "none", HttpStatusCode.NotFound)]
    [InlineData("POST", "/api/batches/unknown/rerun", "none", HttpStatusCode.NotFound)]
    [InlineData("GET", "/api/lists/profiles/unknown/versions", "none", HttpStatusCode.NotFound)]
    [InlineData("POST", "/api/cases", "no-files", HttpStatusCode.BadRequest)]
    [InlineData("POST", "/api/cases", "oversize", HttpStatusCode.BadRequest)]
    [InlineData("POST", "/api/batches", "[]", HttpStatusCode.BadRequest)]
    [InlineData("POST", "/api/batches", """[{"subjectRef":"s","triggerEventId":"t","files":{}}]""", HttpStatusCode.BadRequest)]
    [InlineData("POST", "/api/batches", """[{"subjectRef":"s","triggerEventId":"t","files":{"a.exe":"x"}}]""", HttpStatusCode.BadRequest)]
    [InlineData("POST", "/api/batches", "batch-oversize", HttpStatusCode.BadRequest)]
    [InlineData("POST", "/api/batches", "batch-null-file", HttpStatusCode.BadRequest)]
    [InlineData("POST", "/api/batches", "batch-null-item", HttpStatusCode.BadRequest)]
    [InlineData("POST", "/api/batches", "batch-no-ids", HttpStatusCode.BadRequest)]
    [InlineData("POST", "/api/batches", """[{"subjectRef":" ","triggerEventId":"t","files":{"a.txt":"x"}}]""", HttpStatusCode.BadRequest)]
    [InlineData("GET", "/api/lists/candidates?mode=Nickname&name=x", "none", HttpStatusCode.BadRequest)]
    public async Task Unknown_ids_and_invalid_requests_are_refused(string method, string path, string body, HttpStatusCode expected)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = api.CreateClient();
        using HttpContent? content = body switch
        {
            "none" => null,
            "case-files" => Files("GC-11"),
            "no-files" => new MultipartFormDataContent { { new StringContent("s"), "subjectRef" }, { new StringContent("t"), "triggerEventId" } },
            "oversize" => new MultipartFormDataContent
            {
                { new StringContent("s"), "subjectRef" }, { new StringContent("t"), "triggerEventId" },
                { new ByteArrayContent(new byte[5 * 1024 * 1024 + 1]), "files", "案件資訊.txt" },
            },
            // Under 5 MB in characters, over 5 MB in UTF-8 bytes: the limit counts bytes.
            "batch-oversize" => JsonContent.Create(new[]
            {
                new { subjectRef = "s", triggerEventId = "t", files = new Dictionary<string, string> { ["a.txt"] = new('名', 2_000_000) } },
            }),
            "batch-null-file" => new StringContent("""[{"subjectRef":"s","triggerEventId":"t","files":{"a.txt":null}}]""", System.Text.Encoding.UTF8, "application/json"),
            "batch-null-item" => new StringContent("[null]", System.Text.Encoding.UTF8, "application/json"),
            "batch-no-ids" => new StringContent("""[{"files":{"a.txt":"x"}}]""", System.Text.Encoding.UTF8, "application/json"),
            _ => new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = content };
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(expected, response.StatusCode);
        if (body.StartsWith("batch-", StringComparison.Ordinal))
            Assert.Equal("Every item needs subjectRef, triggerEventId and .txt, .html or .htm files up to 5 MB each", await response.Content.ReadFromJsonAsync<string>(ct));
    }

    [Fact]
    public async Task Batch_runs_items_in_the_background_with_lineage_to_each_case()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = api.CreateClient();
        object Item(string gc) => new
        {
            subjectRef = "subject-" + gc,
            triggerEventId = "batch-" + gc,
            files = Directory.GetFiles(GoldenCases.GoldenCaseTests.Folder(gc), "*.txt").ToDictionary(p => Path.GetFileName(p), File.ReadAllText),
        };

        using var posted = await client.PostAsJsonAsync("/api/batches", new[] { Item("GC-11"), Item("GC-12") }, ct);
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        var batchId = (await posted.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("batchId").GetString();

        var batch = await Wait.UntilAsync(() => client.GetFromJsonAsync<JsonElement>($"/api/batches/{batchId}", ct),
            b => b.GetProperty("succeeded").GetInt32() == 2, "both batch items to succeed", ct);
        foreach (var item in batch.GetProperty("items").EnumerateArray())
        {
            Assert.Equal(1, item.GetProperty("analysisRevision").GetInt32());
            var c = await client.GetFromJsonAsync<JsonElement>($"/api/cases/{item.GetProperty("caseId").GetString()}", ct);
            Assert.Equal("IN_REVIEW", c.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task Health_check_pings_the_database()
    {
        using var client = api.CreateClient();
        Assert.Equal("Healthy", await client.GetStringAsync("/health", TestContext.Current.CancellationToken));
    }
}
