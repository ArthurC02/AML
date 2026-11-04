using Aml.Core.Cases;

namespace Aml.Core.Ports;

public interface ICaseStore
{
    /// <summary>Inserts the Case; when (triggerEventId, subjectRef) already exists, returns the existing one.</summary>
    Task<CaseRecord> CreateAsync(CaseRecord record, CancellationToken ct);

    Task<CaseRecord?> GetAsync(string caseId, CancellationToken ct);

    /// <summary>
    /// Stores the revision with the input files (name → content) it was run on and moves the Case to it, all in one transaction.
    /// False when the revision number is already taken (nothing is stored) or a newer revision is already current (this one stays
    /// stored as history). Revisions are numbered current + 1, so the second case needs a gap and does not occur in practice.
    /// </summary>
    Task<bool> AcceptAnalysisAsync(AnalysisRecord analysis, CaseStatus status, IReadOnlyDictionary<string, string>? files, CancellationToken ct);

    Task<AnalysisRecord?> GetAnalysisAsync(string caseId, int revision, CancellationToken ct);

    /// <summary>The latest stored input of one Case, or null when it has none.</summary>
    Task<IReadOnlyDictionary<string, string>?> LatestInputAsync(string caseId, CancellationToken ct);

    /// <summary>Each Case's latest stored input.</summary>
    Task<IReadOnlyList<(string CaseId, int Revision, IReadOnlyDictionary<string, string> Files)>> LatestInputsAsync(CancellationToken ct);
}
