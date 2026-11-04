using System.Net.Http.Json;
using System.Text.Json;

namespace Aml.Tests.E2E;

public sealed class CodesApiTests(ApiHost api) : IClassFixture<ApiHost>
{
    [Fact]
    public async Task Codes_and_schemas_are_published()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = api.CreateClient();

        var problems = await client.GetFromJsonAsync<JsonElement>("/api/codes?kind=problem", ct);
        Assert.Contains(problems.GetProperty("codes").EnumerateArray(), c => c.GetProperty("code").GetString() == "INPUT_INCOMPLETE");
        // A value with detail attached is found by its key.
        var soft = await client.GetFromJsonAsync<JsonElement>("/api/codes/" + Uri.EscapeDataString("SOFT_DIFFERENCE（Jaccard 0.40，DISSIMILAR）"), ct);
        Assert.Equal(("SOFT_DIFFERENCE", "EVIDENCE_DETAIL"), (soft.GetProperty("code").GetString(), soft.GetProperty("kind").GetString()));

        var schemas = await client.GetFromJsonAsync<JsonElement>("/api/schemas", ct);
        Assert.Contains(schemas.GetProperty("names").EnumerateArray(), n => n.GetString() == "case-explanation");
        using var schema = await client.GetAsync("/api/schemas/case-analysis", ct);
        Assert.Equal("application/schema+json", schema.Content.Headers.ContentType?.MediaType);
        Assert.Equal("aml:CaseAnalysis:v1", (await schema.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("$id").GetString());
    }
}
