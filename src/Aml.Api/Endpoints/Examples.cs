using System.Text.Json.Nodes;

namespace Aml.Api.Endpoints;

/// <summary>
/// OpenAPI response examples: the GC-03 Case and hit summaries (Examples/*.json, embedded), kept equal to what the API
/// returns for GC-03 by ApiExampleTests.
/// </summary>
internal static class Examples
{
    public const string CaseSummary = "gc-03-case-summary.json";
    public const string HitSummary = "gc-03-hit-summary.json";

    public static RouteHandlerBuilder WithExample(this RouteHandlerBuilder builder, string file, params string[] statuses) =>
        builder.AddOpenApiOperationTransformer((op, ctx, ct) =>
        {
            foreach (var status in statuses) op.Responses![status].Content!["application/json"].Example = Of(file);
            return Task.CompletedTask;
        });

    private static JsonNode Of(string file)
    {
        using var stream = typeof(Examples).Assembly.GetManifestResourceStream(file)
            ?? throw new InvalidOperationException($"No embedded example {file}.");
        return JsonNode.Parse(stream)!;
    }
}
