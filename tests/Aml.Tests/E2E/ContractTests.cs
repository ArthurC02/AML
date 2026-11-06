using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aml.Tests.E2E;

/// <summary>
/// The published contract equals its snapshot: the OpenAPI document and every JSON Schema. After an intended change,
/// run with AML_UPDATE_SNAPSHOTS=1 to rewrite the snapshots and review their diff.
/// </summary>
public sealed class ContractTests(ApiHost api) : IClassFixture<ApiHost>
{
    private static readonly string[] Tags = ["Cases", "Batches", "Profiles", "Reference data"];
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public async Task The_openapi_document_and_schemas_equal_their_snapshots_and_every_operation_is_described_in_one_business_area()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = api.CreateClient();

        var openApi = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json", ct))!;
        var operations = openApi["paths"]!.AsObject().SelectMany(p => p.Value!.AsObject().Select(o => (Route: $"{o.Key} {p.Key}", Op: o.Value!))).ToList();
        Assert.All(operations, o =>
        {
            Assert.False(string.IsNullOrEmpty((string?)o.Op["operationId"]), o.Route);
            Assert.False(string.IsNullOrEmpty((string?)o.Op["description"]), o.Route);
            var tags = o.Op["tags"]?.AsArray().Select(t => (string?)t).ToList() ?? [];
            Assert.True(tags.Count == 1 && Tags.Contains(tags[0]), $"{o.Route} has tags [{string.Join(", ", tags)}]");
        });
        Assert.Equal(operations.Count, operations.Select(o => (string?)o.Op["operationId"]).Distinct().Count());
        // getCase, getHit and getProfile return the full shape (default) or a summary depending on view; both are in the contract.
        foreach (var (operation, shapes) in new[] { ("getCase", new[] { "CaseDetail", "CaseSummary" }), ("getHit", ["HitExplanation", "HitSummary"]), ("getProfile", ["ProfileView", "ProfileSummary"]) })
        {
            var oneOf = operations.Single(o => (string?)o.Op["operationId"] == operation).Op["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["oneOf"]!.AsArray();
            Assert.Equal(shapes.Select(s => $"#/components/schemas/{s}"), oneOf.Select(s => (string?)s!["$ref"]));
            Assert.All(shapes, name => Assert.NotNull(openApi["components"]!["schemas"]![name]));
        }

        // Every response example has each property its schema requires, at every level; oneOf takes the first branch that fits.
        var media = operations.SelectMany(o => o.Op["responses"]!.AsObject().Select(r => r.Value!["content"]?["application/json"])).OfType<JsonNode>().ToList();
        var examples = media.Where(m => m["example"] is not null).Select(m => (Schema: m["schema"]!, Value: m["example"]!))
            .Concat(media.SelectMany(m => (m["examples"]?.AsObject() ?? []).Select(e => (Schema: m["schema"]!, Value: e.Value!["value"]!)))).ToList();
        Assert.Equal(4, examples.Count);
        Assert.All(examples, e => Assert.Empty(Missing(openApi, e.Schema, e.Value, "$")));

        openApi.AsObject().Remove("servers"); // the host the document was served from, not part of the contract
        var snapshots = new Dictionary<string, JsonNode> { ["openapi.json"] = openApi };
        var names = JsonNode.Parse(await client.GetStringAsync("/api/schemas", ct))!["names"]!.AsArray().Select(n => (string)n!);
        foreach (var name in names)
            snapshots[$"schemas/{name}.json"] = JsonNode.Parse(await client.GetStringAsync($"/api/schemas/{name}", ct))!;

        var update = Environment.GetEnvironmentVariable("AML_UPDATE_SNAPSHOTS") == "1";
        Assert.False(update && Environment.GetEnvironmentVariable("CI") is not null, "AML_UPDATE_SNAPSHOTS is for local runs only.");
        var stale = Directory.Exists(Folder())
            ? Directory.GetFiles(Folder(), "*.json", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Folder(), f).Replace('\\', '/')).Except(snapshots.Keys).ToList()
            : [];
        if (update) stale.ForEach(f => File.Delete(Path.Combine(Folder(), f)));
        else Assert.True(stale.Count == 0, $"Snapshots with no published contract: {string.Join(", ", stale)}; run with AML_UPDATE_SNAPSHOTS=1.");
        foreach (var (file, node) in snapshots)
        {
            var path = Path.Combine(Folder(), file);
            var actual = node.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";
            if (update)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, actual, ct);
            }
            Assert.True(File.Exists(path), $"{file} has no snapshot; run with AML_UPDATE_SNAPSHOTS=1.");
            Assert.Equal((await File.ReadAllTextAsync(path, ct)).ReplaceLineEndings("\n"), actual);
        }
    }

    // The required properties a value lacks, as JSON paths. ponytail: checks presence only, not types; a validator when the contract needs one.
    private static List<string> Missing(JsonNode doc, JsonNode schema, JsonNode? value, string path)
    {
        if (schema["$ref"] is { } r) schema = doc["components"]!["schemas"]![((string)r!).Split('/')[^1]]!;
        if (schema["oneOf"] is JsonArray branches)
            return branches.Where(b => value is null || (string?)b!["type"] != "null")
                .Select(b => Missing(doc, b!, value, path)).FirstOrDefault(m => m.Count == 0) ?? [$"{path} fits no oneOf branch"];
        if (value is JsonArray items && schema["items"] is { } item)
            return [.. items.SelectMany((v, i) => Missing(doc, item, v, $"{path}[{i}]"))];
        if (value is not JsonObject o) return [];
        var missing = (schema["required"]?.AsArray() ?? []).Select(n => (string)n!).Where(n => !o.ContainsKey(n)).Select(n => $"{path}.{n}").ToList();
        foreach (var (name, v) in o)
            if (schema["properties"]?[name] is { } p) missing.AddRange(Missing(doc, p, v, $"{path}.{name}"));
        return missing;
    }

    // ponytail: snapshots are read from the source tree, so the test runs where the repository is checked out.
    private static string Folder([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "Contracts");
}
