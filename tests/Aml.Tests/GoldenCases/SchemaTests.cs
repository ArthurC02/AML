using System.Text.Json;
using System.Text.Json.Nodes;
using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Infra.Txt;

namespace Aml.Tests.GoldenCases;

/// <summary>The stored result and explanation of every Golden Case match their published JSON Schema and read back unchanged.</summary>
public class SchemaTests
{
    public static TheoryData<string> Cases() => ["GC-01", "GC-02", "GC-03", "GC-11", "GC-12"];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Result_and_explanation_validate_against_their_schema_and_round_trip(string gc)
    {
        var input = TxtCaseReader.Read(GoldenCaseTests.Folder(gc));
        var analysis = CaseAnalyzer.Analyze(input);
        var explanation = ExplanationBuilder.Build(input, analysis);

        foreach (var (value, type) in new (object, Type)[] { (analysis, typeof(CaseAnalysis)), (explanation, typeof(CaseExplanation)) })
        {
            var json = JsonSerializer.Serialize(value, type, CaseJson.Options);
            Assert.Empty(SchemaCheck.Errors(CaseJson.Schema(type), JsonNode.Parse(json), "$"));
            Assert.Equal(json, JsonSerializer.Serialize(JsonSerializer.Deserialize(json, type, CaseJson.Options), type, CaseJson.Options));
        }
    }
}

/// <summary>
/// The JSON Schema keywords the exporter emits (type, enum, properties, required, items, additionalProperties and local
/// $ref), checked recursively. ponytail: a subset validator; use a full one if the schemas grow oneOf or formats.
/// </summary>
internal static class SchemaCheck
{
    public static List<string> Errors(JsonNode? schema, JsonNode? value, string path, JsonNode? root = null)
    {
        var errors = new List<string>();
        root ??= schema;
        if (schema is JsonObject { } r && (string?)r["$ref"] is { } pointer)
        {
            schema = pointer.TrimStart('#', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Aggregate(root, (n, part) => n?[part]);
            if (schema is not JsonObject) return [$"{path}: unresolved $ref {pointer}"];
        }
        if (schema is not JsonObject s) return errors;
        if (s["type"] is { } t)
        {
            var types = t is JsonArray a ? a.Select(x => (string)x!).ToList() : [(string)t!];
            if (!types.Contains(Kind(value)) && !(Kind(value) == "integer" && types.Contains("number")))
            {
                errors.Add($"{path}: {Kind(value)} is not {string.Join("|", types)}");
                return errors;
            }
        }
        if (s["enum"] is JsonArray values && !values.Any(v => JsonNode.DeepEquals(v, value))) errors.Add($"{path}: {value} not in enum");
        if (value is JsonObject o)
        {
            if (s["required"] is JsonArray required)
                errors.AddRange(required.Select(r => (string)r!).Where(r => !o.ContainsKey(r)).Select(r => $"{path}.{r}: required"));
            var props = s["properties"] as JsonObject;
            foreach (var (name, child) in o)
            {
                var sub = props?[name] ?? s["additionalProperties"];
                if (sub is null && props is not null) errors.Add($"{path}.{name}: not in schema");
                errors.AddRange(Errors(sub, child, $"{path}.{name}", root));
            }
        }
        if (value is JsonArray items)
            for (var i = 0; i < items.Count; i++) errors.AddRange(Errors(s["items"], items[i], $"{path}[{i}]", root));
        return errors;
    }

    private static string Kind(JsonNode? v) => v?.GetValueKind() switch
    {
        null or JsonValueKind.Null => "null",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => v.ToJsonString().Contains('.', StringComparison.Ordinal) ? "number" : "integer",
    };
}
