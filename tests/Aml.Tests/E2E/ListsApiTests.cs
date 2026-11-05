using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Aml.Tests.E2E;

/// <summary>List database, source refresh and retrieval modes through the API.</summary>
public sealed class ListsApiTests(ApiHost api) : IClassFixture<ApiHost>
{

    private static readonly string Gc11 = GoldenCases.GoldenCaseTests.Folder("GC-11");
    private const string ProfileName = "名單資料_90000011.txt";

    private static MultipartFormDataContent Files(params (string Name, string Content)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, content) in files) form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "files", name);
        return form;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r, CancellationToken ct)
    {
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(ct))!;
    }

    [Fact]
    public async Task A_changed_profile_gets_a_new_version_and_re_analyzes_the_cases_that_used_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = api.CreateClient();
        var caseFiles = Directory.GetFiles(Gc11, "*.txt").Select(p => (Path.GetFileName(p), File.ReadAllText(p))).ToArray();
        var create = Files(caseFiles);
        create.Add(new StringContent("subject-11"), "subjectRef");
        create.Add(new StringContent("event-11"), "triggerEventId");
        var created = await http.PostAsync("/api/cases", create, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var caseId = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("caseId").GetString();

        // First capture of the same text the Case used: version 1, nothing to re-analyze.
        var original = caseFiles.Single(f => f.Item1 == ProfileName).Item2;
        var first = await Json(await http.PostAsync("/api/profiles/import", Files((ProfileName, original)), ct), ct);
        Assert.Equal(1, first.GetProperty("profiles")[0].GetProperty("version").GetInt32());
        Assert.Equal(("90000011", "DJ:90000011"), (first.GetProperty("profiles")[0].GetProperty("profileId").GetString(), first.GetProperty("profiles")[0].GetProperty("profileKey").GetString()));
        Assert.Equal(0, first.GetProperty("queuedReanalyses").GetArrayLength());

        // Same content again: unchanged.
        var again = await Json(await http.PostAsync("/api/profiles/import", Files((ProfileName, original)), ct), ct);
        Assert.False(again.GetProperty("profiles")[0].GetProperty("changed").GetBoolean());

        // A source update: full DOB. New version, the old one kept, and the queued job gives the Case revision 2.
        var updated = original.Replace("Date of Birth\t1973", "Date of Birth\t1973/6/12", StringComparison.Ordinal);
        var refresh = await Json(await http.PostAsync("/api/profiles/import", Files((ProfileName, updated)), ct), ct);
        Assert.Equal(2, refresh.GetProperty("profiles")[0].GetProperty("version").GetInt32());
        Assert.Equal(caseId, refresh.GetProperty("queuedReanalyses")[0].GetProperty("caseId").GetString());
        var batchId = refresh.GetProperty("batchId").GetString();
        await Wait.UntilAsync(async () => await Json(await http.GetAsync($"/api/batches/{batchId}", ct), ct),
            b => b.GetProperty("succeeded").GetInt32() == 1, "the queued re-analysis to succeed", ct);
        var c = await Json(await http.GetAsync($"/api/cases/{caseId}", ct), ct);
        Assert.Equal(2, c.GetProperty("currentRevision").GetInt32());
        var versions = await Json(await http.GetAsync("/api/profiles/DJ:90000011/versions", ct), ct);
        Assert.Equal(2, versions.GetArrayLength());

        // Retrieval: search, direct reference, and a Case's own candidates.
        var search = await Json(await http.GetAsync("/api/profiles/search?name=" + Uri.EscapeDataString("鍾仁聲"), ct), ct);
        Assert.Equal(("DJ:90000011", "90000011", "EXACT_ALIAS"), (search[0].GetProperty("profileKey").GetString(), search[0].GetProperty("profileId").GetString(), search[0].GetProperty("route").GetString()));
        var direct = await Json(await http.GetAsync("/api/profiles/DJ:90000011", ct), ct);
        Assert.Equal((2, "DJ:90000011"), (direct.GetProperty("version").GetInt32(), direct.GetProperty("profileKey").GetString()));
        Assert.Equal(1, (await Json(await http.GetAsync("/api/profiles/90000011?version=1", ct), ct)).GetProperty("version").GetInt32());
        var previous = await Json(await http.GetAsync($"/api/cases/{caseId}/candidates?source=previous", ct), ct);
        Assert.Equal(("DJ:90000011", "90000011"), (previous[0].GetProperty("profileKey").GetString(), previous[0].GetProperty("profileId").GetString()));
        var controlled = await Json(await http.GetAsync($"/api/cases/{caseId}/candidates", ct), ct);
        Assert.Equal(1, controlled.GetArrayLength()); // two candidate rows, one Profile
        Assert.Equal(("DJ:90000011", "90000011"), (controlled[0].GetProperty("profileKey").GetString(), controlled[0].GetProperty("profileId").GetString()));

        var missing = await http.GetAsync("/api/profiles/search", ct);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("/api/codes/INVALID_REQUEST", (await missing.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("type").GetString());

        // Wiring: an HTML page and its .txt export hash to one Profile version; merges list refused merges apart.
        await http.PostAsync("/api/profiles/import", Files(("名單資料_123.txt", "Profile_ID:\t123\nGender:\tMale\n")), ct);
        var html = await Json(await http.PostAsync("/api/profiles/import", Files(("名單資料_123.html",
            "<html><body><table><tr><td>Profile_ID</td><td>123</td></tr><tr><td>Gender</td><td>Male</td></tr></table></body></html>")), ct), ct);
        Assert.Equal(1, html.GetProperty("profiles")[0].GetProperty("version").GetInt32());
        var merges = await Json(await http.GetAsync("/api/profiles/merges", ct), ct);
        Assert.Equal(JsonValueKind.Array, merges.GetProperty("rejected").ValueKind);
    }
}
