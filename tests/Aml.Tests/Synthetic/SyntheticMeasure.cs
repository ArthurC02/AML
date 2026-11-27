using System.Globalization;
using Aml.Core.Cases;
using Aml.Core.Identity;
using Aml.Core.Ports;
using Aml.Core.Scoring;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using Aml.Infra.Txt;

namespace Aml.Tests.Synthetic;

/// <summary>One candidate through the chain: what the product gave, what the score band would give, and where it was lost.</summary>
public sealed record PairOutcome(SyntheticCase Case, CandidateTruth Truth, HitResult? Hit, IdentityScore? Score, IReadOnlyList<string> FieldErrors,
    bool ConditionRight, bool? StageRight, string Loss, ScreeningAssessment? ScoreResult, string ScoreLoss, string DataCarried);

/// <summary>
/// Runs every synthetic Case through the product's readers (TxtCaseReader, HtmlExport) and CaseAnalyzer, as CaseService
/// does, scores it against the truth at each stage of the chain, and computes the score band beside the stage 3 table
/// (measurement only: nothing reaches a Case).
/// </summary>
public static class SyntheticMeasure
{
    public static IReadOnlyList<PairOutcome> Run(IReadOnlyList<SyntheticCase> cases, DateOnly asOf, IReadOnlyList<IStageReader>? stageReaders = null, ScoringPolicy? policy = null)
    {
        var outcomes = new List<PairOutcome>();
        foreach (var c in cases)
        {
            CaseInput input;
            try
            {
                input = TxtCaseReader.ReadFiles(c.Files);
            }
            catch (CaseInputException e)
            {
                outcomes.AddRange(c.Candidates.Select(t => new PairOutcome(c, t, null, null, [$"ingest:{e.File}:{e.Problem}"], false, null, "extraction: Case not ingested", null, "extraction: Case not ingested", "-")));
                continue;
            }
            var analysis = CaseAnalyzer.Analyze(input, asOf: asOf, stageReaders: stageReaders);
            var subjectErrors = SubjectErrors(c.Subject, input);
            foreach (var t in c.Candidates)
            {
                var errors = subjectErrors.Concat(ProfileErrors(t, input)).ToList();
                var hit = analysis.Hits.FirstOrDefault(h => h.Profile.ProfileKey == t.ProfileKey);
                if (hit is null)
                {
                    outcomes.Add(new(c, t, null, null, errors, false, null, "candidate: no hit", null, "candidate: no hit", "-"));
                    continue;
                }
                if (hit.CandidateRows != t.CandidateRows) errors.Add("candidate:rows-not-folded");
                var condition = hit.Conditions.FirstOrDefault(x => x.Type == t.ListType);
                var conditionRight = condition is not null && (t.ConditionHolds ? condition.Status == ConditionStatus.Hit : condition.Status == ConditionStatus.NotHit);
                bool? stageRight = t.Stage is { } s ? condition?.LegalStage == s : null;
                var score = Score(t, hit.Identity, policy);
                var scoreResult = ScoreResult(hit, score);
                outcomes.Add(new(c, t, hit, score, errors, conditionRight, stageRight,
                    Loss(t, hit.PairResult, hit, errors, condition, conditionRight, score: null),
                    scoreResult, Loss(t, scoreResult, hit, errors, condition, conditionRight, score), DataCarried(hit.Identity)));
            }
        }
        return outcomes;
    }

    /// <summary>The identity score with the stated values counted at the factor of the record's text (news, press release, court ruling).</summary>
    public static IdentityScore Score(CandidateTruth t, IdentityResult identity, ScoringPolicy? policy = null) =>
        ScoringRule.Identity(identity, entityRecord: false, policy, text: t.ScoredAs == SourceKind.OfficialList ? SourceKind.AllowlistedNews : t.ScoredAs);

    /// <summary>The pair result if the score band decided identity (automatic MATCH allowed: scoring-rule.md).</summary>
    public static ScreeningAssessment ScoreResult(HitResult hit, IdentityScore score) =>
        hit.Conditions.Count == 0 || hit.Conditions.All(c => c.Status == ConditionStatus.NotHit) ? ScreeningAssessment.NotApplicable
        : score.Band switch
        {
            IdentityBand.Match => ScreeningAssessment.Hit,
            IdentityBand.NoMatch => ScreeningAssessment.NoHit,
            _ => ScreeningAssessment.Review,
        };

    public static bool IsAutomatic(ScreeningAssessment? r) => r is ScreeningAssessment.Hit or ScreeningAssessment.NoHit or ScreeningAssessment.NotApplicable;

    /// <summary>
    /// "right: …" for a correct automatic end result; otherwise the first stage of the chain that lost it: extraction,
    /// candidate, condition, then identity (wrong, or undecided and why).
    /// </summary>
    private static string Loss(CandidateTruth t, ScreeningAssessment actual, HitResult hit, List<string> errors, SourceCondition? condition, bool conditionRight, IdentityScore? score)
    {
        var expected = t.Expected;
        if (actual == expected) return "right: " + actual;
        var wrong = IsAutomatic(actual) ? "WRONG " : "";
        if (errors.Count > 0) return wrong + "extraction: " + errors[0];
        if (!conditionRight) return wrong + "condition: " + (condition is null ? "list type not raised" : $"{condition.Type} {condition.Status}, truth {(t.ConditionHolds ? "holds" : "does not hold")}");
        if (wrong.Length > 0) return wrong + $"identity: {(score?.Band.ToString() ?? hit.Identity.Decision.ToString())} for {(t.SamePerson ? "the same person" : "another person")}";
        var id = hit.Identity;
        if (score is null)
        {
            if (id.Decision == IdentityDecision.ProbableMatch) return t.SamePerson ? "identity: PROBABLE_MATCH, no automatic MATCH in Phase 1" : "identity: PROBABLE_MATCH for another person";
            return "identity: REVIEW, " + Stage3Reason(id);
        }
        if (score.Band == IdentityBand.ProbableMatch) return t.SamePerson ? "identity: PROBABLE_MATCH, below T_match" : "identity: PROBABLE_MATCH for another person";
        return "identity: REVIEW, " + (score.Applied.Count > 0 ? $"held or capped ({string.Join(", ", score.Applied)})"
            : score.Items.Count == 0 ? "no item beyond the prerequisites" : "support below the thresholds");
    }

    private static string Stage3Reason(IdentityResult id) => id.HardConflict is { } hard
            ? HardConflictGate.ValidatedReasonCodes.Contains(hard) ? $"hard conflict {hard} downgraded (source quality)" : $"pending hard conflict {hard}"
        : id.Stage3Row switch
        {
            Stage3Rows.Conflict => "row 1 conflict " + id.ReasonCodes[0],
            Stage3Rows.PartialSupport => "row 5 partial support",
            Stage3Rows.NoSupport => "row 6 no support beyond the prerequisites",
            _ => $"row {id.Stage3Row}",
        };

    private static readonly EvidenceGroup[] Carried =
    [
        EvidenceGroup.DateOfBirth, EvidenceGroup.Document, EvidenceGroup.Address, EvidenceGroup.Employment, EvidenceGroup.Relationship,
        EvidenceGroup.CompanyAssociation, EvidenceGroup.OtherIdentifier, EvidenceGroup.StatedAge, EvidenceGroup.StatedBirthDate,
    ];

    /// <summary>How many groups beyond the prerequisites both sides carry: 0, 1, 2+.</summary>
    public static string DataCarried(IdentityResult id)
    {
        var n = Carried.Count(g => id.Evidence.GetValueOrDefault(g)?.Status is { } s && s is not (ComparisonStatus.SubjectMissing or ComparisonStatus.ProfileMissing));
        return n >= 2 ? "2+" : n.ToString(CultureInfo.InvariantCulture);
    }

    // ---- Extraction: what the readers got, against what the generator wrote ----

    private static List<string> SubjectErrors(ExpectedSubject e, CaseInput input)
    {
        var s = input.Subject;
        var errors = new List<string>();
        if (!e.Names.All(s.Names.Contains)) errors.Add("subject:names");
        if (s.Gender != e.Gender) errors.Add("subject:gender");
        if (s.DateOfBirth != e.Dob) errors.Add("subject:dob");
        if (!s.Nationalities.Contains(e.Nationality)) errors.Add("subject:nationality");
        if (s.Document?.Number != e.DocumentNumber) errors.Add("subject:document");
        return errors;
    }

    private static List<string> ProfileErrors(CandidateTruth t, CaseInput input)
    {
        if (!input.Profiles.TryGetValue(t.ProfileId, out var p)) return ["profile:not-read"];
        var e = t.Fields;
        var errors = new List<string>();
        if (!p.Names.ToHashSet().SetEquals(e.Names)) errors.Add("profile:names");
        if (p.Gender != e.Gender) errors.Add("profile:gender");
        if (!p.DatesOfBirth.SequenceEqual(e.Dobs)) errors.Add("profile:dob");
        if (!p.Citizenship.SequenceEqual(e.Citizenship)) errors.Add("profile:citizenship");
        if (!(p.Documents ?? []).Select(d => d.Number).SequenceEqual(e.DocumentNumbers)) errors.Add("profile:document");
        if (p.OfficialList?.Status != e.OfficialListStatus) errors.Add("profile:official-list");
        if (e.NewsPublished is { } published && (input.NewsByProfileId.GetValueOrDefault(t.ProfileId) is not [var first, ..] || first.PublishedOn != published)) errors.Add("news:published");
        return errors;
    }

    /// <summary>The comparison-cell rates of the Cases whose first Profile is of these kinds, as AnalysisQuality computes them for a revision.</summary>
    public static AnalysisQuality Quality(IReadOnlyList<SyntheticCase> cases, DateOnly asOf, params RecordKind[] kinds)
    {
        var hits = new List<Core.Explanation.HitExplanation>();
        foreach (var c in cases.Where(c => kinds.Contains(c.Candidates[0].Kind)))
        {
            var input = TxtCaseReader.ReadFiles(c.Files);
            hits.AddRange(Core.Explanation.ExplanationBuilder.Build(input, CaseAnalyzer.Analyze(input, asOf: asOf), asOf: asOf).Hits);
        }
        return AnalysisQuality.Of(hits, [], 0);
    }

    /// <summary>Identity bands of every pair under a policy, for the sensitivity table.</summary>
    public static IReadOnlyList<(CandidateTruth Truth, IdentityScore Score)> Rescore(IReadOnlyList<PairOutcome> outcomes, ScoringPolicy policy) =>
        [.. outcomes.Where(o => o.Hit is not null).Select(o => (o.Truth, Score(o.Truth, o.Hit!.Identity, policy)))];
}
