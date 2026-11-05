using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aml.Core.Cases;

namespace Aml.Tests.GoldenCases;

/// <summary>
/// The OpenAPI response examples (src/Aml.Api/Examples) are the GC-03 Case and hit summaries as the API returns them, with
/// a made-up Case id, subject reference, trigger event id and Profile name in place of the sample's. A rule change that changes GC-03 changes the examples with it:
/// run with AML_UPDATE_SNAPSHOTS=1 and review the difference.
/// </summary>
public class ApiExampleTests
{
    public const string CaseId = "0199a3c2-5b7e-7c3d-9f10-2a4b6c8d0e03";
    public const string ProfileName = "王大明";

    [Fact]
    public void The_openapi_examples_are_the_gc03_summaries()
    {
        var (stored, rev) = CaseViewTests.Stored("GC-03");
        var c = stored with { CaseId = CaseId, SubjectRef = "CUST-REF-0003", TriggerEventId = "ALERT-0003" };
        var hit = Assert.Single(rev.Explanation!.Hits);
        var summary = CaseViews.Summary(c, rev);
        var examples = new Dictionary<string, object>
        {
            ["gc-03-case-summary.json"] = summary with { Hits = [.. summary.Hits!.Select(l => l with { ProfileName = ProfileName })] },
            ["gc-03-hit-summary.json"] = CaseViews.Hit(CaseId, rev.Revision, hit) with { ProfileName = ProfileName },
        };
        var plain = new JsonSerializerOptions(CaseJson.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

        var update = Environment.GetEnvironmentVariable("AML_UPDATE_SNAPSHOTS") == "1";
        Assert.False(update && Environment.GetEnvironmentVariable("CI") is not null, "AML_UPDATE_SNAPSHOTS is for local runs only.");
        foreach (var (file, value) in examples)
        {
            var actual = JsonSerializer.Serialize(value, value.GetType(), plain).ReplaceLineEndings("\n") + "\n";
            Assert.DoesNotContain(hit.ProfileName!, actual, StringComparison.Ordinal); // the sample's name is never published
            var path = Path.Combine(Folder(), file);
            if (update) File.WriteAllText(path, actual);
            Assert.True(File.Exists(path), $"{file} has no example; run with AML_UPDATE_SNAPSHOTS=1.");
            Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
        }
    }

    // ponytail: examples are read from the source tree, so the test runs where the repository is checked out.
    private static string Folder([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "src", "Aml.Api", "Examples"));
}
