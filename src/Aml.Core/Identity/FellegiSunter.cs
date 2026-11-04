using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Identity;

/// <summary>One group's Fellegi-Sunter weight: log2(m/u) on agreement, log2((1−m)/(1−u)) on disagreement.</summary>
public sealed record GroupWeight(EvidenceGroup Group, double M, double U, double Weight);

public sealed record ShadowScore(string ModelVersion, double TotalWeight, IReadOnlyList<GroupWeight> Groups);

/// <summary>
/// Fellegi-Sunter match weight, computed in shadow for Gate D calibration (ADR-0019). It never changes a decision, is not
/// part of the identity result or the explanation, and is never shown as a probability (ADR-0018).
/// u follows the support-strength table; m is one placeholder agreement rate for every group. Agreeing with any one of
/// k Profile DOBs is k times as likely by chance, so the DOB u is multiplied by k.
/// ponytail: m and u are uncalibrated; estimate them (e.g. EM over a representative labelled set) before any use at Gate D.
/// </summary>
public static class FellegiSunter
{
    public const string ModelVersion = "fs-shadow-0.1";
    public const double M = 0.95;

    // Chance of an accidental agreement per strength (identity-adjudication.md#support-strength).
    private static double U(Strength s) => s switch
    {
        Strength.Unique => 1e-7,
        Strength.High => 1.0 / 22_000,
        Strength.Medium => 1.0 / 700,
        Strength.Low => 1.0 / 60,
        _ => 0.5,
    };

    // Groups whose disagreement is evidence against identity; every other conflict is soft and weighs nothing here.
    private static readonly Dictionary<EvidenceGroup, double> ConflictU = new()
    {
        [EvidenceGroup.DateOfBirth] = 1.0 / 22_000,
        [EvidenceGroup.Document] = 1e-7,
    };

    public static ShadowScore Score(IReadOnlyDictionary<EvidenceGroup, Evidence> evidence, SourceProfile? profile = null)
    {
        var dobOptions = profile is null ? 1 : Math.Max(1, EvidenceBuilder.ProfileDatesOfBirth(profile).Count);
        var groups = new List<GroupWeight>();
        foreach (var e in evidence.Values)
        {
            if (e.Direction == Direction.Support)
            {
                var u = U(e.Strength) * (e.Group == EvidenceGroup.DateOfBirth ? dobOptions : 1);
                groups.Add(new(e.Group, M, u, Math.Log2(M / u)));
            }
            else if (e.Direction == Direction.Conflict && ConflictU.TryGetValue(e.Group, out var cu))
                groups.Add(new(e.Group, M, cu, Math.Log2((1 - M) / (1 - cu))));
            // Prerequisites, missing, uncertain and neutral values carry no weight: absence is not evidence.
        }
        return new ShadowScore(ModelVersion, groups.Sum(g => g.Weight), groups);
    }
}
