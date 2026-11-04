namespace Aml.Core.Ports;

/// <summary>One captured version of a Profile's source text. Earlier versions are kept, never overwritten.</summary>
public sealed record ListProfileRecord(string ProfileId, int Version, string ContentHash, string SourceText, DateTimeOffset CapturedAt);

/// <summary>The list database of structured Profiles (operating architecture step 3). Holds no customer data.</summary>
public interface IListStore
{
    /// <summary>
    /// Stores a new version when the content hash differs from the current one and, in the same transaction, queues the jobs
    /// <paramref name="jobsFor"/> returns for that version. Returns the current record, whether it changed, and the ids of the
    /// jobs queued (a job whose idempotency key is already queued is left out).
    /// </summary>
    Task<(ListProfileRecord Current, bool Changed, IReadOnlyList<string> QueuedJobIds)> CaptureAsync(
        string profileId, string contentHash, string sourceText, DateTimeOffset now, Func<int, IReadOnlyList<NewJob>> jobsFor, CancellationToken ct);

    Task<ListProfileRecord?> GetAsync(string profileId, CancellationToken ct);

    Task<IReadOnlyList<ListProfileRecord>> LatestAsync(CancellationToken ct);

    Task<IReadOnlyList<ListProfileRecord>> HistoryAsync(string profileId, CancellationToken ct);

    /// <summary>Changes whenever a Profile version is captured (the list database is append-only).</summary>
    Task<long> VersionAsync(CancellationToken ct);
}
