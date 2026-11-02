using Aml.Core.Cases;
using Aml.Core.Ports;
using Aml.Core.Subjects;
using Aml.Infra.Persistence;
using Aml.Infra.Persistence.Repositories;
using Aml.Infra.Txt;
using Aml.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

var db = new SqliteDatabase(builder.Configuration["Database:ConnectionString"] ?? "Data Source=aml.db");
db.Upgrade();
builder.Services.AddSingleton(db);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICaseStore, CaseStore>();
builder.Services.AddSingleton<CaseService>();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    foreach (var c in CaseService.Json.Converters) o.SerializerOptions.Converters.Add(c);
});

var app = builder.Build();
app.MapOpenApi();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "AML PoC"));

var cases = app.MapGroup("/api/cases").DisableAntiforgery(); // token-based API, no cookies

cases.MapPost("/", async Task<Results<Created<CaseResponse>, BadRequest<string>>> (
    [FromForm] string subjectRef, [FromForm] string triggerEventId, IFormFileCollection files, CaseService service, CancellationToken ct) =>
{
    var (input, error) = await ReadUploadAsync(files, ct);
    if (input is null) return TypedResults.BadRequest(error);
    var c = await service.CreateAsync(subjectRef, triggerEventId, input, ct);
    return TypedResults.Created($"/api/cases/{c.CaseId}", CaseResponse.From(c));
})
.WithSummary("Create a Case from the .txt export files and run the first analysis")
.WithDescription("Upload 案件資訊*.txt, 名單資料_<id>.txt, the customer or related-party files and any 新聞資訊_<id>.txt. Repeating the same triggerEventId and subjectRef returns the existing Case.");

cases.MapPost("/{caseId}/analyses", async Task<Results<Ok<CaseResponse>, NotFound, BadRequest<string>>> (
    string caseId, IFormFileCollection files, CaseService service, CancellationToken ct) =>
{
    var (input, error) = await ReadUploadAsync(files, ct);
    if (input is null) return TypedResults.BadRequest(error);
    var c = await service.ReanalyzeAsync(caseId, input, ct);
    return c is null ? TypedResults.NotFound() : TypedResults.Ok(CaseResponse.From(c));
})
.WithSummary("Run a new analysis revision with updated input files");

cases.MapGet("/{caseId}", async Task<Results<Ok<CaseResponse>, NotFound>> (string caseId, CaseService service, CancellationToken ct) =>
    await service.GetAsync(caseId, ct) is { } c ? TypedResults.Ok(CaseResponse.From(c)) : TypedResults.NotFound());

cases.MapGet("/{caseId}/analyses/{revision:int}", async Task<Results<ContentHttpResult, NotFound>> (string caseId, int revision, CaseService service, CancellationToken ct) =>
    await service.GetAnalysisAsync(caseId, revision, ct) is { } a
        ? TypedResults.Content(a.ResultJson, "application/json")
        : TypedResults.NotFound())
.WithSummary("Analysis result: hits, source conditions, evidence, identity decision and reason codes");

cases.MapGet("/{caseId}/explanation", async Task<Results<ContentHttpResult, NotFound>> (string caseId, CaseService service, CancellationToken ct) =>
    await service.GetExplanationAsync(caseId, ct) is { } json
        ? TypedResults.Content(json, "application/json")
        : TypedResults.NotFound())
.WithSummary("Reviewer and audit view of the effective revision: hit reason, comparison table, evidence, reason codes and a five-part summary");

app.Run();

// Saves the upload to a private temp folder (file names reduced to their last segment), reads it, deletes it.
static async Task<(CaseInput? Input, string? Error)> ReadUploadAsync(IFormFileCollection files, CancellationToken ct)
{
    const long maxBytes = 5 * 1024 * 1024;
    if (files.Count == 0) return (null, "No files uploaded");
    if (files.Any(f => !f.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || f.Length > maxBytes))
        return (null, "Only .txt files up to 5 MB are accepted");

    var folder = Directory.CreateTempSubdirectory("aml-upload-");
    try
    {
        foreach (var f in files)
        {
            await using var target = File.Create(Path.Combine(folder.FullName, Path.GetFileName(f.FileName)));
            await f.CopyToAsync(target, ct);
        }
        return (TxtCaseReader.Read(folder.FullName), null);
    }
    catch (Exception e) when (e is InvalidOperationException or FileNotFoundException or IndexOutOfRangeException or KeyNotFoundException)
    {
        return (null, "Input files are incomplete or not in the expected .txt layout");
    }
    finally
    {
        folder.Delete(recursive: true);
    }
}

internal sealed record CaseResponse(string CaseId, string SubjectRef, string TriggerEventId, CaseStatus Status, int CurrentRevision, int? EffectiveRevision)
{
    public static CaseResponse From(CaseRecord c) =>
        new(c.CaseId, c.SubjectRef, c.TriggerEventId, c.Status, c.CurrentRevision, c.EffectiveRevision);
}

public partial class Program;
