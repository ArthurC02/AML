using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Aml.Tests.E2E;

/// <summary>The body limit is Kestrel's, so this class runs the API on a real Kestrel server (the in-memory test server has no limit).</summary>
public sealed class RequestLimitTests
{
    [Fact]
    public async Task A_body_over_the_limit_is_a_413_problem_with_its_fix()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = new ApiHost();
        await using var api = host.WithWebHostBuilder(b => b.UseSetting("Api:MaxRequestBytes", "1024"));
        api.UseKestrel(0);
        api.StartServer();
        using var client = api.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/cases",
            new { subjectRef = "S1", triggerEventId = "T1", files = new Dictionary<string, string> { ["案件資訊.txt"] = new('x', 4096) } }, ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(("/api/codes/HTTP_ERROR", 413), (problem.GetProperty("type").GetString(), problem.GetProperty("status").GetInt32()));
        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal(("POST", "/api/cases"), (error.GetProperty("method").GetString(), error.GetProperty("path").GetString()));
        // Problems.Fix(413); Problems is internal to the API.
        Assert.Equal("Send fewer or smaller files per request: the request body is over the server's limit (Api:MaxRequestBytes).",
            problem.GetProperty("fix").GetString());
    }
}
