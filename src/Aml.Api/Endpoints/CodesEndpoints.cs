using System.Text.Json.Nodes;
using Aml.Core.Cases;
using Aml.Core.Codes;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aml.Api.Endpoints;

/// <summary>Reference data: the code catalog and the JSON Schemas of the stored records.</summary>
internal static class CodesEndpoints
{
    public static void MapCodes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/codes", (string? kind) => TypedResults.Ok(new CodeList(CodeCatalog.Version,
                CodeCatalog.All.Values.Where(e => kind is null || e.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)).ToList())))
            .WithSummary("Every code the system emits: kind, Chinese name, meaning, the rule that emits it and its document section")
            .WithDescription("Filter with kind (IDENTITY_REASON, EVIDENCE_DETAIL, DATA_QUALITY, PROBLEM, …). A value with detail attached (SOFT_DIFFERENCE（…）, ENCODING_FALLBACK:<file>) is listed under its key. To explain one code use getCode instead. Next: getCode.")
            .WithName("listCodes")
            .WithTags("read");

        app.MapGet("/api/codes/{code}", Results<Ok<CodeEntry>, NotFound> (string code) =>
                CodeCatalog.Find(code) is { } e ? TypedResults.Ok(e) : TypedResults.NotFound())
            .WithSummary("One code; a value with detail attached is looked up by its key")
            .WithDescription("Use it to explain a code found in a response: its kind, Chinese name, meaning, the rule that emits it and the document section that defines it. Next: the document section, for the rule in full.")
            .WithName("getCode")
            .WithTags("read");

        app.MapGet("/api/schemas", () => TypedResults.Ok(new SchemaList(CaseJson.SchemaVersion, [.. CaseJson.Schemas.Keys])))
            .WithSummary("Names of the published JSON Schemas and the schema version")
            .WithDescription("Use it to find the schema of a record or view and the current schemaVersion; to explain a code value use getCode instead. Next: getSchema.")
            .WithName("listSchemas")
            .WithTags("read");

        app.MapGet("/api/schemas/{name}", Results<ContentHttpResult, NotFound> (string name) =>
                CaseJson.Schemas.TryGetValue(name, out var type)
                    ? TypedResults.Content(CaseJson.Schema(type).ToJsonString(), "application/schema+json")
                    : TypedResults.NotFound())
            .WithSummary("JSON Schema of a stored record: field descriptions, and x-sensitivity (PERSONAL, IDENTIFIER) on personal data")
            .WithDescription("Use it to validate or interpret a stored record or a view; x-sensitivity (personal data) is published only here, for the schemas listSchemas names. Next: getCode for the codes a field holds.")
            .WithName("getSchema")
            .WithTags("read");
    }
}

internal sealed record CodeList(string Version, IReadOnlyList<CodeEntry> Codes);

internal sealed record SchemaList(int SchemaVersion, IReadOnlyList<string> Names);
