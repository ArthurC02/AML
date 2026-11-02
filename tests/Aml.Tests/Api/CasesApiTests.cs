using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Aml.Tests.Api;

public sealed class CasesApiTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"aml-api-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public CasesApiTests() =>
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Database:ConnectionString", $"Data Source={_file};Pooling=False"));

    public void Dispose()
    {
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

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
        using var client = _factory.CreateClient();

        using var created = await client.PostAsync("/api/cases", Upload(gc, "evt-" + gc), ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var c = await created.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(status, c.GetProperty("status").GetString());
        Assert.Equal(1, c.GetProperty("currentRevision").GetInt32());

        var caseId = c.GetProperty("caseId").GetString();
        var analysis = await client.GetFromJsonAsync<JsonElement>($"/api/cases/{caseId}/analyses/1", ct);
        var hit = analysis.GetProperty("hits")[0];
        Assert.Equal(identityDecision, hit.GetProperty("identity").GetProperty("decision").GetString());

        // Same trigger and subject: the existing Case comes back, no new revision.
        using var repeated = await client.PostAsync("/api/cases", Upload(gc, "evt-" + gc), ct);
        var again = await repeated.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(caseId, again.GetProperty("caseId").GetString());
        Assert.Equal(1, again.GetProperty("currentRevision").GetInt32());
    }

    [Fact]
    public async Task Rejects_non_txt_and_incomplete_uploads()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        using var exe = new MultipartFormDataContent
        {
            { new StringContent("s"), "subjectRef" }, { new StringContent("t"), "triggerEventId" },
            { new ByteArrayContent([1, 2, 3]), "files", "evil.exe" },
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/cases", exe, ct)).StatusCode);

        using var incomplete = new MultipartFormDataContent
        {
            { new StringContent("s"), "subjectRef" }, { new StringContent("t"), "triggerEventId" },
            { new StringContent("x"), "files", "notes.txt" },
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/cases", incomplete, ct)).StatusCode);
    }
}
