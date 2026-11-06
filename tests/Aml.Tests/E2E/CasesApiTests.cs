using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

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
        var revision = await client.GetFromJsonAsync<JsonElement>($"/api/cases/{caseId}/revisions/1", ct);
        var hit = revision.GetProperty("result").GetProperty("hits")[0];
        Assert.Equal(identityDecision, hit.GetProperty("identity").GetProperty("decision").GetString());
        var first = revision.GetProperty("explanation").GetProperty("hits")[0];
        Assert.Equal(9, first.GetProperty("comparison").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(first.GetProperty("summary").GetProperty("conclusion").GetString()));

        // Same trigger and subject: the existing Case comes back with 200, no new revision.
        using var repeated = await client.PostAsync("/api/cases", Upload(gc, "evt-" + gc), ct);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Null(repeated.Headers.Location);
        var again = await repeated.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(caseId, again.GetProperty("caseId").GetString());
        Assert.Equal(1, again.GetProperty("currentRevision").GetInt32());
    }

    [Fact]
    public async Task A_case_reads_from_summary_to_hit_detail_to_its_revisions()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = api.CreateClient();

        using var created = await client.PostAsync("/api/cases", Upload("GC-03", "evt-views"), ct);
        var summary = await created.Content.ReadFromJsonAsync<JsonElement>(ct);
        var caseId = summary.GetProperty("caseId").GetString();
        Assert.Equal($"/api/cases/{caseId}", created.Headers.Location?.OriginalString);
        Assert.Equal("REVIEW", summary.GetProperty("result").GetString());
        Assert.False(string.IsNullOrEmpty(summary.GetProperty("conclusion").GetString()));

        var line = summary.GetProperty("hits")[0];
        Assert.Equal(("90000031", "DJ:90000031"), (line.GetProperty("profileId").GetString(), line.GetProperty("profileKey").GetString()));
        // The summary's hit link reads the hit summary, without the compared values; the full hit is the default view.
        var hit = await client.GetFromJsonAsync<JsonElement>(line.GetProperty("link").GetString(), ct);
        Assert.Equal(4, hit.GetProperty("stage3Row").GetInt32());
        Assert.Equal("90000031", hit.GetProperty("profileId").GetString());
        Assert.False(hit.TryGetProperty("comparison", out _));
        Assert.True(hit.GetProperty("wouldChangeIf").GetArrayLength() > 0);
        var detail = await client.GetFromJsonAsync<JsonElement>(hit.GetProperty("detail").GetString(), ct);
        Assert.Equal(9, detail.GetProperty("comparison").GetArrayLength());
        Assert.Equal(detail.GetRawText(), (await client.GetFromJsonAsync<JsonElement>($"/api/cases/{caseId}/hits/1", ct)).GetRawText());
        using var missing = await client.GetAsync($"/api/cases/{caseId}/hits/99", ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var again = await client.PostAsync($"/api/cases/{caseId}/analyses", Files("GC-03"), ct);
        Assert.Equal(2, (await again.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("effectiveRevision").GetInt32());
        var revisions = await client.GetFromJsonAsync<JsonElement>($"/api/cases/{caseId}/revisions", ct);
        Assert.Equal(2, revisions.GetArrayLength());
        Assert.True(revisions[1].GetProperty("effective").GetBoolean());
        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>($"/api/cases/{caseId}/hits/1?revision=1&view=summary", ct)).GetProperty("revision").GetInt32());
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>($"/api/cases/{caseId}/hits/1?view=summary", ct)).GetProperty("revision").GetInt32());
        var revision = await client.GetFromJsonAsync<JsonElement>((await client.GetFromJsonAsync<JsonElement>($"/api/cases/{caseId}?view=summary", ct)).GetProperty("revision").GetString(), ct);
        Assert.Equal(2, revision.GetProperty("revision").GetInt32());
        Assert.True(revision.GetProperty("lineage").GetProperty("inputFiles").GetArrayLength() > 0);
        Assert.Equal(1, revision.GetProperty("quality").GetProperty("hits").GetInt32());
        // The Case itself (view=full, the default) carries the full record of its effective revision.
        var full = await client.GetFromJsonAsync<JsonElement>($"/api/cases/{caseId}", ct);
        Assert.Equal((caseId, 2), (full.GetProperty("case").GetProperty("caseId").GetString(), full.GetProperty("effective").GetProperty("revision").GetInt32()));
        Assert.Equal(revision.GetRawText(), full.GetProperty("effective").GetRawText());
        Assert.Equal(full.GetRawText(), (await client.GetFromJsonAsync<JsonElement>($"/api/cases/{caseId}?view=full", ct)).GetRawText());

        // The workbook renders the effective revision, or ?revision=, and is never cached. Each one is counted by view.
        var counted = new System.Collections.Concurrent.ConcurrentBag<(string Instrument, object? View)>();
        using var meters = new System.Diagnostics.Metrics.MeterListener();
        meters.InstrumentPublished = (i, l) => { if (i.Meter.Name == Services.AmlTelemetry.Name && i.Name.StartsWith("aml.workbook", StringComparison.Ordinal)) l.EnableMeasurementEvents(i); };
        void Seen<T>(System.Diagnostics.Metrics.Instrument i, T _, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? __)
        {
            foreach (var t in tags) if (t.Key == "view") counted.Add((i.Name, t.Value));
        }
        meters.SetMeasurementEventCallback<long>(Seen);
        meters.SetMeasurementEventCallback<double>(Seen);
        meters.Start();
        using var workbook = await client.GetAsync($"/api/cases/{caseId}/workbook?revision=1", ct);
        Assert.Equal(HttpStatusCode.OK, workbook.StatusCode);
        Assert.Equal(Infra.Excel.Xlsx.ContentType, workbook.Content.Headers.ContentType?.MediaType);
        Assert.Equal(("attachment", $"case-{caseId}-revision-1.xlsx"), (workbook.Content.Headers.ContentDisposition?.DispositionType, workbook.Content.Headers.ContentDisposition?.FileName));
        Assert.NotNull(workbook.Content.Headers.ContentLength);
        Assert.Empty(workbook.Content.Headers.ContentEncoding);
        Assert.Equal((null, null), (workbook.Headers.ETag, workbook.Content.Headers.LastModified));
        Assert.Empty(workbook.Headers.AcceptRanges);
        Assert.True(workbook.Headers.CacheControl?.NoStore);
        Assert.Equal(["nosniff"], workbook.Headers.GetValues("X-Content-Type-Options"));
        var sheets = GoldenCases.WorkbookTests.Read(await workbook.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal(7, sheets.Count);
        var fullBook = GoldenCases.WorkbookTests.Read(await client.GetByteArrayAsync($"/api/cases/{caseId}/workbook?view=full", ct));
        Assert.Contains("受檢對象的值", fullBook.Single(s => s.Name == "比對").Rows[0]);
        Assert.DoesNotContain("受檢對象的值", sheets.Single(s => s.Name == "比對").Rows[0]);
        // Revision 1 is no longer effective, so its conclusion only points to the effective one.
        Assert.Contains(sheets[0].Rows, row => row[1] == "結論" && row[2]!.Contains("生效 revision 為 2", StringComparison.Ordinal));
        using var noRevision = await client.GetAsync($"/api/cases/{caseId}/workbook?revision=9", ct);
        Assert.Equal(HttpStatusCode.NotFound, noRevision.StatusCode);
        // One measurement per workbook issued and none for the 404. Only the endpoint records them, and tests of one class run in turn.
        foreach (var instrument in new[] { "aml.workbooks", "aml.workbook.duration", "aml.workbook.size" })
            Assert.Equal(["full", "summary"], counted.Where(m => m.Instrument == instrument).Select(m => m.View as string).Order());

        var found = await client.GetFromJsonAsync<JsonElement>("/api/cases?subjectRef=subject-GC-03", ct);
        Assert.Contains(found.GetProperty("items").EnumerateArray(), i => i.GetProperty("caseId").GetString() == caseId);
    }

    [Fact]
    public async Task A_case_is_created_from_json_with_201_and_a_repeat_returns_200()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = api.CreateClient();
        var files = Directory.GetFiles(GoldenCases.GoldenCaseTests.Folder("GC-11"), "*.txt").ToDictionary(p => Path.GetFileName(p), File.ReadAllText);
        var body = new { subjectRef = "subject-json", triggerEventId = "evt-json", files };

        using var created = await client.PostAsJsonAsync("/api/cases", body, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var caseId = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("caseId").GetString();
        Assert.Equal($"/api/cases/{caseId}", created.Headers.Location?.OriginalString);
        // JSON files are text: their source hash is the SHA-256 of the UTF-8 encoding.
        var lineage = (await client.GetFromJsonAsync<JsonElement>($"/api/cases/{caseId}/revisions/1", ct)).GetProperty("lineage");
        var caseFile = lineage.GetProperty("inputFiles").EnumerateArray().Single(f => f.GetProperty("name").GetString()!.StartsWith("案件資訊", StringComparison.Ordinal));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(files[caseFile.GetProperty("name").GetString()!]))),
            caseFile.GetProperty("sha256").GetString());

        using var repeated = await client.PostAsJsonAsync("/api/cases", body, ct);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(caseId, (await repeated.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("caseId").GetString());

        var openApi = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", ct);
        var content = openApi.GetProperty("paths").GetProperty("/api/cases").GetProperty("post").GetProperty("requestBody").GetProperty("content");
        Assert.True(content.TryGetProperty("multipart/form-data", out _));
        Assert.True(content.TryGetProperty("application/json", out _));
    }

    [Fact]
    public async Task Workbooks_past_the_concurrency_limit_are_refused_with_429_and_other_endpoints_are_not()
    {
        var ct = TestContext.Current.CancellationToken;
        // One permit and no queue; the Case store holds the first request until the test opens it.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var limited = api.WithWebHostBuilder(b => b
            .UseSetting("Api:WorkbookConcurrency", "1").UseSetting("Api:WorkbookQueue", "0")
            .ConfigureTestServices(s => s.AddSingleton<Core.Ports.ICaseStore>(sp =>
            {
                var proxy = System.Reflection.DispatchProxy.Create<Core.Ports.ICaseStore, Gated>();
                var gate = (Gated)(object)proxy;
                (gate.Inner, gate.Entered, gate.Open) = (ActivatorUtilities.CreateInstance<Infra.Persistence.Repositories.CaseStore>(sp), entered, open);
                return proxy;
            })));
        using var client = limited.CreateClient();

        var held = client.GetAsync("/api/cases/unknown/workbook", ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            using var refused = await client.GetAsync("/api/cases/unknown/workbook", ct);
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            Assert.Equal(["1"], refused.Headers.GetValues("Retry-After"));
            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(("/api/codes/HTTP_ERROR", 429), (problem.GetProperty("type").GetString(), problem.GetProperty("status").GetInt32()));
            Assert.Contains("Retry-After", problem.GetProperty("fix").GetString(), StringComparison.Ordinal);
            using var other = await client.GetAsync("/api/codes/HTTP_ERROR", ct);
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        }
        finally
        {
            open.TrySetResult(); // a failed assertion must not leave the held request hanging the host's disposal
        }
        using var first = await held;
        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);
        using var again = await client.GetAsync("/api/cases/unknown/workbook", ct);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Theory]
    [InlineData("0", "64")]
    [InlineData("16", "-1")]
    public void A_workbook_limit_that_cannot_work_stops_the_start(string concurrency, string queue)
    {
        using var broken = api.WithWebHostBuilder(b => b.UseSetting("Api:WorkbookConcurrency", concurrency).UseSetting("Api:WorkbookQueue", queue));
        Assert.Contains("Api:WorkbookConcurrency", Assert.Throws<InvalidOperationException>(() => broken.CreateClient()).Message, StringComparison.Ordinal);
    }

    /// <summary>A Case store whose GetAsync says it was entered, then waits until <see cref="Open"/> is set.</summary>
    public class Gated : System.Reflection.DispatchProxy
    {
        internal Core.Ports.ICaseStore Inner { get; set; } = null!;
        internal TaskCompletionSource Entered { get; set; } = null!;
        internal TaskCompletionSource Open { get; set; } = null!;

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == nameof(Core.Ports.ICaseStore.GetAsync) ? HeldAsync(targetMethod, args) : targetMethod.Invoke(Inner, args);

        private async Task<Core.Cases.CaseRecord?> HeldAsync(System.Reflection.MethodInfo targetMethod, object?[]? args)
        {
            Entered.TrySetResult();
            await Open.Task;
            return await (Task<Core.Cases.CaseRecord?>)targetMethod.Invoke(Inner, args)!;
        }
    }

    private static StringContent Charset(StringContent content, string charset)
    {
        content.Headers.ContentType!.CharSet = charset;
        return content;
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
        var stable = await client.GetFromJsonAsync<JsonElement>($"/api/cases/{caseId}/revisions/2", ct);
        Assert.Equal(0, stable.GetProperty("explanation").GetProperty("changesFromPreviousRevision").GetArrayLength());

        // Source update: the Profile's birth years become 1960 only → DOB conflict, identity PROBABLE_MATCH → REVIEW.
        using var updated = await client.PostAsync($"/api/cases/{caseId}/analyses", Files("GC-11", (name, text) =>
            name.StartsWith("名單資料", StringComparison.Ordinal)
                ? text.Replace("1	Date of Birth	1973", "1	Date of Birth	1960", StringComparison.Ordinal)
                      .Replace("2	Date of Birth	1972", "2	Date of Birth	1960", StringComparison.Ordinal)
                : text), ct);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var changed = await client.GetFromJsonAsync<JsonElement>($"/api/cases/{caseId}/revisions/3", ct);
        var changes = changed.GetProperty("explanation").GetProperty("changesFromPreviousRevision").EnumerateArray().ToList();
        Assert.Contains(changes, c => c.GetProperty("kind").GetString() == "IDENTITY_CHANGED"
            && c.GetProperty("from").GetString() == "PROBABLE_MATCH" && c.GetProperty("to").GetString() == "REVIEW");

        // Revision 2 is kept unchanged as history.
        var rev2 = await client.GetFromJsonAsync<JsonElement>($"/api/cases/{caseId}/revisions/2", ct);
        Assert.Equal("PROBABLE_MATCH", rev2.GetProperty("result").GetProperty("hits")[0].GetProperty("identity").GetProperty("decision").GetString());
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
        var problem = await Problem(refused, HttpStatusCode.BadRequest, "UNSUPPORTED_FILE", ct);
        Assert.Equal("evil.exe", problem.GetProperty("errors")[0].GetProperty("file").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("fix").GetString()));

        using var incomplete = new MultipartFormDataContent
        {
            { new StringContent("s"), "subjectRef" }, { new StringContent("t"), "triggerEventId" },
            { new StringContent("x"), "files", "notes.txt" },
        };
        using var unreadable = await client.PostAsync("/api/cases", incomplete, ct);
        var missing = await Problem(unreadable, HttpStatusCode.BadRequest, "INPUT_INCOMPLETE", ct);
        Assert.Equal(("案件資訊.txt", "MISSING"), (missing.GetProperty("errors")[0].GetProperty("file").GetString(), missing.GetProperty("errors")[0].GetProperty("problem").GetString()));

        using var twice = new MultipartFormDataContent
        {
            { new StringContent("s"), "subjectRef" }, { new StringContent("t"), "triggerEventId" },
            { new StringContent("x"), "files", "案件資訊.txt" }, { new StringContent("x"), "files", "案件資訊_2.txt" },
        };
        using var duplicated = await client.PostAsync("/api/cases", twice, ct);
        var duplicate = await Problem(duplicated, HttpStatusCode.BadRequest, "INPUT_INCOMPLETE", ct);
        Assert.Equal("DUPLICATE", duplicate.GetProperty("errors")[0].GetProperty("problem").GetString());
    }

    /// <summary>An RFC 9457 problem: the status, application/problem+json and the type of its catalog entry.</summary>
    private static async Task<JsonElement> Problem(HttpResponseMessage response, HttpStatusCode status, string code, CancellationToken ct)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(($"/api/codes/{code}", (int)status), (problem.GetProperty("type").GetString(), problem.GetProperty("status").GetInt32()));
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("detail").GetString()));
        Assert.Equal(JsonValueKind.Array, problem.GetProperty("errors").ValueKind);
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("fix").GetString()));
        return problem;
    }

    [Theory]
    [InlineData("GET", "/api/cases/unknown", "none", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("GET", "/api/cases/unknown/analyses/1", "none", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("GET", "/api/cases/unknown/explanation", "none", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("GET", "/api/lists/merges", "none", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("GET", "/api/cases/unknown/candidates", "none", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("GET", "/api/cases/unknown/candidates?source=other", "none", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("GET", "/api/profiles/unknown", "none", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("GET", "/api/profiles/unknown?view=detail", "none", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("GET", "/api/profiles/search?name=x&top=0", "none", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("GET", "/api/batches/unknown?view=table", "none", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("GET", "/api/batches/unknown?pageSize=0", "none", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("GET", "/api/batches/unknown?status=DONE", "none", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("GET", "/api/batches/unknown?status=1", "none", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("POST", "/api/cases", "json-no-ids", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("POST", "/api/cases", "json-broken", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("POST", "/api/cases", "form-no-ids", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("POST", "/api/cases", "form-broken", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("POST", "/api/cases", "form-twice", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("POST", "/api/cases", "json-charset", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("POST", "/api/cases", """{"subjectRef":"s","triggerEventId":"t","files":{"x/案件資訊.txt":"x"}}""", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("POST", "/api/cases/unknown/analyses", "case-files", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("GET", "/api/batches/unknown", "none", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("POST", "/api/batches/unknown/rerun", "none", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("GET", "/api/profiles/unknown/versions", "none", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("POST", "/api/cases", "no-files", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("POST", "/api/cases", "oversize", HttpStatusCode.BadRequest, "UNSUPPORTED_FILE")]
    [InlineData("POST", "/api/cases", """{"subjectRef":"s","triggerEventId":"t","files":{"notes.txt":"x"}}""", HttpStatusCode.BadRequest, "INPUT_INCOMPLETE")]
    [InlineData("POST", "/api/batches", "[]", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("POST", "/api/batches", """[{"subjectRef":"s","triggerEventId":"t","files":{}}]""", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("POST", "/api/batches", """[{"subjectRef":"s","triggerEventId":"t","files":{"a.exe":"x"}}]""", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("POST", "/api/batches", "batch-oversize", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("POST", "/api/batches", "batch-null-file", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("POST", "/api/batches", "batch-null-item", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("POST", "/api/batches", "batch-no-ids", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("POST", "/api/batches", """[{"subjectRef":" ","triggerEventId":"t","files":{"a.txt":"x"}}]""", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("GET", "/api/profiles/search", "none", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("GET", "/api/no-such-route", "none", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("GET", "/api/codes/NO_SUCH_CODE", "none", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("GET", "/api/schemas/no-such-schema", "none", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("GET", "/api/cases/unknown/hits/1", "none", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("GET", "/api/cases/unknown/hits/1?view=table", "none", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("GET", "/api/cases/unknown?view=summary", "none", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("GET", "/api/cases/unknown?view=table", "none", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("GET", "/api/cases/unknown/revisions", "none", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("GET", "/api/cases/unknown/revisions/1", "none", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("GET", "/api/cases/unknown/workbook", "none", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("GET", "/api/cases/unknown/workbook?view=table", "none", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("GET", "/api/cases", "none", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("GET", "/api/cases?subjectRef=s&pageSize=101", "none", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("GET", "/api/cases?subjectRef=s&pageSize=abc", "none", HttpStatusCode.BadRequest, "INVALID_REQUEST")] // binding failure
    [InlineData("DELETE", "/api/cases", "none", HttpStatusCode.MethodNotAllowed, "HTTP_ERROR")]
    [InlineData("GET", "/api/cases?subjectRef=s&pageToken=bm90LWEtdG9rZW4", "none", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    public async Task Unknown_ids_and_invalid_requests_are_refused(string method, string path, string body, HttpStatusCode expected, string code)
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
            "json-no-ids" => new StringContent("""{"files":{"a.txt":"x"}}""", System.Text.Encoding.UTF8, "application/json"),
            "json-broken" => new StringContent("""{"subjectRef":""", System.Text.Encoding.UTF8, "application/json"),
            "form-no-ids" => new MultipartFormDataContent { { new StringContent("x"), "files", "案件資訊.txt" } },
            "form-broken" => new StringContent("x", System.Text.Encoding.UTF8, "multipart/form-data"),
            "form-twice" => new MultipartFormDataContent
            {
                { new StringContent("a"), "subjectRef" }, { new StringContent("b"), "subjectRef" }, { new StringContent("t"), "triggerEventId" },
                { new StringContent("x"), "files", "案件資訊.txt" },
            },
            "json-charset" => Charset(new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), "foo"),
            _ => new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = content };
        using var response = await client.SendAsync(request, ct);
        var problem = await Problem(response, expected, code, ct);
        if (body.StartsWith("batch-", StringComparison.Ordinal))
            Assert.Equal(0, problem.GetProperty("errors")[0].GetProperty("item").GetInt32());
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

        // The default summary view lists only the items not yet succeeded.
        var summary = await Wait.UntilAsync(() => client.GetFromJsonAsync<JsonElement>($"/api/batches/{batchId}", ct),
            b => b.GetProperty("succeeded").GetInt32() == 2, "both batch items to succeed", ct);
        Assert.Equal(0, summary.GetProperty("items").GetArrayLength());
        var batch = await client.GetFromJsonAsync<JsonElement>($"/api/batches/{batchId}?view=full&pageSize=1", ct);
        Assert.Equal(1, batch.GetProperty("items").GetArrayLength());
        var rest = await client.GetFromJsonAsync<JsonElement>($"/api/batches/{batchId}?view=full&pageSize=1&pageToken={batch.GetProperty("nextPageToken").GetString()}", ct);
        Assert.Equal(JsonValueKind.Null, rest.GetProperty("nextPageToken").ValueKind);
        Assert.NotEqual(batch.GetProperty("items")[0].GetProperty("jobId").GetString(), rest.GetProperty("items")[0].GetProperty("jobId").GetString());
        var succeeded = await client.GetFromJsonAsync<JsonElement>($"/api/batches/{batchId}?status=SUCCEEDED", ct);
        Assert.Equal(2, succeeded.GetProperty("items").GetArrayLength());
        foreach (var item in batch.GetProperty("items").EnumerateArray().Concat(rest.GetProperty("items").EnumerateArray()))
        {
            Assert.Equal(1, item.GetProperty("analysisRevision").GetInt32());
            var c = await client.GetFromJsonAsync<JsonElement>($"/api/cases/{item.GetProperty("caseId").GetString()}", ct);
            Assert.Equal("IN_REVIEW", c.GetProperty("case").GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task Health_check_pings_the_database()
    {
        using var client = api.CreateClient();
        Assert.Equal("Healthy", await client.GetStringAsync("/health", TestContext.Current.CancellationToken));
    }
}
