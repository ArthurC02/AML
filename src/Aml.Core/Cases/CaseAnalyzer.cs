using Aml.Core.Identity;
using Aml.Core.Screening;
using Aml.Core.Subjects;

namespace Aml.Core.Cases;

public enum PairResult { NoMatch, Review }

public sealed record HitResult(
    SourceProfile Profile,
    int CandidateRows,
    IReadOnlyList<SourceCondition> Conditions,
    IdentityResult Identity,
    PairResult PairResult);

public sealed record CaseAnalysis(IReadOnlyList<HitResult> Hits, IReadOnlyList<ImportedDecision> ImportedDecisions);

/// <summary>Pure AnalyzeCase flow: deduplicate rows by Profile, then source conditions and identity per hit.</summary>
public static class CaseAnalyzer
{
    public static CaseAnalysis Analyze(CaseInput input)
    {
        var hits = input.Rows
            .GroupBy(r => r.ProfileId)
            .Select(g =>
            {
                var profile = input.Profiles[g.Key];
                var conditions = SourceConditions.Determine(profile, g.Any(r => r.IsRca), input.NewsByProfileId.GetValueOrDefault(g.Key)?.Text);
                var identity = IdentityDecisionPolicy.Decide(input.Subject, profile);
                // PROBABLE_MATCH is internal; externally it is REVIEW (ADR-0003).
                var pair = identity.Decision == IdentityDecision.NoMatch ? PairResult.NoMatch : PairResult.Review;
                return new HitResult(profile, g.Count(), conditions, identity, pair);
            })
            .ToList();
        return new CaseAnalysis(hits, input.LegacyDecisions);
    }
}
