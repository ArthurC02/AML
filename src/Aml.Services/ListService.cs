using System.ComponentModel;
using System.Text.Json;
using Aml.Core.Cases;
using Aml.Core.Ports;
using Aml.Core.Retrieval;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Services;

public sealed record ProfileImport(string ProfileId, string ProfileKey, int Version, bool Changed);

public sealed record ProfileView(string ProfileId, string ProfileKey, int Version, int LatestVersion, string ContentHash, DateTimeOffset CapturedAt, SourceProfile Profile);

/// <summary>No identifier values and, of personal data, only names (ADR-0024); the whole Profile is at the full link.</summary>
public sealed record ProfileSummary(
    [property: Description("名單編號（不含 DJ: 前綴）")] string ProfileId,
    [property: Description("名單鍵（DJ:<名單編號>）")] string ProfileKey,
    [property: Description("此版本")] int Version,
    [property: Description("最新版本")] int LatestVersion,
    [property: Description("內容 SHA-256")] string ContentHash,
    [property: Description("擷取時間")] DateTimeOffset CapturedAt,
    [property: Description("記錄類型：Person 或組織、船舶等")] string RecordType,
    [property: Description("性別")] Gender? Gender,
    [property: Description("姓名與別名")][property: Sensitivity(Sensitivity.Personal)] IReadOnlyList<string> Names,
    [property: Description("國籍（ISO 3166-1 alpha-2）")] IReadOnlyList<string> Citizenship,
    [property: Description("名單類別")] IReadOnlyList<SourceCategory> Categories,
    [property: Description("關鍵字")] IReadOnlyList<string> Keywords,
    [property: Description("職務")] IReadOnlyList<ProfileRole> Roles,
    [property: Description("官方名單記錄")] OfficialListEntry? OfficialList,
    [property: Description("資料品質 finding")] IReadOnlyList<string> Findings,
    [property: Description("完整 Profile 的連結（view=full）")] string Full)
{
    public static ProfileSummary Of(ProfileView v) => new(v.ProfileId, v.ProfileKey, v.Version, v.LatestVersion, v.ContentHash, v.CapturedAt,
        v.Profile.RecordType, v.Profile.Gender, v.Profile.Names, v.Profile.Citizenship, v.Profile.Categories, v.Profile.Keywords,
        v.Profile.Roles, v.Profile.OfficialList, v.Profile.Findings, $"/api/profiles/{v.ProfileId}?version={v.Version}&view=full");
}

public enum CandidateSource { CaseTable, LatestInputProfileFiles }

public sealed record ReanalysisJob(string JobId, string CaseId, string ProfileId, string ProfileKey, int Version);

public sealed record ImportReport(IReadOnlyList<ProfileImport> Profiles, string? BatchId, IReadOnlyList<ReanalysisJob> QueuedReanalyses, IReadOnlyList<string> Rejected);

/// <summary>
/// List database and retrieval (operating architecture steps 3 and 5); customer data stays in the Cases. A changed Profile's
/// new version and the REANALYZE_CASE jobs of the Cases that used it are written in one transaction; history is never overwritten.
/// </summary>
public sealed class ListService(
    IListStore lists, ICaseStore caseStore, CaseService cases,
    Func<string, SourceProfile> parseProfile, Func<IReadOnlyDictionary<string, string>, CaseInput> readCase, TimeProvider clock,
    Func<string, string>? exportText = null)
{
    private sealed record CachedIndex(long Version, ProfileIndex Index);

    private CachedIndex? _index;

    public async Task<ImportReport> ImportAsync(IReadOnlyDictionary<string, string> files, CancellationToken ct)
    {
        var imported = new List<ProfileImport>();
        var (accepted, rejected) = SplitImportable(files);
        if (accepted.Count == 0) return new ImportReport(imported, null, [], rejected);

        // ponytail: Cases created between this read and the capture are not queued; they carry their own Profile files.
        var inputs = await caseStore.LatestInputsAsync(ct).ConfigureAwait(false);
        var used = HashCaseFilesOnceByProfile(inputs, accepted.Select(a => a.Id).ToHashSet(StringComparer.Ordinal));
        var batchId = Guid.CreateVersion7().ToString();
        var queued = new List<ReanalysisJob>();
        foreach (var (id, content) in accepted)
        {
            var hash = Hash(content);
            var affected = used[id].Where(f => f.Hash != hash).Select(f => f.CaseId).Distinct().ToList();
            List<ReanalysisJob> planned = [];
            var (current, changed, jobIds) = await lists.CaptureAsync(id, hash, content, clock.GetUtcNow(), version =>
            {
                planned = affected.Select(caseId => new ReanalysisJob(Guid.CreateVersion7().ToString(), caseId, id, ProfileKeys.KeyOf(id), version)).ToList();
                return planned.Select(j => new NewJob(j.JobId, BatchService.ReanalyzeCase, batchId,
                    $"REANALYZE:{j.CaseId}:{id}:{version}", JsonSerializer.Serialize(j, CaseJson.Options))).ToList();
            }, ct).ConfigureAwait(false);
            imported.Add(new ProfileImport(current.ProfileId, ProfileKeys.KeyOf(current.ProfileId), current.Version, changed));
            queued.AddRange(planned.Where(j => jobIds.Contains(j.JobId)));
        }
        return new ImportReport(imported, queued.Count > 0 ? batchId : null, queued, rejected);
    }

    private (List<(string Id, string Content)> Accepted, List<string> Rejected) SplitImportable(IReadOnlyDictionary<string, string> files)
    {
        var accepted = new List<(string Id, string Content)>();
        var rejected = new List<string>();
        foreach (var group in files.GroupBy(f => ProfileFiles.IdOf(f.Key)))
            if (IsOneFileDescribingItsProfile(group)) accepted.Add((group.Key!, group.First().Value));
            else rejected.AddRange(group.Select(f => f.Key));
        return (accepted, rejected);
    }

    private bool IsOneFileDescribingItsProfile(IGrouping<string?, KeyValuePair<string, string>> filesOfOneProfile) =>
        filesOfOneProfile.Key is { } profileId && filesOfOneProfile.Count() == 1 && Describes(profileId, filesOfOneProfile.First().Value);

    private ILookup<string, (string CaseId, string Hash)> HashCaseFilesOnceByProfile(
        IEnumerable<(string CaseId, IReadOnlyDictionary<string, string> Files)> inputs, HashSet<string> profileIds) =>
        inputs.SelectMany(i => i.Files.Select(f => (i.CaseId, ProfileId: ProfileFiles.IdOf(f.Key), f.Value)))
            .Where(f => f.ProfileId is not null && profileIds.Contains(f.ProfileId))
            .ToLookup(f => f.ProfileId!, f => (f.CaseId, Hash: Hash(f.Value)), StringComparer.Ordinal);

    public async Task<CaseRecord> ReanalyzeWithLatestProfilesAsync(string caseId, CancellationToken ct, Func<Task>? beforeWrite = null)
    {
        var input = await caseStore.LatestInputAsync(caseId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Case {caseId} has no stored input");
        var updated = new Dictionary<string, string>(input);
        foreach (var id in input.Keys.Select(ProfileFiles.IdOf).OfType<string>().Distinct())
            if (await lists.GetAsync(id, ct).ConfigureAwait(false) is { } latest && FilesOf(input, id).Any(f => Hash(f.Value) != latest.ContentHash))
                ReplaceEveryFileOfProfileWithListVersion(updated, input, id, latest.SourceText);
        var inputAlreadyMatchesListDatabase = updated.Count == input.Count && updated.All(f => input.TryGetValue(f.Key, out var v) && v == f.Value);
        if (inputAlreadyMatchesListDatabase)
            return await caseStore.GetAsync(caseId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Case {caseId} not found");
        return await cases.ReanalyzeAsync(caseId, readCase(updated), ct, updated, beforeWrite).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Case {caseId} not found");
    }

    private static void ReplaceEveryFileOfProfileWithListVersion(Dictionary<string, string> updated, IReadOnlyDictionary<string, string> input, string profileId, string listVersion)
    {
        foreach (var f in FilesOf(input, profileId)) updated.Remove(f.Key);
        updated[$"名單資料_{profileId}.txt"] = listVersion;
    }

    public async Task<IReadOnlyList<ListProfileRecord>> HistoryAsync(string profileId, CancellationToken ct) =>
        await lists.HistoryAsync(ProfileKeys.IdOf(profileId), ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<RetrievedCandidate>> SearchAsync(string name, int top, CancellationToken ct) =>
        (await IndexForCurrentListVersionAsync(ct).ConfigureAwait(false)).Search(name, top);

    public async Task<ProfileView?> GetProfileAsync(string profileId, int? version, CancellationToken ct)
    {
        var history = await lists.HistoryAsync(ProfileKeys.IdOf(profileId), ct).ConfigureAwait(false);
        var r = version is { } v ? history.FirstOrDefault(h => h.Version == v) : history.Count > 0 ? history[^1] : null;
        return r is null ? null : new ProfileView(r.ProfileId, ProfileKeys.KeyOf(r.ProfileId), r.Version, history[^1].Version, r.ContentHash, r.CapturedAt, parseProfile(r.SourceText));
    }

    public async Task<IReadOnlyList<RetrievedCandidate>?> CaseCandidatesAsync(string caseId, CandidateSource source, int top, CancellationToken ct)
    {
        var input = await caseStore.LatestInputAsync(caseId, ct).ConfigureAwait(false);
        if (input is null) return null;
        var all = source == CandidateSource.LatestInputProfileFiles
            ? input.Keys.Select(ProfileFiles.IdOf).OfType<string>().Distinct()
                .Select(p => new RetrievedCandidate(p, ProfileKeys.KeyOf(p), "", "PREVIOUS_CASE_INPUT", 1, RetrievalMode.PreviousLink)).ToList()
            : readCase(input).Rows.GroupBy(r => r.ProfileId)
                .Select(g => new RetrievedCandidate(g.Key, ProfileKeys.KeyOf(g.Key), g.First().MatchedName, "CASE_TABLE", 1, RetrievalMode.ControlledSet)).ToList();
        return all.Take(top).Select(c => c with { Truncated = all.Count > top }).ToList();
    }

    public async Task<MergeResult> MergesAsync(CancellationToken ct) =>
        ProfileMerge.Resolve((await lists.LatestAsync(ct).ConfigureAwait(false)).Select(r => parseProfile(r.SourceText)));

    private async Task<ProfileIndex> IndexForCurrentListVersionAsync(CancellationToken ct)
    {
        var version = await lists.VersionAsync(ct).ConfigureAwait(false);
        if (_index is { } cached && cached.Version == version) return cached.Index;
        var index = new ProfileIndex((await lists.LatestAsync(ct).ConfigureAwait(false)).Select(r => parseProfile(r.SourceText)));
        _index = new CachedIndex(version, index);
        return index;
    }

    private static IEnumerable<KeyValuePair<string, string>> FilesOf(IReadOnlyDictionary<string, string> files, string profileId) =>
        files.Where(f => ProfileFiles.IdOf(f.Key) == profileId);

#pragma warning disable CA1031 // content the Profile reader cannot read is rejected, whatever the reader threw
    private bool Describes(string profileId, string content)
    {
        try
        {
            return parseProfile(content).ProfileKey == ProfileKeys.KeyOf(profileId);
        }
        catch (Exception)
        {
            return false;
        }
    }
#pragma warning restore CA1031

    private string Hash(string content) => ProfileFiles.Hash(content, exportText);
}
