using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using Aml.Core.Explanation;
using Aml.Core.Shared;

namespace Aml.Core.Cases;

public static class CaseJson
{
    // Bumped on every schema change; the PoC database is then rebuilt, since old formats are never read.
    public const int SchemaVersion = 7;

    public static readonly JsonSerializerOptions Options = Create();

    public static string Code<T>(T value) where T : struct, Enum => JsonNamingPolicy.SnakeCaseUpper.ConvertName(value.ToString());

    /// <summary>Accepts IN_REVIEW or InReview in any case; unlike Enum.TryParse, never a number.</summary>
    public static bool TryParseCode<T>(string code, out T value) where T : struct, Enum
    {
        var nameWithoutUnderscores = code.Replace("_", "", StringComparison.Ordinal);
        var name = Enum.GetNames<T>().FirstOrDefault(n => n.Equals(nameWithoutUnderscores, StringComparison.OrdinalIgnoreCase));
        value = name is null ? default : Enum.Parse<T>(name);
        return name is not null;
    }

    public static T ParseCode<T>(string code) where T : struct, Enum =>
        TryParseCode<T>(code, out var value) ? value : throw new FormatException($"'{code}' is not a {typeof(T).Name} code.");

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

    // Served at GET /api/schemas/{name}.
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

    public static JsonNode Schema(Type type) => Options.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions
    {
        TransformSchemaNode = (ctx, node) =>
        {
            if (node is not JsonObject o) return node;
            var member = ctx.PropertyInfo?.AttributeProvider as MemberInfo;
            if (member?.GetCustomAttribute<DescriptionAttribute>() is { } d) o.Insert(0, "description", d.Description);
            if (member?.GetCustomAttribute<SensitivityAttribute>() is { } s) o["x-sensitivity"] = Code(s.Level);
            if (ctx.PropertyInfo is null && ctx.Path.IsEmpty) o.Insert(0, "$id", $"aml:{type.Name}:v{SchemaVersion}");
            RequireOnlyNonNullable(o);
            return o;
        },
    });

    // Options leave nulls out when writing (WhenWritingNull), so a nullable field may be absent.
    private static void RequireOnlyNonNullable(JsonObject schema)
    {
        if (schema["required"] is not JsonArray required || schema["properties"] is not JsonObject props) return;
        var kept = required.Select(r => (string)r!).Where(r => !IsNullable(props[r])).ToList();
        if (kept.Count == 0) schema.Remove("required");
        else schema["required"] = new JsonArray([.. kept.Select(k => (JsonNode)k)]);
    }

    private static bool IsNullable(JsonNode? property) =>
        (property?["type"] as JsonArray ?? property?["enum"] as JsonArray)?.Any(x => x is null || x.ToString() == "null") == true;
}
