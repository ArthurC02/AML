using System.ComponentModel;
using Aml.Core.Identity;

namespace Aml.Core.Screening;

/// <summary>
/// An AML risk-scoring policy. Weights are the bank's to approve; none ships with the system, and an unapproved policy
/// scores nothing (system-overview.md: no unapproved AML scoring weights).
/// </summary>
public sealed record RiskPolicy(
    string Version,
    bool Approved,
    IReadOnlyDictionary<ListType, int> ListWeights,
    IReadOnlyDictionary<string, int> RiskCategoryWeights,
    IReadOnlyDictionary<LegalStage, int> LegalStageWeights,
    int MediumFrom,
    int HighFrom);

public enum RiskLevel { Low, Medium, High }

public sealed record RiskAssessment(
    [property: Description("風險政策版本")] string PolicyVersion,
    [property: Description("分數")] int Score,
    [property: Description("風險等級")] RiskLevel Level,
    [property: Description("分數組成")] IReadOnlyList<string> Contributions);

/// <summary>
/// Downstream of identity: a pair is scored only when identity is not NO_MATCH, and the score never feeds back.
/// Monotone: an UNKNOWN Sanction or PEP scores as a HIT, an ADVERSE_MEDIA condition set aside by ② scores nothing, the legal
/// stage weighs on ADVERSE_MEDIA only, and any raised Sanction is High whatever the weights.
/// </summary>
public static class RiskScoring
{
    public static RiskAssessment? Assess(IReadOnlyList<SourceCondition> conditions, IdentityResult identity, RiskPolicy? policy)
    {
        if (policy is not { Approved: true } || identity.Decision == IdentityDecision.NoMatch) return null;
        var scored = conditions.Where(IsScored).ToList();
        var parts = scored.SelectMany(c => Contributions(c, policy)).ToList();
        var score = parts.Sum(p => p.Points);
        return new RiskAssessment(policy.Version, score, Level(scored, score, policy), parts.Select(p => $"{p.Name}+{p.Points}").ToList());
    }

    private static bool IsScored(SourceCondition c) =>
        (c.Status == ConditionStatus.Hit || (c.Status == ConditionStatus.Unknown && c.Type is ListType.Sanction or ListType.Pep))
        && !SourceConditions.IsUnrelatedAdverseMedia(c);

    private static IEnumerable<(string Name, int Points)> Contributions(SourceCondition c, RiskPolicy policy)
    {
        if (policy.ListWeights.TryGetValue(c.Type, out var w)) yield return ($"list:{c.Type}", w);
        foreach (var r in c.RiskCategories)
            if (policy.RiskCategoryWeights.TryGetValue(r, out var rw)) yield return (r, rw);
        if (c.Type == ListType.AdverseMedia && policy.LegalStageWeights.TryGetValue(c.LegalStage, out var lw)) yield return ($"stage:{c.LegalStage}", lw);
    }

    private static RiskLevel Level(IReadOnlyList<SourceCondition> scored, int score, RiskPolicy policy) =>
        scored.Any(c => c.Type == ListType.Sanction) || score >= policy.HighFrom ? RiskLevel.High
        : score >= policy.MediumFrom ? RiskLevel.Medium
        : RiskLevel.Low;
}
