using Aml.Core.Cases;

namespace Aml.Core.Ports;

public interface ICaseStore
{
    /// <summary>Inserts the Case; when (triggerEventId, subjectRef) already exists, returns the existing one.</summary>
    Task<CaseRecord> CreateAsync(CaseRecord record, CancellationToken ct);

    Task<CaseRecord?> GetAsync(string caseId, CancellationToken ct);

    /// <summary>
    /// Stores the revision and its input files (name → content) and moves the Case to it, in one transaction. False when the
    /// revision number is taken (nothing is stored) or a newer revision is already current (this one stays as history).
    /// </summary>
    Task<bool> AcceptAnalysisAsync(AnalysisRecord analysis, CaseStatus status, IReadOnlyDictionary<string, string>? files, CancellationToken ct);

    Task<AnalysisRecord?> GetAnalysisAsync(string caseId, int revision, CancellationToken ct);

    /// <summary>Every revision of one Case, oldest first; empty for an unknown Case.</summary>
    Task<IReadOnlyList<RevisionLine>> RevisionsAsync(string caseId, CancellationToken ct);

    /// <summary>Null filters match all; ordered by (created at, id), starting after <paramref name="after"/>.</summary>
    Task<IReadOnlyList<CaseRecord>> SearchAsync(string? subjectRef, string? triggerEventId, (DateTimeOffset At, string Id)? after, int limit, CancellationToken ct);

    /// <summary>Null when the Case has no stored input.</summary>
    Task<IReadOnlyDictionary<string, string>?> LatestInputAsync(string caseId, CancellationToken ct);

    Task<IReadOnlyList<(string CaseId, IReadOnlyDictionary<string, string> Files)>> LatestInputsAsync(CancellationToken ct);
}
