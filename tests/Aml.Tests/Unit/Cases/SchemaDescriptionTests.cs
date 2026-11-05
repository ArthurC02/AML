using System.Text.Json.Nodes;
using Aml.Core.Cases;

namespace Aml.Tests.Unit.Cases;

/// <summary>Every published schema describes every field, and marks names, dates and identifiers with x-sensitivity.</summary>
public class SchemaDescriptionTests
{
    public static TheoryData<string> Names() => [.. CaseJson.Schemas.Keys];

    [Theory]
    [MemberData(nameof(Names))]
    public void Every_field_has_a_description(string name)
    {
        var missing = new List<string>();
        Walk(CaseJson.Schema(CaseJson.Schemas[name]), "$", missing);
        Assert.Empty(missing);
    }

    private static void Walk(JsonNode? node, string path, List<string> missing)
    {
        if (node is JsonObject o)
        {
            if (o["properties"] is JsonObject props)
                foreach (var (field, schema) in props)
                    if (schema?["description"] is null) missing.Add($"{path}.{field}");
            foreach (var (key, child) in o) Walk(child, $"{path}.{key}", missing);
        }
        else if (node is JsonArray a)
            foreach (var child in a) Walk(child, path, missing);
    }

    [Fact]
    public void Document_numbers_are_identifiers_and_names_are_personal()
    {
        var schema = CaseJson.Schema(typeof(CaseAnalysis)).ToJsonString();
        Assert.Contains("\"x-sensitivity\":\"IDENTIFIER\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"x-sensitivity\":\"PERSONAL\"", schema, StringComparison.Ordinal);
        Assert.Contains($"\"$id\":\"aml:CaseAnalysis:v{CaseJson.SchemaVersion}\"", schema, StringComparison.Ordinal);
    }
}
