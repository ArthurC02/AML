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
    IReadOnlyList<string> PairReasonCodes);

/// <summary>Hits, plus candidate Profiles that could not be evaluated (no Profile data): those make the revision incomplete.</summary>
public sealed record CaseAnalysis(IReadOnlyList<HitResult> Hits, IReadOnlyList<string> NotEvaluatedProfiles, IReadOnlyList<ImportedDecision> ImportedDecisions);

/// <summary>Pure AnalyzeCase flow: deduplicate rows by Profile, then source conditions, identity and assessment per hit.</summary>
public static class CaseAnalyzer
{
    public static CaseAnalysis Analyze(CaseInput input)
    {
        var groups = input.Rows.GroupBy(r => r.ProfileId).ToList();
        var hits = groups
            .Where(g => input.Profiles.ContainsKey(g.Key))
            .Select(g =>
            {
                var profile = input.Profiles[g.Key];
                var news = input.NewsByProfileId.GetValueOrDefault(g.Key);
                var conditions = SourceConditions.Determine(profile, g.Any(r => r.IsRca), news?.Text);
                // One identity comparison per pair, reused by every condition (PEP, Sanction, adverse media).
                var identity = IdentityDecisionPolicy.Decide(input.Subject, profile, news);
                var pair = AssessmentPolicy.Assess(conditions, identity);
                return new HitResult(profile, g.Count(), conditions, identity, pair.Result, pair.Priority, pair.ReasonCodes);
            })
            .ToList();
        var notEvaluated = groups.Where(g => !input.Profiles.ContainsKey(g.Key)).Select(g => "DJ:" + g.Key).ToList();
        return new CaseAnalysis(hits, notEvaluated, input.LegacyDecisions);
    }
}
