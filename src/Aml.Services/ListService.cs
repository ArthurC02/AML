using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aml.Core.Ports;
using Aml.Core.Retrieval;
using Aml.Core.Subjects;

namespace Aml.Services;

public sealed record ProfileImport(string ProfileId, int Version, bool Changed);

/// <summary>Captured versions, and the Cases a changed Profile re-analyzed into a new revision.</summary>
public sealed record ImportReport(IReadOnlyList<ProfileImport> Profiles, IReadOnlyList<string> ReanalyzedCases, IReadOnlyList<string> Rejected);

/// <summary>
/// List database and retrieval (operating architecture steps 3 and 5). Holds list data only: customer data stays in the
/// Cases. A Profile whose content changes gets a new version, and every Case whose latest input used it is re-analyzed
/// into a new revision; history is never overwritten.
/// </summary>
public sealed partial class ListService(
    IListStore lists, ICaseStore caseStore, CaseService cases,
    Func<string, SourceProfile> parseProfile, Func<IReadOnlyDictionary<string, string>, CaseInput> readCase, TimeProvider clock)
{
    [GeneratedRegex(@"^名單資料_(\d+)\.(txt|html?)$")]
    private static partial Regex ProfileFile();

    public async Task<ImportReport> ImportAsync(IReadOnlyDictionary<string, string> files, CancellationToken ct)
    {
        var imported = new List<ProfileImport>();
        var rejected = new List<string>();
        foreach (var (name, content) in files)
        {
            if (ProfileFile().Match(Path.GetFileName(name)) is not { Success: true } m) { rejected.Add(name); continue; }
            var (current, changed) = await lists.CaptureAsync(m.Groups[1].Value, Hash(content), content, clock.GetUtcNow(), ct).ConfigureAwait(false);
            imported.Add(new ProfileImport(current.ProfileId, current.Version, changed));
        }

        var changedIds = imported.Where(i => i.Changed).Select(i => i.ProfileId).ToHashSet();
        var reanalyzed = new List<string>();
        if (changedIds.Count > 0)
            foreach (var (caseId, _, input) in await caseStore.LatestInputsAsync(ct).ConfigureAwait(false))
            {
                var updated = new Dictionary<string, string>(input);
                var touched = false;
                foreach (var (name, content) in input)
                    if (ProfileFile().Match(name) is { Success: true } m && changedIds.Contains(m.Groups[1].Value)
                        && await lists.GetAsync(m.Groups[1].Value, ct).ConfigureAwait(false) is { } latest && Hash(content) != latest.ContentHash)
                    {
                        updated[name] = latest.SourceText;
                        touched = true;
                    }
                if (!touched) continue;
                await cases.ReanalyzeAsync(caseId, readCase(updated), ct, updated).ConfigureAwait(false);
                reanalyzed.Add(caseId);
            }
        return new ImportReport(imported, reanalyzed, rejected);
    }

    public async Task<IReadOnlyList<ListProfileRecord>> HistoryAsync(string profileId, CancellationToken ct) =>
        await lists.HistoryAsync(profileId, ct).ConfigureAwait(false);

    /// <summary>
    /// Candidates by retrieval mode: INDEX_SEARCH by name over the list database, DIRECT_REFERENCE by Profile ID,
    /// CONTROLLED_SET from a Case's own candidate table, PREVIOUS_LINK from the Profiles a Case's latest input used.
    /// </summary>
    public async Task<IReadOnlyList<RetrievedCandidate>> RetrieveAsync(RetrievalMode mode, string? name, string? profileId, string? caseId, int top, CancellationToken ct)
    {
        switch (mode)
        {
            case RetrievalMode.IndexSearch when name is not null:
                return (await IndexAsync(ct).ConfigureAwait(false)).Search(name, top);
            case RetrievalMode.DirectReference when profileId is not null:
                return await lists.GetAsync(profileId, ct).ConfigureAwait(false) is { } r
                    ? [new RetrievedCandidate("DJ:" + r.ProfileId, parseProfile(r.SourceText).Names is [var first, ..] ? first : "", "PROFILE_ID", 1, mode)]
                    : [];
            case RetrievalMode.ControlledSet or RetrievalMode.PreviousLink when caseId is not null:
                var input = (await caseStore.LatestInputsAsync(ct).ConfigureAwait(false)).FirstOrDefault(i => i.CaseId == caseId).Files;
                if (input is null) return [];
                if (mode == RetrievalMode.PreviousLink)
                    return input.Keys.Select(k => ProfileFile().Match(k)).Where(m => m.Success)
                        .Select(m => new RetrievedCandidate("DJ:" + m.Groups[1].Value, "", "PREVIOUS_CASE_INPUT", 1, mode)).Take(top).ToList();
                return readCase(input).Rows.GroupBy(r => r.ProfileId)
                    .Select(g => new RetrievedCandidate("DJ:" + g.Key, g.First().MatchedName, "CASE_TABLE", 1, mode)).Take(top).ToList();
            default:
                throw new ArgumentException($"{mode} needs {(mode == RetrievalMode.IndexSearch ? "name" : mode == RetrievalMode.DirectReference ? "profileId" : "caseId")}");
        }
    }

    /// <summary>Profiles in the list database that describe the same person (shared document key or recorded source link).</summary>
    public async Task<IReadOnlyList<MergedProfile>> MergesAsync(CancellationToken ct) =>
        ProfileMerge.Group((await lists.LatestAsync(ct).ConfigureAwait(false)).Select(r => parseProfile(r.SourceText)));

    // ponytail: rebuilt from the list database on every search; cache it per list version when volume grows.
    private async Task<ProfileIndex> IndexAsync(CancellationToken ct) =>
        new((await lists.LatestAsync(ct).ConfigureAwait(false)).Select(r => parseProfile(r.SourceText)));

    private static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
