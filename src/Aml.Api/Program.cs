using Aml.Api.Endpoints;
using Aml.Api.Workers;
using Aml.Core.Cases;
using Aml.Core.Codes;
using Aml.Core.Ports;
using Aml.Core.Subjects;
using Aml.Infra.Html;
using Aml.Infra.Jobs;
using Aml.Infra.Persistence;
using Aml.Infra.Persistence.Repositories;
using Aml.Infra.Txt;
using Aml.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// A batch carries many files as JSON text (CJK escapes to 6 bytes a character), so the body limit is above Kestrel's 30 MB default.
const long DefaultMaxRequestBytes = 100L * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = builder.Configuration.GetValue<long?>("Api:MaxRequestBytes") ?? DefaultMaxRequestBytes);
var workbookConcurrency = builder.Configuration.GetValue<int?>("Api:WorkbookConcurrency") ?? 16;
var workbookQueue = builder.Configuration.GetValue<int?>("Api:WorkbookQueue") ?? 64;
// Checked at the start, before the database is touched, not on the first workbook request: a limit that cannot work
// would turn every one into a 500.
if (workbookConcurrency < 1 || workbookQueue < 0)
    throw new InvalidOperationException("Api:WorkbookConcurrency must be at least 1 and Api:WorkbookQueue at least 0.");

// The hosting "Request starting/finished" lines carry the full URL, and a query (subjectRef=, a searched name) is personal
// data: they are kept out of the default log level. Configuration (Logging:LogLevel) still overrides this.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
// An upload file up to the accepted size stays in memory: a temp file on disk would hold customer data in plain text and
// outlive a crash. A larger file is still buffered to disk until it is refused.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MemoryBufferThreshold = Uploads.MaxBytes);

var db = new SqliteDatabase(builder.Configuration["Database:ConnectionString"] ?? "Data Source=aml.db");
db.Upgrade();
builder.Services.AddSingleton(db);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICaseStore, CaseStore>();
builder.Services.AddSingleton<CaseService>();
builder.Services.AddSingleton<IJobStore, JobStore>();
var batchOptions = builder.Configuration.GetSection("Batch").Get<BatchOptions>() ?? new BatchOptions();
batchOptions.Validate();
builder.Services.AddSingleton(batchOptions);
// Long enough for running jobs to settle (JobWorker drains them); the host's default of 30 s is shorter than one job's deadline.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = batchOptions.ShutdownTimeout);
builder.Services.AddSingleton<Func<IReadOnlyDictionary<string, string>, CaseInput>>(TxtCaseReader.ReadFiles);
builder.Services.AddSingleton<BatchService>();
builder.Services.AddSingleton<IListStore, ListStore>();
builder.Services.AddSingleton<Func<string, SourceProfile>>(TxtCaseReader.ParseProfile);
builder.Services.AddSingleton<Func<string, string>>(TxtCaseReader.ProfileExportText);
builder.Services.AddSingleton<ListService>();
builder.Services.AddHostedService<JobWorker>();
// A null field is left out of the views and revision records (CaseJson), so a property whose C# type can be null is never required.
builder.Services.AddOpenApi(o => o.AddSchemaTransformer((schema, ctx, ct) =>
{
    foreach (var property in ctx.JsonTypeInfo.Properties)
        if (property.IsGetNullable) schema.Required?.Remove(property.Name);
    // An enum component is shared: a parameter default of whichever record reached it first is not the enum's default.
    if (ctx.JsonTypeInfo.Type.IsEnum) schema.Default = null;
    return Task.CompletedTask;
}));
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
{
    var writtenByFramework = ctx.ProblemDetails.Type is null || ctx.ProblemDetails.Type.StartsWith("https://", StringComparison.Ordinal);
    if (writtenByFramework)
    {
        var status = ctx.ProblemDetails.Status ?? ctx.HttpContext.Response.StatusCode;
        var code = Problems.CodeOf(status);
        (ctx.ProblemDetails.Type, ctx.ProblemDetails.Title) = (Problems.Type(code), Problems.Title(code));
        ctx.ProblemDetails.Detail ??= CodeCatalog.Find(code)?.Description;
        ctx.ProblemDetails.Extensions.TryAdd("errors", new[] { new { method = ctx.HttpContext.Request.Method, path = ctx.HttpContext.Request.Path.Value } });
        ctx.ProblemDetails.Extensions.TryAdd("fix", Problems.Fix(status));
    }
});
builder.Services.AddExceptionHandler<CaseInputProblemHandler>();
// Workbooks are built a few at a time and the rest wait in a short queue; past it the answer is 429. Per instance, not per user:
// a per-user limit needs authentication.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "1";
        return ValueTask.CompletedTask;
    };
    o.AddConcurrencyLimiter(CasesEndpoints.WorkbookLimit, l =>
    {
        l.PermitLimit = workbookConcurrency;
        l.QueueLimit = workbookQueue;
        l.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
    });
});

builder.Services.AddHealthChecks().AddAsyncCheck("database", async ct =>
{
    await using var c = await db.OpenAsync(ct);
    await using var cmd = c.CreateCommand();
    cmd.CommandText = db.Sql["System.Ping"];
    await cmd.ExecuteScalarAsync(ct);
    return HealthCheckResult.Healthy();
});

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
// API answers hold personal data: no cache keeps them, and no browser reads them as another type. Set as the response starts,
// since the exception handler clears headers set before it.
app.Use((context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Task.CompletedTask;
        });
    return next(context);
});
app.UseExceptionHandler(new ExceptionHandlerOptions { StatusCodeSelector = e => e is BadHttpRequestException b ? b.StatusCode : StatusCodes.Status500InternalServerError });
app.UseStatusCodePages();
app.UseRateLimiter(); // after UseStatusCodePages, so a 429 gets a problem body
app.MapHealthChecks("/health");
app.MapOpenApi();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "AML PoC"));

app.MapCases();
app.MapBatches();
app.MapProfiles();
app.MapCodes();

app.Run();

public partial class Program;
