using System.ComponentModel;
using Aml.Core.Identity;
using Aml.Core.Screening;
using Aml.Core.Retrieval;

namespace Aml.Core.Cases;

public sealed record PairSnapshot(string ProfileKey, ScreeningAssessment PairResult, IdentityDecision IdentityDecision, IReadOnlyList<string> ReasonCodes);

public enum PairChangeKind { Added, Removed, ResultChanged, IdentityChanged, ReasonsChanged }

public sealed record PairChange(
    [property: Description("名單編號")] string ProfileId,
    [property: Description("名單鍵")] string ProfileKey,
    [property: Description("變更種類")] PairChangeKind Kind,
    [property: Description("變更前")] string? From,
    [property: Description("變更後")] string? To);

// GC-08 Re-adjudication: every difference is listed; none after an unchanged re-analysis is the Stability signal.
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
            PairChange Change(PairChangeKind kind, string? from, string? to) => new(ProfileKeys.IdOf(key), key, kind, from, to);
            if (!before.TryGetValue(key, out var was)) { changes.Add(Change(PairChangeKind.Added, null, CaseJson.Code(now.PairResult))); continue; }
            if (was.PairResult != now.PairResult) changes.Add(Change(PairChangeKind.ResultChanged, CaseJson.Code(was.PairResult), CaseJson.Code(now.PairResult)));
            if (was.IdentityDecision != now.IdentityDecision) changes.Add(Change(PairChangeKind.IdentityChanged, CaseJson.Code(was.IdentityDecision), CaseJson.Code(now.IdentityDecision)));
            if (!was.ReasonCodes.SequenceEqual(now.ReasonCodes)) changes.Add(Change(PairChangeKind.ReasonsChanged, string.Join(",", was.ReasonCodes), string.Join(",", now.ReasonCodes)));
        }
        foreach (var (key, was) in before)
            if (!after.ContainsKey(key)) changes.Add(new(ProfileKeys.IdOf(key), key, PairChangeKind.Removed, CaseJson.Code(was.PairResult), null));
        return changes;
    }
}
