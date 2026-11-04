using Aml.Api.Workers;
using Aml.Core.Cases;
using Aml.Core.Ports;
using Aml.Core.Retrieval;
using Aml.Core.Subjects;
using Aml.Infra.Jobs;
using Aml.Infra.Persistence;
using Aml.Infra.Persistence.Repositories;
using Aml.Infra.Txt;
using Aml.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var db = new SqliteDatabase(builder.Configuration["Database:ConnectionString"] ?? "Data Source=aml.db");
db.Upgrade();
builder.Services.AddSingleton(db);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICaseStore, CaseStore>();
builder.Services.AddSingleton<CaseService>();
builder.Services.AddSingleton<IJobStore, JobStore>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Batch").Get<BatchOptions>() ?? new BatchOptions());
builder.Services.AddSingleton<Func<IReadOnlyDictionary<string, string>, CaseInput>>(TxtCaseReader.ReadFiles);
builder.Services.AddSingleton<BatchService>();
builder.Services.AddSingleton<IListStore, ListStore>();
builder.Services.AddSingleton<Func<string, SourceProfile>>(TxtCaseReader.ParseProfile);
builder.Services.AddSingleton<ListService>();
builder.Services.AddHostedService<JobWorker>();
builder.Services.AddOpenApi();

// Liveness and the database round-trip (System.Ping).
builder.Services.AddHealthChecks().AddAsyncCheck("database", async ct =>
{
    await using var c = await db.OpenAsync(ct);
    await using var cmd = c.CreateCommand();
    cmd.CommandText = db.Sql["System.Ping"];
    await cmd.ExecuteScalarAsync(ct);
    return HealthCheckResult.Healthy();
});

// Traces, metrics and logs; exported over OTLP only when OTEL_EXPORTER_OTLP_ENDPOINT is set.
var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("aml-api"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddSource(AmlTelemetry.Name))
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddMeter(AmlTelemetry.Name))
    .WithLogging();
if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) otel.UseOtlpExporter();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    foreach (var c in CaseService.Json.Converters) o.SerializerOptions.Converters.Add(c);
});

var app = builder.Build();
app.MapHealthChecks("/health");
app.MapOpenApi();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "AML PoC"));

var cases = app.MapGroup("/api/cases").DisableAntiforgery(); // token-based API, no cookies

cases.MapPost("/", async Task<Results<Created<CaseResponse>, BadRequest<string>>> (
    [FromForm] string subjectRef, [FromForm] string triggerEventId, IFormFileCollection files, CaseService service, CancellationToken ct) =>
{
    var (input, contents, error) = await ReadUploadAsync(files, ct);
    if (input is null) return TypedResults.BadRequest(error);
    var c = await service.CreateAsync(subjectRef, triggerEventId, input, ct, contents);
    return TypedResults.Created($"/api/cases/{c.CaseId}", CaseResponse.From(c));
})
.WithSummary("Create a Case from the .txt export files (or the same pages as HTML) and run the first analysis")
.WithDescription("Upload 案件資訊*.txt, 名單資料_<id>.txt, the customer or related-party files and any 新聞資訊_<id>.txt. Repeating the same triggerEventId and subjectRef returns the existing Case.");

cases.MapPost("/{caseId}/analyses", async Task<Results<Ok<CaseResponse>, NotFound, BadRequest<string>>> (
    string caseId, IFormFileCollection files, CaseService service, CancellationToken ct) =>
{
    var (input, contents, error) = await ReadUploadAsync(files, ct);
    if (input is null) return TypedResults.BadRequest(error);
    var c = await service.ReanalyzeAsync(caseId, input, ct, contents);
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

var batches = app.MapGroup("/api/batches");

batches.MapPost("/", async Task<Results<Accepted<BatchSubmitted>, BadRequest<string>>> (BatchItemRequest[] items, BatchService service, CancellationToken ct) =>
{
    const int maxBytes = 5 * 1024 * 1024;
    if (items.Length == 0) return TypedResults.BadRequest("No items");
    if (items.Any(i => i.Files is null || i.Files.Count == 0 || i.Files.Any(f => !Accepted(f.Key) || f.Value.Length > maxBytes)))
        return TypedResults.BadRequest("Every item needs .txt or .html files up to 5 MB each");
    var b = await service.SubmitAsync(items, ct);
    return TypedResults.Accepted($"/api/batches/{b.BatchId}", b);
})
.WithSummary("Queue many Cases for analysis")
.WithDescription("Each item has subjectRef, triggerEventId and files (file name → text content), the same inputs as POST /api/cases. Items already queued under the same triggerEventId and subjectRef are listed as duplicates and not queued again.");

batches.MapGet("/{batchId}", async Task<Results<Ok<BatchStatus>, NotFound>> (string batchId, BatchService service, CancellationToken ct) =>
    await service.GetAsync(batchId, ct) is { } b ? TypedResults.Ok(b) : TypedResults.NotFound())
.WithSummary("Batch progress: counts per status and each item with its Case, revision, attempts and error code");

batches.MapPost("/{batchId}/rerun", async (string batchId, BatchService service, CancellationToken ct) =>
    TypedResults.Ok(new { requeued = await service.RerunAsync(batchId, ct) }))
.WithSummary("Operator re-run: dead-lettered items of the batch go back to the queue with a fresh retry budget");

var lists = app.MapGroup("/api/lists").DisableAntiforgery();

lists.MapPost("/profiles", async Task<Results<Ok<ImportReport>, BadRequest<string>>> (IFormFileCollection files, ListService service, CancellationToken ct) =>
{
    var (contents, error) = await ReadFilesAsync(files, ct);
    return contents is null ? TypedResults.BadRequest(error) : TypedResults.Ok(await service.ImportAsync(contents, ct));
})
.WithSummary("Capture Profiles into the list database; a changed Profile re-analyzes the Cases that used it")
.WithDescription("Upload 名單資料_<id>.txt (or .html). An unchanged file keeps its version; a changed one becomes a new version, the old one is kept, and every Case whose latest input used it gets a new analysis revision.");

lists.MapGet("/profiles/{profileId}/versions", async (string profileId, ListService service, CancellationToken ct) =>
    TypedResults.Ok((await service.HistoryAsync(profileId, ct)).Select(v => new { v.ProfileId, v.Version, v.ContentHash, v.CapturedAt })))
.WithSummary("Every captured version of one Profile, with its content hash");

lists.MapGet("/candidates", async Task<Results<Ok<IReadOnlyList<RetrievedCandidate>>, BadRequest<string>>> (
    RetrievalMode mode, string? name, string? profileId, string? caseId, int? top, ListService service, CancellationToken ct) =>
{
    try
    {
        return TypedResults.Ok(await service.RetrieveAsync(mode, name, profileId, caseId, Math.Clamp(top ?? 10, 1, 100), ct));
    }
    catch (ArgumentException e)
    {
        return TypedResults.BadRequest(e.Message);
    }
})
.WithSummary("Retrieve candidate Profiles: INDEX_SEARCH (name), DIRECT_REFERENCE (profileId), CONTROLLED_SET or PREVIOUS_LINK (caseId)")
.WithDescription("Counted by Profile, not alias row. The score ranks candidates only; it is never an identity probability.");

lists.MapGet("/merges", async (ListService service, CancellationToken ct) => TypedResults.Ok(await service.MergesAsync(ct)))
.WithSummary("Profiles that describe the same person: shared document key or recorded source link, never name similarity");

app.Run();

static bool Accepted(string name) => Path.GetExtension(name).ToLowerInvariant() is ".txt" or ".html" or ".htm";

// Reads uploaded files in memory (file names reduced to their last segment).
static async Task<(Dictionary<string, string>? Contents, string? Error)> ReadFilesAsync(IFormFileCollection files, CancellationToken ct)
{
    const long maxBytes = 5 * 1024 * 1024;
    if (files.Count == 0) return (null, "No files uploaded");
    if (files.Any(f => !Accepted(f.FileName) || f.Length > maxBytes))
        return (null, "Only .txt or .html files up to 5 MB are accepted");
    var contents = new Dictionary<string, string>();
    foreach (var f in files)
    {
        using var reader = new StreamReader(f.OpenReadStream());
        contents[Path.GetFileName(f.FileName)] = await reader.ReadToEndAsync(ct);
    }
    return (contents, null);
}

// Reads the upload and parses it through TxtCaseReader.ReadFiles; the contents are kept with the revision.
static async Task<(CaseInput? Input, Dictionary<string, string>? Contents, string? Error)> ReadUploadAsync(IFormFileCollection files, CancellationToken ct)
{
    var (contents, error) = await ReadFilesAsync(files, ct);
    if (contents is null) return (null, null, error);
    try
    {
        return (TxtCaseReader.ReadFiles(contents), contents, null);
    }
    catch (Exception e) when (TxtCaseReader.IsInputError(e))
    {
        return (null, null, "Input files are incomplete or not in the expected .txt layout");
    }
}

internal sealed record CaseResponse(string CaseId, string SubjectRef, string TriggerEventId, CaseStatus Status, int CurrentRevision, int? EffectiveRevision)
{
    public static CaseResponse From(CaseRecord c) =>
        new(c.CaseId, c.SubjectRef, c.TriggerEventId, c.Status, c.CurrentRevision, c.EffectiveRevision);
}

public partial class Program;
