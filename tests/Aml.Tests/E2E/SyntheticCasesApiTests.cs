using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aml.Tests.Synthetic;

namespace Aml.Tests.E2E;

/// <summary>
/// Synthetic Cases uploaded through POST /api/cases (one .txt and one .html export, both of adverse records whose result
/// does not depend on the analysis date) come back from GET …/revisions/1 with the hits the in-process measurement gave.
/// </summary>
public sealed class SyntheticCasesApiTests(ApiHost api) : IClassFixture<ApiHost>
{
    [Fact]
    public async Task Uploaded_synthetic_cases_read_back_as_measured()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = api.CreateClient();
        var all = SyntheticCaseGenerator.Generate();
        var chosen = new[] { all.First(c => c.Candidates[0].Kind == RecordKind.News && !c.Html), all.First(c => c.Candidates[0].Kind == RecordKind.CourtRuling && c.Html) };
        var measured = SyntheticMeasure.Run(chosen, SyntheticAssumptions.Default.AsOf);

        foreach (var c in chosen)
        {
            using var form = new MultipartFormDataContent();
            foreach (var (name, text) in c.Files) form.Add(new StringContent(text), "files", name);
            form.Add(new StringContent("syn-" + c.Id), "subjectRef");
            form.Add(new StringContent("evt-" + c.Id), "triggerEventId");
            using var created = await client.PostAsync("/api/cases", form, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var caseId = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("caseId").GetString();
            var hits = (await client.GetFromJsonAsync<JsonElement>($"/api/cases/{caseId}/revisions/1", ct)).GetProperty("result").GetProperty("hits").EnumerateArray()
                .Select(h => (h.GetProperty("identity").GetProperty("decision").GetString(), h.GetProperty("pairResult").GetString(),
                    h.GetProperty("conditions")[0].GetProperty("legalStage").GetString()));
            static string Upper(object v) => JsonNamingPolicy.SnakeCaseUpper.ConvertName(v.ToString()!);
            Assert.Equal(measured.Where(o => o.Case == c).Select(o => ((string?)Upper(o.Hit!.Identity.Decision), (string?)Upper(o.Hit.PairResult), (string?)Upper(o.Hit.Conditions[0].LegalStage))), hits);
        }
    }
}
