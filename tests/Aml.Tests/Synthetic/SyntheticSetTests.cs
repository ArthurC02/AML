using Aml.Core.Identity;
using Aml.Core.Ports;
using Aml.Core.Scoring;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Infra.Models;

namespace Aml.Tests.Synthetic;

/// <summary>
/// The synthetic end-to-end set (docs/plan/poc-delivery-plan.md#synthetic-end-to-end-set): generated Cases through the
/// product's readers and CaseAnalyzer, scored against their truth. Measurement only; without models the rules give the
/// stage. Set AML_SYNTHETIC_REPORT to a file path to write the full report there.
/// </summary>
public class SyntheticSetTests
{
    private static readonly Lazy<(IReadOnlyList<SyntheticCase> Cases, IReadOnlyList<PairOutcome> Outcomes)> Set = new(() =>
    {
        var cases = SyntheticCaseGenerator.Generate();
        return (cases, SyntheticMeasure.Run(cases, SyntheticAssumptions.Default.AsOf));
    });

    [Fact]
    public void The_generator_is_deterministic()
    {
        var (cases, _) = Set.Value;
        var again = SyntheticCaseGenerator.Generate();
        Assert.Equal(SyntheticCaseGenerator.Digest(cases), SyntheticCaseGenerator.Digest(again));
        Assert.Equal("E2E885CCF63BA82A", SyntheticCaseGenerator.Digest(cases));
        var candidates = cases.SelectMany(c => c.Candidates).ToList();
        Assert.Equal((300, 58, 359, 147), (cases.Count, cases.Count(c => c.Html), candidates.Count, candidates.Count(t => t.SamePerson)));
        Assert.Equal(100, SyntheticCaseGenerator.External(cases).Count);
        Assert.NotEqual(SyntheticCaseGenerator.Digest(cases), SyntheticCaseGenerator.Digest(SyntheticCaseGenerator.Generate(SyntheticAssumptions.Default with { Seed = 1 })));
    }

    /// <summary>The headline figures the plan states, as counts so a moved pair shows which way it went.</summary>
    [Fact]
    public void Headline_figures_as_the_plan_states()
    {
        var (_, o) = Set.Value;
        var hits = o.Where(x => x.Hit is not null).ToList();
        int Id(IdentityDecision d, bool? same = null) => hits.Count(x => x.Hit!.Identity.Decision == d && (same is null || x.Truth.SamePerson == same));
        int Band(IdentityBand b, bool? same = null) => hits.Count(x => x.Score!.Band == b && (same is null || x.Truth.SamePerson == same));

        // Chain stages.
        Assert.Equal((359, 359, 359), (hits.Count, o.Count(x => x.FieldErrors.Count == 0), hits.Count(x => x.Hit!.CandidateRows == x.Truth.CandidateRows)));
        Assert.Equal((350, 9), (hits.Count(x => x.ConditionRight), hits.Count(x => x.Hit!.Conditions.Any(c => c.Type == ListType.Sanction && c.Status == ConditionStatus.Unknown))));
        Assert.Equal((224, 224), (hits.Count(x => x.StageRight == true), hits.Count(x => x.StageRight is not null)));

        // End: the product decides 84 (79 right; 5 NO_HIT on a delisted sanction of another person, whose truth cell is
        // NOT_APPLICABLE); the score band decides 87.
        Assert.Equal((84, 79), (o.Count(x => SyntheticMeasure.IsAutomatic(x.Hit?.PairResult)), o.Count(x => x.Loss.StartsWith("right", StringComparison.Ordinal))));
        // Sanction, PEP and RCA pairs close like any other list type: their NO_HIT pairs are all another person, and no NO_HIT is the same person.
        int NoHit(RecordKind k) => o.Count(x => x.Truth.Kind == k && x.Hit?.PairResult == ScreeningAssessment.NoHit && !x.Truth.SamePerson);
        Assert.Equal((23, 13, 10, 0), (NoHit(RecordKind.Sanction), NoHit(RecordKind.Pep), NoHit(RecordKind.Rca), o.Count(x => x.Hit?.PairResult == ScreeningAssessment.NoHit && x.Truth.SamePerson)));
        Assert.Equal((84, 84, 57, 51), (Id(IdentityDecision.NoMatch), Id(IdentityDecision.NoMatch, false), Id(IdentityDecision.ProbableMatch), Id(IdentityDecision.ProbableMatch, true)));
        Assert.Equal((3, 3, 84, 84, 55, 49), (Band(IdentityBand.Match), Band(IdentityBand.Match, true), Band(IdentityBand.NoMatch), Band(IdentityBand.NoMatch, false),
            Band(IdentityBand.ProbableMatch), Band(IdentityBand.ProbableMatch, true)));
        Assert.Equal((87, 82), (o.Count(x => SyntheticMeasure.IsAutomatic(x.ScoreResult)), o.Count(x => x.ScoreLoss.StartsWith("right", StringComparison.Ordinal))));

        // Held at REVIEW only because the document conflict rule is pending: what releasing it would close.
        var held = hits.Where(x => x.Score!.Applied.Contains(ScoringRule.HeldPendingValidation) && x.Score.Total <= ScoringPolicy.Proposed.NoMatch && x.Hit!.Identity.HardConflict is null).ToList();
        Assert.Equal((1, 0), (held.Count, held.Count(x => x.Truth.SamePerson)));

        // Stated next to the name: an age after the gender in one bracket is read as one alone is; no stated age of the customer differs.
        var ageAfterGender = hits.Where(x => x.Truth.Tags.Contains("text:age") && x.Truth.Tags.Contains("text:gender") && !x.Truth.Tags.Contains("text:masked-name")).ToList();
        Assert.Equal((50, 50), (ageAfterGender.Count(x => x.Hit!.Identity.Evidence.ContainsKey(EvidenceGroup.StatedAge)), ageAfterGender.Count));
        Assert.Equal((38, 0), (hits.Count(x => x.Hit!.Identity.Evidence.GetValueOrDefault(EvidenceGroup.StatedAge)?.Status == ComparisonStatus.Conflict),
            hits.Count(x => x.Truth.SamePerson && x.Hit!.Identity.Evidence.GetValueOrDefault(EvidenceGroup.StatedAge)?.Status == ComparisonStatus.Conflict)));
    }

    /// <summary>The plan's legal-stage split: right / read, per kind of record (rules only).</summary>
    [Fact]
    public void Legal_stage_split_as_the_plan_states()
    {
        var staged = Set.Value.Outcomes.Where(x => x.StageRight is not null).ToList();
        string Kind(PairOutcome x) => x.Truth.Kind switch
        {
            RecordKind.WantedList => "wanted list",
            RecordKind.CourtRuling => x.Truth.Tags.Contains("text:judgment") ? "judgment" : "court release",
            RecordKind.News => "news",
            _ => "press release",
        };
        Assert.Equal(new Dictionary<string, (int, int)>
        {
            ["wanted list"] = (36, 36),
            ["judgment"] = (26, 26),
            ["news"] = (73, 73),
            ["press release"] = (60, 60),
            ["court release"] = (29, 29),
        }, staged.GroupBy(Kind).ToDictionary(g => g.Key, g => (g.Count(x => x.StageRight == true), g.Count())));
    }

    /// <summary>The plan's table of where each end result was lost, first stage of the chain, product and score band.</summary>
    [Fact]
    public void Losses_as_the_plan_states()
    {
        var o = Set.Value.Outcomes;
        Assert.Equal(new Dictionary<string, int>
        {
            ["identity: REVIEW, row 6 no support beyond the prerequisites"] = 154,
            ["right: NoHit"] = 79,
            ["identity: PROBABLE_MATCH, no automatic MATCH in Phase 1"] = 49,
            ["identity: REVIEW, row 5 partial support"] = 28,
            ["identity: REVIEW, row 1 conflict AGE_DIFFERENCE_AT_REPORT"] = 26,
            ["identity: REVIEW, row 1 conflict NEWS_BIRTH_DATE_DIFFERENCE"] = 8,
            ["WRONG condition: Sanction Unknown, truth does not hold"] = 5,
            ["identity: PROBABLE_MATCH for another person"] = 5,
            ["condition: Sanction Unknown, truth does not hold"] = 4,
            ["identity: REVIEW, row 1 conflict DOCUMENT_IDENTITY_CONFLICT"] = 1,
        }, o.GroupBy(x => x.Loss).ToDictionary(g => g.Key, g => g.Count()));
        // In both, the 5 WRONG cells are delisted sanctions of another person: identity rightly NO_MATCH, so NO_HIT where the
        // truth's cell is NOT_APPLICABLE (the condition UNKNOWN, never NOT_HIT); no reviewer would act on either.
        Assert.Equal(new Dictionary<string, int>
        {
            ["identity: REVIEW, no item beyond the prerequisites"] = 154,
            ["right: NoHit"] = 79,
            ["identity: REVIEW, support below the thresholds"] = 61,
            ["identity: PROBABLE_MATCH, below T_match"] = 47,
            ["WRONG condition: Sanction Unknown, truth does not hold"] = 5,
            ["identity: PROBABLE_MATCH for another person"] = 5,
            ["condition: Sanction Unknown, truth does not hold"] = 4,
            ["right: Hit"] = 3,
            ["identity: REVIEW, held or capped (SCORE_HELD_PENDING_VALIDATION)"] = 1,
        }, o.GroupBy(x => x.ScoreLoss).ToDictionary(g => g.Key, g => g.Count()));
    }

    /// <summary>
    /// The plan's identity table, per row: n; stage 3 NO_MATCH (right), PROBABLE_MATCH (same person); score MATCH (right),
    /// NO_MATCH (right), PROBABLE_MATCH (same person).
    /// </summary>
    [Fact]
    public void Identity_per_kind_as_the_plan_states()
    {
        var hits = Set.Value.Outcomes.Where(x => x.Hit is not null).ToList();
        (int, int, int, int, int, int, int, int, int, int, int) Row(IEnumerable<PairOutcome> rows)
        {
            var x = rows.ToList();
            int S3(IdentityDecision d, bool? same) => x.Count(p => p.Hit!.Identity.Decision == d && (same is null || p.Truth.SamePerson == same));
            int Sc(IdentityBand b, bool? same) => x.Count(p => p.Score!.Band == b && (same is null || p.Truth.SamePerson == same));
            return (x.Count, S3(IdentityDecision.NoMatch, null), S3(IdentityDecision.NoMatch, false), S3(IdentityDecision.ProbableMatch, null), S3(IdentityDecision.ProbableMatch, true),
                Sc(IdentityBand.Match, null), Sc(IdentityBand.Match, true), Sc(IdentityBand.NoMatch, null), Sc(IdentityBand.NoMatch, false), Sc(IdentityBand.ProbableMatch, null), Sc(IdentityBand.ProbableMatch, true));
        }
        Assert.Equal((359, 84, 84, 57, 51, 3, 3, 84, 84, 55, 49), Row(hits));
        Assert.Equal(new Dictionary<string, (int, int, int, int, int, int, int, int, int, int, int)>
        {
            ["Sanction"] = (45, 23, 23, 12, 10, 2, 2, 23, 23, 10, 8),
            ["Pep"] = (52, 13, 13, 9, 8, 0, 0, 13, 13, 9, 8),
            ["Rca"] = (38, 10, 10, 2, 2, 0, 0, 10, 10, 2, 2),
            ["WantedList"] = (36, 7, 7, 15, 13, 1, 1, 7, 7, 14, 12),
            ["News"] = (73, 13, 13, 7, 6, 0, 0, 13, 13, 7, 6),
            ["PressRelease"] = (60, 8, 8, 4, 4, 0, 0, 8, 8, 4, 4),
            ["CourtRuling"] = (55, 10, 10, 8, 8, 0, 0, 10, 10, 9, 9),
            ["carries 0"] = (100, 2, 2, 0, 0, 0, 0, 2, 2, 0, 0),
            ["carries 1"] = (155, 44, 44, 24, 19, 0, 0, 44, 44, 25, 20),
            ["carries 2+"] = (104, 38, 38, 33, 32, 3, 3, 38, 38, 30, 29),
        }, hits.GroupBy(x => x.Truth.Kind.ToString()).Concat(hits.GroupBy(x => "carries " + x.DataCarried)).ToDictionary(g => g.Key, Row));
    }

    /// <summary>The plan's sensitivity table: decided; auto MATCH (right); auto NO_MATCH (right); PROBABLE_MATCH (same person).</summary>
    [Fact]
    public void Sensitivity_as_the_plan_states()
    {
        var o = Set.Value.Outcomes;
        Assert.Equal(new Dictionary<string, (int, int, int, int, int, int, int)>
        {
            ["proposed (60 / 30 / 8 / -25)"] = (87, 3, 3, 84, 84, 55, 49),
            ["T_match 40"] = (91, 7, 7, 84, 84, 51, 45),
            ["T_match 50"] = (87, 3, 3, 84, 84, 55, 49),
            ["T_match 70"] = (84, 0, 0, 84, 84, 58, 52),
            ["T_probable 20"] = (87, 3, 3, 84, 84, 57, 51),
            ["T_probable 40"] = (87, 3, 3, 84, 84, 55, 49),
            ["T_probable_exact 15"] = (87, 3, 3, 84, 84, 28, 28),
            ["T_probable_exact 25"] = (87, 3, 3, 84, 84, 21, 21),
            ["T_no_match -10"] = (87, 3, 3, 84, 84, 55, 49),
            ["T_no_match -40"] = (87, 3, 3, 84, 84, 55, 49),
            ["DOB within tolerance +25"] = (87, 3, 3, 84, 84, 55, 49),
        }, SyntheticReport.Sensitivity.ToDictionary(p => p.Label, p => SyntheticMeasure.Rescore(o, p.Policy) is var s
            ? (s.Count(x => x.Score.Band is IdentityBand.Match or IdentityBand.NoMatch), s.Count(x => x.Score.Band == IdentityBand.Match), s.Count(x => x.Score.Band == IdentityBand.Match && x.Truth.SamePerson),
                s.Count(x => x.Score.Band == IdentityBand.NoMatch), s.Count(x => x.Score.Band == IdentityBand.NoMatch && !x.Truth.SamePerson),
                s.Count(x => x.Score.Band == IdentityBand.ProbableMatch), s.Count(x => x.Score.Band == IdentityBand.ProbableMatch && x.Truth.SamePerson))
            : default));
    }

    /// <summary>The plan's birth-date tolerance figures: who the two-year tolerance lets through, under the product and both proposed points.</summary>
    [Fact]
    public void Birth_date_tolerance_as_the_plan_states()
    {
        var hits = Set.Value.Outcomes.Where(x => x.Hit is not null).ToList();
        bool Tolerant(PairOutcome x) => x.Hit!.Identity.Evidence[EvidenceGroup.DateOfBirth].Detail == ReasonCodes.DobAgreesWithinTolerance;

        // Strangers with a DOB on both sides, and those born within two years of the customer (truth; implied by the generator).
        var strangers = hits.Where(x => !x.Truth.SamePerson && x.Case.Subject.Dob is not null && x.Truth.Fields.Dobs.Count > 0).ToList();
        Assert.Equal((95, 14), (strangers.Count, strangers.Count(x => x.Truth.Fields.Dobs.Any(d => !x.Case.Subject.Dob!.Value.IsMoreThanYearsApart(d, EvidenceBuilder.DobToleranceYears)))));

        // Agreement within tolerance: 10 strangers (4 PROBABLE_MATCH), 1 customer (the one-day typo on a full date); none MATCH at +8 or +25.
        var plus25 = SyntheticMeasure.Rescore(Set.Value.Outcomes, ScoringPolicy.Proposed with { DobWithinTolerance = 25 }).ToDictionary(x => x.Truth, x => x.Score.Band);
        foreach (var same in new[] { false, true })
        {
            var t = hits.Where(x => Tolerant(x) && x.Truth.SamePerson == same).ToList();
            Assert.Equal(same ? (1, 0, 0, 0, 0) : (10, 4, 4, 4, 0),
                (t.Count, t.Count(x => x.Hit!.Identity.Decision == IdentityDecision.ProbableMatch), t.Count(x => x.Score!.Band == IdentityBand.ProbableMatch),
                    t.Count(x => plus25[x.Truth] == IdentityBand.ProbableMatch), t.Count(x => x.Score!.Band == IdentityBand.Match || plus25[x.Truth] == IdentityBand.Match)));
        }

        // The one-day typos on the customer's Profile: none concludes NO_MATCH.
        var typos = hits.Where(x => x.Truth.Tags.Contains("dob:typo")).ToList();
        Assert.Equal((2, 0), (typos.Count, typos.Count(x => x.Hit!.Identity.Decision == IdentityDecision.NoMatch)));

        // Wrong automatic identity decisions: none, under the product or the score.
        Assert.Equal(0, hits.Count(x => x.Hit!.Identity.Decision == IdentityDecision.NoMatch && x.Truth.SamePerson));
        Assert.Equal(0, hits.Count(x => x.Score!.Band == IdentityBand.NoMatch && x.Truth.SamePerson || x.Score.Band == IdentityBand.Match && !x.Truth.SamePerson));
    }

    [Fact]
    public void Report()
    {
        var (cases, outcomes) = Set.Value;
        Write(SyntheticReport.Write(cases, outcomes, SyntheticCaseGenerator.External(cases)));
    }

    /// <summary>
    /// The same set with both stage models voting (ADR-0012). Skipped unless AML_STAGE_MODEL names the classifier's ONNX
    /// folder and AML_STAGE_LLM_URL / AML_STAGE_LLM_MODEL a language-model server; the report shows what the vote changes.
    /// </summary>
    [Fact]
    public void Report_with_the_stage_models()
    {
        var (model, url, llm) = (Environment.GetEnvironmentVariable("AML_STAGE_MODEL"), Environment.GetEnvironmentVariable("AML_STAGE_LLM_URL"), Environment.GetEnvironmentVariable("AML_STAGE_LLM_MODEL"));
        Assert.SkipWhen(string.IsNullOrEmpty(model) || string.IsNullOrEmpty(url) || string.IsNullOrEmpty(llm),
            "AML_STAGE_MODEL, AML_STAGE_LLM_URL or AML_STAGE_LLM_MODEL is not set: the models are outside the repository");
        using var classifier = new OnnxStageClassifier(model!);
        using var http = new HttpClient { BaseAddress = new Uri(url!), Timeout = TimeSpan.FromMinutes(2) };
        var cases = Set.Value.Cases;
        var outcomes = SyntheticMeasure.Run(cases, SyntheticAssumptions.Default.AsOf, [classifier, new OllamaStageReader(http, llm!)]);
        Write(SyntheticReport.Write(cases, outcomes, []));
    }

    private static void Write(string report)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        if (Environment.GetEnvironmentVariable("AML_SYNTHETIC_REPORT") is { Length: > 0 } path) File.WriteAllText(path, report);
    }
}
