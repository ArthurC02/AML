using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Identity;

/// <summary>One group's Fellegi-Sunter weight: log2(m/u) on agreement, log2((1−m)/(1−u)) on disagreement.</summary>
public sealed record GroupWeight(EvidenceGroup Group, double M, double U, double Weight);

public sealed record ShadowScore(string ModelVersion, double TotalWeight, IReadOnlyList<GroupWeight> Groups);

/// <summary>
/// Fellegi-Sunter match weight for Gate D calibration (ADR-0019). It does not run in the analysis, so a revision's lineage
/// does not name it (ADR-0025); calibration recomputes it from the stored revisions (the hit evidence and the Profile). It never changes a decision, is not
/// part of the identity result or the explanation, and is never shown as a probability (ADR-0018).
/// u follows the support-strength table; m is one placeholder agreement rate for every group. Agreeing with any one of
/// k Profile DOBs is k times as likely by chance, so the DOB u is multiplied by k.
/// ponytail: m and u are uncalibrated placeholders; estimate them with <see cref="EstimateParameters"/> over representative
/// pairs, and a threshold with <see cref="Calibrate"/> over labelled ones, before any use at Gate D.
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

    /// <summary>One pair's agreement pattern: support agrees, conflict disagrees; every other group is left out (missing).</summary>
    public static IReadOnlyDictionary<EvidenceGroup, bool> Pattern(IReadOnlyDictionary<EvidenceGroup, Evidence> evidence) =>
        evidence.Values.Where(e => e.Direction is Direction.Support or Direction.Conflict)
            .ToDictionary(e => e.Group, e => e.Direction == Direction.Support);

    /// <summary>
    /// Expectation-maximization over unlabelled agreement patterns (Fellegi-Sunter with conditional independence; a group
    /// missing from a pattern is left out of that pair's likelihood). Returns m and u per group and the match share.
    /// ponytail: one latent class, no interactions between groups; fit a log-linear model if groups prove dependent.
    /// </summary>
    public static Estimate EstimateParameters(IReadOnlyList<IReadOnlyDictionary<EvidenceGroup, bool>> patterns,
        double matchShare = 0.1, int maxIterations = 200, double tolerance = 1e-9)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(matchShare, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(matchShare, 1);
        if (patterns.Count == 0) return new Estimate([], matchShare, 0);
        var groups = patterns.SelectMany(p => p.Keys).Distinct().Order().ToList();
        var m = groups.ToDictionary(g => g, _ => 0.9);
        var u = groups.ToDictionary(g => g, _ => 0.1);
        var p = matchShare;
        var iterations = 0;
        for (; iterations < maxIterations; iterations++)
        {
            // E step: the posterior that each pair is a match.
            var w = patterns.Select(x =>
            {
                double lm = Math.Log(p), lu = Math.Log(1 - p);
                foreach (var (g, agrees) in x)
                {
                    lm += Math.Log(agrees ? m[g] : 1 - m[g]);
                    lu += Math.Log(agrees ? u[g] : 1 - u[g]);
                }
                return 1 / (1 + Math.Exp(lu - lm));
            }).ToList();

            // M step, clamped away from 0 and 1 so a log stays finite.
            var change = Math.Abs(w.Average() - p);
            p = Clamp(w.Average());
            foreach (var g in groups)
            {
                double am = 0, nm = 0, au = 0, nu = 0;
                for (var i = 0; i < patterns.Count; i++)
                    if (patterns[i].TryGetValue(g, out var agrees))
                    {
                        nm += w[i];
                        nu += 1 - w[i];
                        if (agrees) { am += w[i]; au += 1 - w[i]; }
                    }
                var (newM, newU) = (Clamp(nm > 0 ? am / nm : m[g]), Clamp(nu > 0 ? au / nu : u[g]));
                change = Math.Max(change, Math.Max(Math.Abs(newM - m[g]), Math.Abs(newU - u[g])));
                (m[g], u[g]) = (newM, newU);
            }
            if (change < tolerance) { iterations++; break; }
        }
        return new Estimate(groups.Select(g => new GroupParameters(g, m[g], u[g])).ToList(), p, iterations);
    }

    private static double Clamp(double x) => Math.Clamp(x, 1e-6, 1 - 1e-6);

    /// <summary>Total weight of a pattern under estimated parameters; a group without parameters weighs nothing.</summary>
    public static double Weight(IReadOnlyDictionary<EvidenceGroup, bool> pattern, Estimate estimate) =>
        pattern.Sum(x => estimate.Groups.FirstOrDefault(g => g.Group == x.Key) is { } g
            ? x.Value ? Math.Log2(g.M / g.U) : Math.Log2((1 - g.M) / (1 - g.U))
            : 0);

    /// <summary>
    /// The highest weight threshold that keeps at least <paramref name="minRecall"/> of the labelled matches at or above it,
    /// with the recall and precision it gives on the labelled pairs. Null without a labelled match.
    /// </summary>
    public static Threshold? Calibrate(IReadOnlyList<(double Weight, bool IsMatch)> labelled, double minRecall = 1.0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minRecall, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minRecall, 1);
        var matches = labelled.Where(x => x.IsMatch).Select(x => x.Weight).OrderDescending().ToList();
        if (matches.Count == 0) return null;
        var t = matches[(int)Math.Ceiling(minRecall * matches.Count - 1e-9) - 1];
        var above = labelled.Where(x => x.Weight >= t).ToList();
        return new Threshold(t, (double)matches.Count(w => w >= t) / matches.Count, (double)above.Count(x => x.IsMatch) / above.Count);
    }
}

public sealed record GroupParameters(EvidenceGroup Group, double M, double U);

/// <summary>EM result: m and u per group, the estimated match share and the iterations run.</summary>
public sealed record Estimate(IReadOnlyList<GroupParameters> Groups, double MatchShare, int Iterations);

public sealed record Threshold(double Weight, double Recall, double Precision);
