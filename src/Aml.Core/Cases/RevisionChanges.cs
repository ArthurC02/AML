using System.ComponentModel;
using Aml.Core.Identity;
using Aml.Core.Screening;
using Aml.Core.Retrieval;

namespace Aml.Core.Cases;

/// <summary>What a pair looked like in one revision; enough to tell what a re-analysis changed.</summary>
public sealed record PairSnapshot(string ProfileKey, ScreeningAssessment PairResult, IdentityDecision IdentityDecision, IReadOnlyList<string> ReasonCodes);

public enum PairChangeKind { Added, Removed, ResultChanged, IdentityChanged, ReasonsChanged }

public sealed record PairChange(
    [property: Description("名單編號")] string ProfileId,
    [property: Description("名單鍵")] string ProfileKey,
    [property: Description("變更種類")] PairChangeKind Kind,
    [property: Description("變更前")] string? From,
    [property: Description("變更後")] string? To);

/// <summary>
/// Re-adjudication (GC-08 capability): a new revision never silently rewrites the previous one; every difference is listed.
/// An empty list on a re-analysis with unchanged inputs is the Re-adjudication Stability signal.
/// </summary>
public static class RevisionChanges
{
    public static IReadOnlyList<PairSnapshot> Snapshot(CaseAnalysis a) =>
        a.Hits.Select(h => new PairSnapshot(h.Profile.ProfileKey, h.PairResult, h.Identity.Decision,
            h.Identity.ReasonCodes.Concat(h.PairReasonCodes).ToList())).ToList();

    public static IReadOnlyList<PairChange> Compare(IReadOnlyList<PairSnapshot> previous, IReadOnlyList<PairSnapshot> current)
    {
        var changes = new List<PairChange>();
        var before = previous.ToDictionary(p => p.ProfileKey);
        var after = current.ToDictionary(p => p.ProfileKey);

        foreach (var (key, now) in after)
        {
            if (!before.TryGetValue(key, out var was)) { changes.Add(new(ProfileKeys.IdOf(key), key, PairChangeKind.Added, null, CaseJson.Code(now.PairResult))); continue; }
            if (was.PairResult != now.PairResult) changes.Add(new(ProfileKeys.IdOf(key), key, PairChangeKind.ResultChanged, CaseJson.Code(was.PairResult), CaseJson.Code(now.PairResult)));
            if (was.IdentityDecision != now.IdentityDecision) changes.Add(new(ProfileKeys.IdOf(key), key, PairChangeKind.IdentityChanged, CaseJson.Code(was.IdentityDecision), CaseJson.Code(now.IdentityDecision)));
            if (!was.ReasonCodes.SequenceEqual(now.ReasonCodes))
                changes.Add(new(ProfileKeys.IdOf(key), key, PairChangeKind.ReasonsChanged, string.Join(",", was.ReasonCodes), string.Join(",", now.ReasonCodes)));
        }
        foreach (var (key, was) in before)
            if (!after.ContainsKey(key)) changes.Add(new(ProfileKeys.IdOf(key), key, PairChangeKind.Removed, CaseJson.Code(was.PairResult), null));
        return changes;
    }
}
