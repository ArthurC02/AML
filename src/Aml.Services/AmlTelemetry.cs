using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Aml.Services;

public static class AmlTelemetry
{
    public const string Name = "Aml";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    /// <summary>Analysis revisions, tagged with case result and execution status.</summary>
    public static readonly Counter<long> Analyses = Meter.CreateCounter<long>("aml.analyses");

    /// <summary>Batch job attempts, tagged with outcome (SUCCEEDED, RETRY, DEAD_LETTER) and error code.</summary>
    public static readonly Counter<long> JobAttempts = Meter.CreateCounter<long>("aml.job.attempts");

    public static readonly Histogram<double> AnalysisDuration = Meter.CreateHistogram<double>("aml.analysis.duration", "ms");

    /// <summary>Case workbooks issued, tagged with view (summary, full).</summary>
    public static readonly Counter<long> Workbooks = Meter.CreateCounter<long>("aml.workbooks");

    /// <summary>Time to build one Case workbook, reads included, tagged with view.</summary>
    public static readonly Histogram<double> WorkbookDuration = Meter.CreateHistogram<double>("aml.workbook.duration", "ms");

    /// <summary>Size of one Case workbook, tagged with view.</summary>
    public static readonly Histogram<long> WorkbookSize = Meter.CreateHistogram<long>("aml.workbook.size", "By");
}
