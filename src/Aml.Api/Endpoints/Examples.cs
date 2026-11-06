using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace Aml.Api.Endpoints;

/// <summary>
/// OpenAPI response examples: the GC-03 Case and hit summaries (Examples/*.json, embedded), kept equal to what the API
/// returns for GC-03 by ApiExampleTests. The full views hold identifier values and so have no example.
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

    /// <summary>The example of a non-default view, named by that view (the default view has no example).</summary>
    public static RouteHandlerBuilder WithViewExample(this RouteHandlerBuilder builder, string file, string view) =>
        builder.AddOpenApiOperationTransformer((op, ctx, ct) =>
        {
            op.Responses!["200"].Content!["application/json"].Examples = new Dictionary<string, IOpenApiExample>
            {
                [view] = new OpenApiExample { Summary = $"view={view}", Value = Of(file) },
            };
            return Task.CompletedTask;
        });

    private static JsonNode Of(string file)
    {
        using var stream = typeof(Examples).Assembly.GetManifestResourceStream(file)
            ?? throw new InvalidOperationException($"No embedded example {file}.");
        return JsonNode.Parse(stream)!;
    }
}
