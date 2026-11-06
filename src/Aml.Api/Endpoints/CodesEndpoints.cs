using Aml.Core.Cases;
using Aml.Core.Codes;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aml.Api.Endpoints;

internal static class CodesEndpoints
{
    public static void MapCodes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/codes", (string? kind) => TypedResults.Ok(new CodeList(CodeCatalog.Version,
                CodeCatalog.All.Values.Where(e => kind is null || e.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)).ToList())))
            .WithSummary("Every code the system emits: kind, Chinese name, meaning, the rule that emits it and its document section")
            .WithDescription("Filter with kind (IDENTITY_REASON, EVIDENCE_DETAIL, DATA_QUALITY, PROBLEM, …). A value with detail attached (SOFT_DIFFERENCE（…）, ENCODING_FALLBACK:<file>) is listed under its key. HIT and UNKNOWN serve several kinds and are listed once, under CONDITION_STATUS.")
            .WithName("listCodes")
            .WithTags(Tags.ReferenceData);

        app.MapGet("/api/codes/{code}", Results<Ok<CodeEntry>, NotFound> (string code) =>
                CodeCatalog.Find(code) is { } e ? TypedResults.Ok(e) : TypedResults.NotFound())
            .WithSummary("One code; a value with detail attached is looked up by its key")
            .WithDescription("The meaning of a code found in a response: its kind, Chinese name, meaning, the rule that emits it and the document section that defines the rule in full.")
            .WithName("getCode")
            .WithTags(Tags.ReferenceData);

        app.MapGet("/api/schemas", () => TypedResults.Ok(new SchemaList(CaseJson.SchemaVersion, [.. CaseJson.Schemas.Keys])))
            .WithSummary("Names of the published JSON Schemas and the schema version")
            .WithDescription("The schema names of the stored records and views, and the current schemaVersion.")
            .WithName("listSchemas")
            .WithTags(Tags.ReferenceData);

        app.MapGet("/api/schemas/{name}", Results<ContentHttpResult, NotFound> (string name) =>
                CaseJson.Schemas.TryGetValue(name, out var type)
                    ? TypedResults.Content(CaseJson.Schema(type).ToJsonString(), "application/schema+json")
                    : TypedResults.NotFound())
            .WithSummary("JSON Schema of a stored record: field descriptions, and x-sensitivity (PERSONAL, IDENTIFIER) on personal data")
            .WithDescription("Validates or interprets a stored record or a view. x-sensitivity (personal data) is published only here, for the schemas GET /api/schemas names.")
            .WithName("getSchema")
            .WithTags(Tags.ReferenceData);
    }
}

internal sealed record CodeList(string Version, IReadOnlyList<CodeEntry> Codes);

internal sealed record SchemaList(int SchemaVersion, IReadOnlyList<string> Names);
