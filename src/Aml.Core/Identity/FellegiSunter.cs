using Aml.Core.Normalization;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Identity;

/// <summary>One group's Fellegi-Sunter weight: log2(m/u) on agreement, log2((1−m)/(1−u)) on disagreement.</summary>
public sealed record GroupWeight(EvidenceGroup Group, double M, double U, double Weight);

public sealed record CalibrationWeight(string ModelVersion, double TotalWeight, IReadOnlyList<GroupWeight> Groups);

/// <summary>
/// Fellegi-Sunter match weight for Gate D calibration (ADR-0001). It does not run in the analysis, so a revision's lineage
/// does not name it (ADR-0023); calibration recomputes it from the stored revisions (the hit evidence and the Profile). It never changes a decision, is not
/// part of the identity result or the explanation, and is never shown as a probability (ADR-0001).
/// Agreeing with any one of k Profile DOBs is k times as likely by chance, so the DOB u is multiplied by k.
/// The hit prerequisites (NAME, GENDER, NATIONALITY) weigh here although they never support a decision: without them the
/// pattern holds only DOB and DOCUMENT, and EM over it does not converge on list data.
/// ponytail: m and u are uncalibrated placeholders, partly measured on sanctions lists against each other (not a customer
/// population; identity-adjudication.md#calibration-weight); estimate them with <see cref="EstimateParameters"/> over
/// representative pairs, and a threshold with <see cref="Calibrate"/> over labelled ones, before any use at Gate D.
/// </summary>
public static class FellegiSunter
{
    public const string ModelVersion = "fs-calibration-0.2";

    /// <summary>m of a group the calibration run did not estimate (it saw no support in any other group).</summary>
    public const double PlaceholderM = 0.95;

    private const double InitialM = 0.9;
    private const double InitialU = 0.1;
    private const double ProbabilityMargin = 1e-6;
    private const double RoundingSlack = 1e-9;

    // m = share of same persons that agree; EM over UN x OFAC pairs with the prerequisites in the pattern.
    private static readonly Dictionary<EvidenceGroup, double> MOfGroup = new()
    {
        [EvidenceGroup.Name] = 0.76,        // an EXACT name; the other 0.24 are PARTIAL (spelling, romanization)
        [EvidenceGroup.Gender] = 0.99,      // EM 1.00, kept below 1 so a disagreement stays finite
        [EvidenceGroup.Nationality] = 0.99,
        [EvidenceGroup.DateOfBirth] = 0.91, // any DOB agreement
        [EvidenceGroup.Document] = 0.96,
    };

    // u = share of different persons that agree by chance, for a prerequisite.
    private static readonly Dictionary<EvidenceGroup, double> UOfPrerequisite = new()
    {
        [EvidenceGroup.Name] = 1.0 / 4_200, // Chinese news corpus, three-character full names; an upper bound
        [EvidenceGroup.Gender] = 0.5,       // a general population; 0.85 on the mostly male lists
        [EvidenceGroup.Nationality] = 0.1,  // 0.11 OFAC x OFAC, 0.018 UN x OFAC; set by the customer population
    };

    // identity-adjudication.md#support-strength: the rough u that defines each level, for groups without a measured u.
    private static double LevelChance(Strength s) => s switch
    {
        Strength.Unique => 1e-7, // no chance document agreement in 12,810 comparable pairs bounds it below 2.3e-4 only
        Strength.High => 1.0 / 22_000,
        Strength.Medium => 1.0 / 700,
        Strength.Low => 1.0 / 60,
        _ => 0.5,
    };

    // Chance DOB agreement at each level: 2,000,000 random pairs of different OFAC persons, at the precision the pair
    // allows; an upper bound for a customer against a list, since list dates cluster on placeholder days.
    private static double DobChance(Strength s) => s switch
    {
        Strength.High => 1.0 / 13_700,
        Strength.Medium => 1.0 / 440,
        Strength.Low => 1.0 / 50,
        _ => LevelChance(s),
    };

    private static readonly Dictionary<EvidenceGroup, double> AccidentalDisagreementChanceOfHardGroups = new()
    {
        [EvidenceGroup.DateOfBirth] = DobChance(Strength.High),
        [EvidenceGroup.Document] = LevelChance(Strength.Unique),
    };

    public static CalibrationWeight Score(IReadOnlyDictionary<EvidenceGroup, Evidence> evidence, SourceProfile? profile = null)
    {
        var profileDobCount = profile is null ? 1 : Math.Max(1, EvidenceBuilder.ProfileDatesOfBirth(profile).Count);
        var groups = evidence.Values.Select(e => WeightOf(e, profileDobCount)).OfType<GroupWeight>().ToList();
        return new CalibrationWeight(ModelVersion, groups.Sum(g => g.Weight), groups);
    }

    private static GroupWeight? WeightOf(Evidence e, int profileDobCount)
    {
        if (e.Direction == Direction.Prerequisite)
            return Agrees(e) is { } agrees && UOfPrerequisite.TryGetValue(e.Group, out var pu) ? Weighed(e.Group, pu, agrees) : null;
        if (e.Direction == Direction.Support)
            return Weighed(e.Group, e.Group == EvidenceGroup.DateOfBirth ? DobChance(e.Strength) * profileDobCount : LevelChance(e.Strength), true);
        if (e.Direction == Direction.Conflict && AccidentalDisagreementChanceOfHardGroups.TryGetValue(e.Group, out var du))
            return Weighed(e.Group, du, false);
        return null;
    }

    private static GroupWeight Weighed(EvidenceGroup g, double u, bool agrees)
    {
        var m = MOfGroup.GetValueOrDefault(g, PlaceholderM);
        return new(g, m, u, agrees ? AgreementWeight(m, u) : DisagreementWeight(m, u));
    }

    private static double AgreementWeight(double m, double u) => Math.Log2(m / u);

    private static double DisagreementWeight(double m, double u) => Math.Log2((1 - m) / (1 - u));

    /// <summary>
    /// One pair's agreement pattern. Support agrees, conflict disagrees. A hit prerequisite agrees when EXACT and disagrees
    /// when CONFLICT or PARTIAL: a name has no CONFLICT, and PARTIAL is every compared name that is not EXACT, similar or
    /// not, as the decision policy reads it (row 4 needs EXACT). A PARTIAL name across scripts was not compared.
    /// Everything else, missing on either side included, is left out.
    /// ponytail: binary agreement, so a romanization variant disagrees like any other name; give it its own level with
    /// its own m and u once labelled pairs can estimate them.
    /// </summary>
    public static IReadOnlyDictionary<EvidenceGroup, bool> Pattern(IReadOnlyDictionary<EvidenceGroup, Evidence> evidence) =>
        evidence.Values.Select(e => (e.Group, Agrees: Agrees(e))).Where(x => x.Agrees is not null)
            .ToDictionary(x => x.Group, x => x.Agrees!.Value);

    private static bool? Agrees(Evidence e) => e.Direction switch
    {
        Direction.Support => true,
        Direction.Conflict => false,
        Direction.Prerequisite => e.Status switch
        {
            ComparisonStatus.Exact => true,
            ComparisonStatus.Conflict => false,
            ComparisonStatus.Partial when e.Detail != NameComparison.NotComparableCrossScript => false,
            _ => null,
        },
        _ => null,
    };

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
        var m = groups.ToDictionary(g => g, _ => InitialM);
        var u = groups.ToDictionary(g => g, _ => InitialU);
        var share = matchShare;
        var iterations = 0;
        for (; iterations < maxIterations; iterations++)
        {
            var posteriors = MatchPosteriors(patterns, m, u, share);
            var change = Math.Abs(posteriors.Average() - share);
            share = KeepLogFinite(posteriors.Average());
            foreach (var g in groups)
            {
                var (newM, newU) = Reestimate(g, patterns, posteriors, m[g], u[g]);
                change = Math.Max(change, Math.Max(Math.Abs(newM - m[g]), Math.Abs(newU - u[g])));
                (m[g], u[g]) = (newM, newU);
            }
            if (change < tolerance) { iterations++; break; }
        }
        return new Estimate(groups.Select(g => new GroupParameters(g, m[g], u[g])).ToList(), share, iterations);
    }

    private static List<double> MatchPosteriors(IReadOnlyList<IReadOnlyDictionary<EvidenceGroup, bool>> patterns,
        Dictionary<EvidenceGroup, double> m, Dictionary<EvidenceGroup, double> u, double share) =>
        patterns.Select(x =>
        {
            double logMatch = Math.Log(share), logNonMatch = Math.Log(1 - share);
            foreach (var (g, agrees) in x)
            {
                logMatch += Math.Log(agrees ? m[g] : 1 - m[g]);
                logNonMatch += Math.Log(agrees ? u[g] : 1 - u[g]);
            }
            return 1 / (1 + Math.Exp(logNonMatch - logMatch));
        }).ToList();

    private static (double M, double U) Reestimate(EvidenceGroup g, IReadOnlyList<IReadOnlyDictionary<EvidenceGroup, bool>> patterns,
        List<double> posteriors, double previousM, double previousU)
    {
        double matchAgree = 0, matchSeen = 0, nonMatchAgree = 0, nonMatchSeen = 0;
        for (var i = 0; i < patterns.Count; i++)
            if (patterns[i].TryGetValue(g, out var agrees))
            {
                matchSeen += posteriors[i];
                nonMatchSeen += 1 - posteriors[i];
                if (agrees) { matchAgree += posteriors[i]; nonMatchAgree += 1 - posteriors[i]; }
            }
        return (KeepLogFinite(matchSeen > 0 ? matchAgree / matchSeen : previousM),
            KeepLogFinite(nonMatchSeen > 0 ? nonMatchAgree / nonMatchSeen : previousU));
    }

    private static double KeepLogFinite(double probability) => Math.Clamp(probability, ProbabilityMargin, 1 - ProbabilityMargin);

    /// <summary>Total weight of a pattern under estimated parameters; a group without parameters weighs nothing.</summary>
    public static double Weight(IReadOnlyDictionary<EvidenceGroup, bool> pattern, Estimate estimate) =>
        pattern.Sum(x => estimate.Groups.FirstOrDefault(g => g.Group == x.Key) is { } g
            ? x.Value ? AgreementWeight(g.M, g.U) : DisagreementWeight(g.M, g.U)
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
        var matchesToKeep = (int)Math.Ceiling(minRecall * matches.Count - RoundingSlack);
        var threshold = matches[matchesToKeep - 1];
        var above = labelled.Where(x => x.Weight >= threshold).ToList();
        return new Threshold(threshold, (double)matches.Count(w => w >= threshold) / matches.Count, (double)above.Count(x => x.IsMatch) / above.Count);
    }
}

public sealed record GroupParameters(EvidenceGroup Group, double M, double U);

/// <summary>EM result: m and u per group, the estimated match share and the iterations run.</summary>
public sealed record Estimate(IReadOnlyList<GroupParameters> Groups, double MatchShare, int Iterations);

public sealed record Threshold(double Weight, double Recall, double Precision);
