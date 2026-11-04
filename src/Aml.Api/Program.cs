using Aml.Api.Endpoints;
using Aml.Api.Workers;
using Aml.Core.Cases;
using Aml.Core.Ports;
using Aml.Core.Subjects;
using Aml.Infra.Html;
using Aml.Infra.Jobs;
using Aml.Infra.Persistence;
using Aml.Infra.Persistence.Repositories;
using Aml.Infra.Txt;
using Aml.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
// A batch carries many files as JSON text (CJK escapes to 6 bytes a character), so the body limit is above Kestrel's 30 MB default.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = builder.Configuration.GetValue<long?>("Api:MaxRequestBytes") ?? 100L * 1024 * 1024);

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
// The Profile content hash is taken on the export text, so an HTML page and its .txt export are one version.
builder.Services.AddSingleton<Func<string, string>>(c =>
    c.TrimStart().TrimStart('﻿').StartsWith('<') ? HtmlExport.ToExportText(c, HtmlExport.Kind.Profile) : c);
builder.Services.AddSingleton<ListService>();
builder.Services.AddHostedService<JobWorker>();
builder.Services.AddOpenApi();
// Every 4xx and 5xx is application/problem+json; a status without a handler-written problem gets its catalog type here.
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
{
    if (ctx.ProblemDetails.Type is null || ctx.ProblemDetails.Type.StartsWith("https://", StringComparison.Ordinal))
    {
        var code = Problems.CodeOf(ctx.ProblemDetails.Status ?? ctx.HttpContext.Response.StatusCode);
        (ctx.ProblemDetails.Type, ctx.ProblemDetails.Title) = (Problems.Type(code), Problems.Title(code));
    }
});

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
    foreach (var c in CaseJson.Options.Converters) o.SerializerOptions.Converters.Add(c);
});

var app = builder.Build();
app.UseExceptionHandler(new ExceptionHandlerOptions { StatusCodeSelector = e => e is BadHttpRequestException b ? b.StatusCode : StatusCodes.Status500InternalServerError });
app.UseStatusCodePages();
app.MapHealthChecks("/health");
app.MapOpenApi();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "AML PoC"));

app.MapCases();
app.MapBatches();
app.MapProfiles();
app.MapCodes();

app.Run();

public partial class Program;
