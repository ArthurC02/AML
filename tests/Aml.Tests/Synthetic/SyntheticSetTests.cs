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
        Assert.Equal((186, 224), (hits.Count(x => x.StageRight == true), hits.Count(x => x.StageRight is not null)));

        // End: the product decides nothing; the score band decides 7, all right.
        Assert.Equal(0, o.Count(x => SyntheticMeasure.IsAutomatic(x.Hit?.PairResult)));
        Assert.Equal((4, 4, 53, 51), (Id(IdentityDecision.NoMatch), Id(IdentityDecision.NoMatch, false), Id(IdentityDecision.ProbableMatch), Id(IdentityDecision.ProbableMatch, true)));
        Assert.Equal((3, 3, 4, 4, 51, 49), (Band(IdentityBand.Match), Band(IdentityBand.Match, true), Band(IdentityBand.NoMatch), Band(IdentityBand.NoMatch, false),
            Band(IdentityBand.ProbableMatch), Band(IdentityBand.ProbableMatch, true)));
        Assert.Equal(7, o.Count(x => SyntheticMeasure.IsAutomatic(x.ScoreResult)));
        Assert.Equal(173, o.Count(x => x.Loss == "identity: REVIEW, row 6 no support beyond the prerequisites"));

        // Held at REVIEW only because the conflict rules are pending: what releasing them would close, and wrongly.
        var held = hits.Where(x => x.Score!.Applied.Contains(ScoringRule.HeldPendingValidation) && x.Score.Total <= ScoringPolicy.Proposed.NoMatch && x.Hit!.Identity.HardConflict is null).ToList();
        Assert.Equal((92, 1), (held.Count, held.Count(x => x.Truth.SamePerson)));

        // Reader holes: an age after the gender in one bracket, a court release's acquittal.
        var ageAfterGender = hits.Where(x => x.Truth.Tags.Contains("text:age") && x.Truth.Tags.Contains("text:gender") && !x.Truth.Tags.Contains("text:masked-name")).ToList();
        Assert.Equal((0, 50), (ageAfterGender.Count(x => x.Hit!.Identity.Evidence.ContainsKey(EvidenceGroup.StatedAge)), ageAfterGender.Count));
        var courtReleaseAcquittals = hits.Where(x => x.Truth is { Kind: RecordKind.CourtRuling, Stage: LegalStage.Acquitted } && !x.Truth.Tags.Contains("text:judgment")).ToList();
        Assert.Equal((0, 13), (courtReleaseAcquittals.Count(x => x.StageRight == true), courtReleaseAcquittals.Count));
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
