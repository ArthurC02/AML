namespace Aml.Core.Ports;

/// <summary>One captured version of a Profile's source text. Earlier versions are kept, never overwritten.</summary>
public sealed record ListProfileRecord(string ProfileId, int Version, string ContentHash, string SourceText, DateTimeOffset CapturedAt);

/// <summary>The list database of structured Profiles (operating architecture step 3). Holds no customer data.</summary>
public interface IListStore
{
    /// <summary>Stores a new version when the content hash differs from the current one; returns the current record and whether it changed.</summary>
    Task<(ListProfileRecord Current, bool Changed)> CaptureAsync(string profileId, string contentHash, string sourceText, DateTimeOffset now, CancellationToken ct);

    Task<ListProfileRecord?> GetAsync(string profileId, CancellationToken ct);

    Task<IReadOnlyList<ListProfileRecord>> LatestAsync(CancellationToken ct);

    Task<IReadOnlyList<ListProfileRecord>> HistoryAsync(string profileId, CancellationToken ct);
}
