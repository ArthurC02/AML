using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Aml.Services;

/// <summary>Domain traces and metrics under the name "Aml"; exported by OpenTelemetry in the Api host.</summary>
public static class AmlTelemetry
{
    public const string Name = "Aml";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    /// <summary>Analysis revisions, tagged with case result and execution status.</summary>
    public static readonly Counter<long> Analyses = Meter.CreateCounter<long>("aml.analyses");

    /// <summary>Batch job attempts, tagged with outcome (SUCCEEDED, RETRY, DEAD_LETTER) and error code.</summary>
    public static readonly Counter<long> JobAttempts = Meter.CreateCounter<long>("aml.job.attempts");

    /// <summary>Analysis latency in milliseconds.</summary>
    public static readonly Histogram<double> AnalysisDuration = Meter.CreateHistogram<double>("aml.analysis.duration", "ms");
}
