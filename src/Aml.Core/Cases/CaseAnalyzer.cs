using Aml.Core.Identity;
using Aml.Core.Screening;
using Aml.Core.Subjects;

namespace Aml.Core.Cases;

public sealed record HitResult(
    SourceProfile Profile,
    int CandidateRows,
    IReadOnlyList<SourceCondition> Conditions,
    IdentityResult Identity,
    ScreeningAssessment PairResult,
    ReviewPriority? ReviewPriority,
    IReadOnlyList<string> PairReasonCodes,
    DispositionRecommendation Disposition,
    RiskAssessment? Risk = null);

/// <summary>Hits, plus candidate Profiles that could not be evaluated (no Profile data): those make the revision incomplete.</summary>
public sealed record CaseAnalysis(IReadOnlyList<HitResult> Hits, IReadOnlyList<string> NotEvaluatedProfiles, IReadOnlyList<ImportedDecision> ImportedDecisions);

/// <summary>Pure AnalyzeCase flow: deduplicate rows by Profile, then source conditions, identity and assessment per hit.</summary>
public static class CaseAnalyzer
{
    /// <param name="risk">An approved AML risk policy; without one no pair is scored.</param>
    public static CaseAnalysis Analyze(CaseInput input, RiskPolicy? risk = null)
    {
        var groups = input.Rows.GroupBy(r => r.ProfileId).ToList();
        var hits = groups
            .Where(g => input.Profiles.ContainsKey(g.Key))
            .Select(g =>
            {
                var profile = input.Profiles[g.Key];
                var news = input.NewsByProfileId.GetValueOrDefault(g.Key);
                var conditions = SourceConditions.Determine(profile, g.Any(r => r.IsRca), news?.Text, ListFlags.Merge(g.Select(r => r.Flags ?? ListFlags.None)));
                // One identity comparison per pair, reused by every condition (PEP, Sanction, adverse media).
                var identity = IdentityDecisionPolicy.Decide(input.Subject, profile, news);
                var pair = AssessmentPolicy.Assess(conditions, identity);
                var disposition = DispositionPolicy.Recommend(input.Subject, profile, conditions, identity, news is not null);
                return new HitResult(profile, g.Count(), conditions, identity, pair.Result, pair.Priority, pair.ReasonCodes, disposition,
                    RiskScoring.Assess(conditions, identity, risk));
            })
            .ToList();
        var notEvaluated = groups.Where(g => !input.Profiles.ContainsKey(g.Key)).Select(g => "DJ:" + g.Key).ToList();
        return new CaseAnalysis(hits, notEvaluated, input.LegacyDecisions);
    }
}
