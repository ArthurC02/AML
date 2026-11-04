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

/// <summary>Downstream of identity: a pair is scored only when identity is not NO_MATCH, and the score never feeds back.</summary>
public static class RiskScoring
{
    public static RiskAssessment? Assess(IReadOnlyList<SourceCondition> conditions, IdentityResult identity, RiskPolicy? policy)
    {
        if (policy is not { Approved: true } || identity.Decision == IdentityDecision.NoMatch) return null;
        var parts = new List<(string Name, int Points)>();
        foreach (var c in conditions.Where(c => c.Status == ConditionStatus.Hit))
        {
            if (policy.ListWeights.TryGetValue(c.Type, out var w)) parts.Add((c.Type.ToString(), w));
            foreach (var r in c.RiskCategories)
                if (policy.RiskCategoryWeights.TryGetValue(r, out var rw)) parts.Add((r, rw));
            if (policy.LegalStageWeights.TryGetValue(c.LegalStage, out var lw)) parts.Add((c.LegalStage.ToString(), lw));
        }
        var score = parts.Sum(p => p.Points);
        var level = score >= policy.HighFrom ? RiskLevel.High : score >= policy.MediumFrom ? RiskLevel.Medium : RiskLevel.Low;
        return new RiskAssessment(policy.Version, score, level, parts.Select(p => $"{p.Name}+{p.Points}").ToList());
    }
}
