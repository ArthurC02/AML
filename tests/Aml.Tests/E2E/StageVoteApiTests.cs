using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aml.Core.Ports;
using Aml.Core.Screening;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aml.Tests.E2E;

/// <summary>
/// The rules and the stage models voting through the API (watchlist-and-adverse-information.md#three-readers, StageVote) on
/// GC-12, whose attached news the rules read as INDICTED by keyword. The models are fakes registered as the host registers the
/// configured ones.
/// </summary>
public sealed class StageVoteApiTests(ApiHost api) : IClassFixture<ApiHost>
{
    /// <summary>A model reader: a stage, "null" (cannot answer: unavailable, error, timeout) or "throw".</summary>
    private sealed class Model(string name, string answer) : IStageReader
    {
        public string Name => name;

        public StageAnswer? Read(string text, IReadOnlyList<string> names) => answer switch
        {
            "null" => null,
            "throw" => throw new InvalidOperationException("model failed"),
            _ => new(Enum.Parse<LegalStage>(answer, ignoreCase: true)),
        };
    }

    // Properties whose values are catalog codes (a value may carry detail after the code, and a difference joins several with ；).
    private static readonly HashSet<string> CodeProperties =
    [
        "reasonCodes", "pairReasonCodes", "reasonCode", "hardConflict", "detail", "difference", "legalStage", "legalStageBasis", "basis", "stage",
        "riskCategories", "offenceCategories", "findings", "dataQualityFindings", "usedFor", "pendingChecks", "pepType",
    ];

    /// <param name="classifier">The classifier's answer; "-" when it is not configured.</param>
    /// <param name="llm">The language model's answer; "-" when it is not configured.</param>
    /// <param name="readers">The condition's stageReaders as reader:stage, in order.</param>
    [Theory]
    [InlineData("INDICTED", "INDICTED", "INDICTED", "STAGE_VOTE_UNANIMOUS", "", "rules:INDICTED,classifier:INDICTED,llm:INDICTED")]
    [InlineData("CONVICTED", "CONVICTED", "UNKNOWN", "STAGE_VOTE_MAJORITY", "STAGE_READERS_DISAGREE", "rules:INDICTED,classifier:CONVICTED,llm:CONVICTED")]
    [InlineData("CONVICTED", "-", "INDICTED", "STAGE_KEYWORD", "STAGE_READERS_DISAGREE", "rules:INDICTED,classifier:CONVICTED")]
    [InlineData("CONVICTED", "null", "INDICTED", "STAGE_KEYWORD", "STAGE_READERS_DISAGREE", "rules:INDICTED,classifier:CONVICTED")]
    [InlineData("throw", "CONVICTED", "INDICTED", "STAGE_KEYWORD", "STAGE_READERS_DISAGREE", "rules:INDICTED,llm:CONVICTED")]
    public async Task The_stage_models_vote_with_the_rules_and_leave_identity_alone(string classifier, string llm, string stage, string basis, string reasons, string readers)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            if (classifier != "-") s.AddSingleton<IStageReader>(new Model("classifier", classifier));
            if (llm != "-") s.AddSingleton<IStageReader>(new Model("llm", llm));
        }));
        using var client = host.CreateClient();
        var form = new MultipartFormDataContent { { new StringContent($"subject-vote-{classifier}-{llm}"), "subjectRef" }, { new StringContent("evt-vote"), "triggerEventId" } };
        foreach (var (name, text) in GoldenCaseFiles.Read("GC-12")) form.Add(new StringContent(text), "files", name);

        using var created = await client.PostAsync("/api/cases", form, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var caseId = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("caseId").GetString();
        var revision = await client.GetFromJsonAsync<JsonElement>($"/api/cases/{caseId}/revisions/1", ct);
        var hit = Assert.Single(revision.GetProperty("result").GetProperty("hits").EnumerateArray());
        var c = Assert.Single(hit.GetProperty("conditions").EnumerateArray());

        Assert.Equal(("ADVERSE_MEDIA", "HIT", stage, basis), (c.GetProperty("type").GetString(), c.GetProperty("status").GetString(), c.GetProperty("legalStage").GetString(), c.GetProperty("legalStageBasis").GetString()));
        Assert.Equal(reasons.Length == 0 ? [] : reasons.Split(','), c.GetProperty("reasonCodes").EnumerateArray().Select(r => r.GetString()));
        var stageReaders = c.GetProperty("stageReaders").EnumerateArray().ToList();
        Assert.Equal(readers.Split(','), stageReaders.Select(r => $"{r.GetProperty("reader").GetString()}:{r.GetProperty("stage").GetString()}"));
        Assert.Equal("STAGE_KEYWORD", stageReaders[0].GetProperty("basis").GetString()); // the rules' own basis stays on their line

        // Identity is decided apart from the stage: GC-12's expected.yaml, as without models.
        Assert.Equal(("REVIEW", 6, "REVIEW", "LOW", 0),
            (hit.GetProperty("identity").GetProperty("decision").GetString(), hit.GetProperty("identity").GetProperty("stage3Row").GetInt32(),
                hit.GetProperty("pairResult").GetString(), hit.GetProperty("reviewPriority").GetString(), hit.GetProperty("pairReasonCodes").GetArrayLength()));

        // Every code the stored revision carries is in the catalog.
        var codes = Codes(revision).Select(Core.Codes.CodeCatalog.Key).Distinct().ToList();
        Assert.Contains(basis, codes);
        foreach (var code in codes)
        {
            using var entry = await client.GetAsync("/api/codes/" + Uri.EscapeDataString(code), ct);
            Assert.True(entry.StatusCode == HttpStatusCode.OK, $"{code} is not in /api/codes");
        }
    }

    private static IEnumerable<string> Codes(JsonElement e, string? property = null) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().SelectMany(p => Codes(p.Value, p.Name)),
        JsonValueKind.Array => e.EnumerateArray().SelectMany(x => Codes(x, property)),
        JsonValueKind.String when property is not null && CodeProperties.Contains(property) => e.GetString()!.Split('；'),
        _ => [],
    };
}
