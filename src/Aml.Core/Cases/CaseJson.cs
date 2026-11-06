using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using Aml.Core.Explanation;
using Aml.Core.Shared;

namespace Aml.Core.Cases;

/// <summary>
/// The JSON form of stored and returned records, and their JSON Schemas. A stored record is read back into its type; when the
/// schema changes, <see cref="SchemaVersion"/> goes up and the PoC database is rebuilt (no old formats are read).
/// </summary>
public static class CaseJson
{
    public const int SchemaVersion = 2;

    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    /// <summary>Published schemas by name (GET /api/schemas/{name}).</summary>
    public static readonly IReadOnlyDictionary<string, Type> Schemas = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["case-analysis"] = typeof(CaseAnalysis),
        ["case-explanation"] = typeof(CaseExplanation),
        ["analysis-lineage"] = typeof(AnalysisLineage),
        ["analysis-quality"] = typeof(AnalysisQuality),
        ["revision"] = typeof(AnalysisRecord),
        ["revision-line"] = typeof(RevisionLine),
        ["case-detail"] = typeof(CaseDetail),
        ["case-summary"] = typeof(CaseSummary),
        ["hit-summary"] = typeof(HitSummary),
        ["case-page"] = typeof(CasePage),
    };

    /// <summary>JSON Schema of <paramref name="type"/>: field descriptions from [Description], sensitivity as x-sensitivity.</summary>
    public static JsonNode Schema(Type type) => Options.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions
    {
        TransformSchemaNode = (ctx, node) =>
        {
            if (node is not JsonObject o) return node;
            var member = ctx.PropertyInfo?.AttributeProvider as MemberInfo;
            if (member?.GetCustomAttribute<DescriptionAttribute>() is { } d) o.Insert(0, "description", d.Description);
            if (member?.GetCustomAttribute<SensitivityAttribute>() is { } s) o["x-sensitivity"] = JsonNamingPolicy.SnakeCaseUpper.ConvertName(s.Level.ToString());
            if (ctx.PropertyInfo is null && ctx.Path.IsEmpty) o.Insert(0, "$id", $"aml:{type.Name}:v{SchemaVersion}");
            // Nulls are left out when written, so a nullable field is never required.
            if (o["required"] is JsonArray required && o["properties"] is JsonObject props)
            {
                static bool Nullable(JsonNode? p) => (p?["type"] as JsonArray ?? p?["enum"] as JsonArray)?.Any(x => x is null || x.ToString() == "null") == true;
                var kept = required.Select(r => (string)r!).Where(r => !Nullable(props[r])).ToList();
                if (kept.Count == 0) o.Remove("required");
                else o["required"] = new JsonArray([.. kept.Select(k => (JsonNode)k)]);
            }
            return o;
        },
    });
}
