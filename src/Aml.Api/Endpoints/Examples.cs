using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace Aml.Api.Endpoints;

/// <summary>GC-03 summaries (Examples/*.json), kept equal to the API's output by ApiExampleTests; full views hold identifiers, so have none.</summary>
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
