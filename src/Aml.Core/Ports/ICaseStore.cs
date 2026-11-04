using Aml.Core.Cases;

namespace Aml.Core.Ports;

public interface ICaseStore
{
    /// <summary>Inserts the Case; when (triggerEventId, subjectRef) already exists, returns the existing one.</summary>
    Task<CaseRecord> CreateAsync(CaseRecord record, CancellationToken ct);

    Task<CaseRecord?> GetAsync(string caseId, CancellationToken ct);

    /// <summary>Stores the revision and moves the Case to it atomically. False when a newer revision is already current.</summary>
    Task<bool> AcceptAnalysisAsync(AnalysisRecord analysis, CaseStatus status, CancellationToken ct);

    Task<AnalysisRecord?> GetAnalysisAsync(string caseId, int revision, CancellationToken ct);

    /// <summary>The input files (name → content) an analysis revision was run on.</summary>
    Task SaveInputAsync(string caseId, int revision, IReadOnlyDictionary<string, string> files, DateTimeOffset now, CancellationToken ct);

    /// <summary>Each Case's latest stored input.</summary>
    Task<IReadOnlyList<(string CaseId, int Revision, IReadOnlyDictionary<string, string> Files)>> LatestInputsAsync(CancellationToken ct);
}
