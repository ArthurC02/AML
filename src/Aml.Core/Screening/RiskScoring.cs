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

public sealed record RiskAssessment(string PolicyVersion, int Score, RiskLevel Level, IReadOnlyList<string> Contributions);

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
        var scored = conditions.Where(c =>
            (c.Status == ConditionStatus.Hit || (c.Status == ConditionStatus.Unknown && c.Type is ListType.Sanction or ListType.Pep))
            && !(c.Type == ListType.AdverseMedia && c.AmlRelevance == AmlRelevance.Unrelated)).ToList();
        var parts = new List<(string Name, int Points)>();
        foreach (var c in scored)
        {
            if (policy.ListWeights.TryGetValue(c.Type, out var w)) parts.Add(($"list:{c.Type}", w));
            foreach (var r in c.RiskCategories)
                if (policy.RiskCategoryWeights.TryGetValue(r, out var rw)) parts.Add((r, rw));
            if (c.Type == ListType.AdverseMedia && policy.LegalStageWeights.TryGetValue(c.LegalStage, out var lw)) parts.Add(($"stage:{c.LegalStage}", lw));
        }
        var score = parts.Sum(p => p.Points);
        var level = scored.Any(c => c.Type == ListType.Sanction) || score >= policy.HighFrom ? RiskLevel.High
            : score >= policy.MediumFrom ? RiskLevel.Medium : RiskLevel.Low;
        return new RiskAssessment(policy.Version, score, level, parts.Select(p => $"{p.Name}+{p.Points}").ToList());
    }
}
