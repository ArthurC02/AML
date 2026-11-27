using System.Globalization;
using System.Text;
using Aml.Core.Identity;
using Aml.Core.Scoring;
using Aml.Core.Screening;

namespace Aml.Tests.Synthetic;

/// <summary>The tables of docs/plan/poc-delivery-plan.md#synthetic-end-to-end-set, computed from the outcomes.</summary>
public static class SyntheticReport
{
    public static string Pct(int n, int d) => d == 0 ? "n/a" : string.Create(CultureInfo.InvariantCulture, $"{100.0 * n / d:0.0}% ({n}/{d})");

    public static string Write(IReadOnlyList<SyntheticCase> cases, IReadOnlyList<PairOutcome> o, IReadOnlyList<ExternalFixture> external)
    {
        var sb = new StringBuilder();
        void Line(string s) => sb.Append(s).Append('\n');

        Line($"Cases {cases.Count} (HTML {cases.Count(c => c.Html)}), candidates {o.Count}");
        Line("| kind | same, holds | same, not | other, holds | other, not |");
        foreach (var k in Enum.GetValues<RecordKind>())
        {
            int N(bool same, bool holds) => o.Count(x => x.Truth.Kind == k && x.Truth.SamePerson == same && x.Truth.ConditionHolds == holds);
            Line($"| {k} | {N(true, true)} | {N(true, false)} | {N(false, true)} | {N(false, false)} |");
        }

        foreach (var (label, kinds) in new[] { ("list records", new[] { RecordKind.Sanction, RecordKind.Pep, RecordKind.Rca, RecordKind.WantedList }), ("adverse records", new[] { RecordKind.News, RecordKind.PressRelease, RecordKind.CourtRuling }) })
        {
            var q = SyntheticMeasure.Quality(cases, SyntheticAssumptions.Default.AsOf, kinds);
            Line(string.Create(CultureInfo.InvariantCulture, $"Comparison cells, {label}: missing {q.MissingRate:P0}, ambiguous {q.UncertainRate:P0}, conflict {q.ConflictRate:P0} ({q.ComparisonCells} cells)"));
        }

        Line("\nChain stages (stage 3 table deciding)");
        var hits = o.Where(x => x.Hit is not null).ToList();
        var fieldChecked = o.Count;
        Line($"| ingest | {Pct(cases.Count - o.Where(x => x.Loss.StartsWith("extraction: Case not ingested", StringComparison.Ordinal)).Select(x => x.Case.Id).Distinct().Count(), cases.Count)} Cases read |");
        Line($"| fields | {Pct(o.Count(x => x.FieldErrors.Count == 0), fieldChecked)} candidates with every checked field right |");
        Line($"| candidate | {Pct(hits.Count(x => !x.FieldErrors.Contains("candidate:rows-not-folded")), o.Count)} found, rows folded into one hit |");
        Line($"| condition | {Pct(hits.Count(x => x.ConditionRight), hits.Count)} list type and status right; undecided {hits.Count(x => !x.ConditionRight && Status(x) == ConditionStatus.Unknown)}, wrong {hits.Count(x => !x.ConditionRight && Status(x) != ConditionStatus.Unknown)} |");
        var staged = hits.Where(x => x.StageRight is not null).ToList();
        Line($"| legal stage | {Pct(staged.Count(x => x.StageRight == true), staged.Count)} right |");
        Line($"| identity | right automatic {hits.Count(x => x.Hit!.Identity.Decision == IdentityDecision.NoMatch && !x.Truth.SamePerson)}, wrong automatic {hits.Count(x => x.Hit!.Identity.Decision == IdentityDecision.NoMatch && x.Truth.SamePerson)}, PROBABLE_MATCH {hits.Count(x => x.Hit!.Identity.Decision == IdentityDecision.ProbableMatch)} (same person {hits.Count(x => x.Hit!.Identity.Decision == IdentityDecision.ProbableMatch && x.Truth.SamePerson)}), REVIEW {hits.Count(x => x.Hit!.Identity.Decision == IdentityDecision.Review)} |");
        Line($"| pair result | right automatic {o.Count(x => x.Loss.StartsWith("right", StringComparison.Ordinal))}, wrong automatic {o.Count(x => x.Loss.StartsWith("WRONG", StringComparison.Ordinal))}, REVIEW {o.Count(x => x.Hit?.PairResult == ScreeningAssessment.Review)} |");

        Line("\nEnd results");
        Line("| measure | product (stage 3) | score band |");
        Line($"| decided (no reviewer) | {Pct(o.Count(x => SyntheticMeasure.IsAutomatic(x.Hit?.PairResult)), o.Count)} | {Pct(o.Count(x => SyntheticMeasure.IsAutomatic(x.ScoreResult)), o.Count)} |");
        Line($"| right of those | {Pct(o.Count(x => x.Loss.StartsWith("right", StringComparison.Ordinal)), o.Count(x => SyntheticMeasure.IsAutomatic(x.Hit?.PairResult)))} | {Pct(o.Count(x => x.ScoreLoss.StartsWith("right", StringComparison.Ordinal)), o.Count(x => SyntheticMeasure.IsAutomatic(x.ScoreResult)))} |");
        foreach (var r in new[] { ScreeningAssessment.Hit, ScreeningAssessment.NoHit, ScreeningAssessment.NotApplicable })
            Line($"| automatic {r} precision | {Pct(o.Count(x => x.Hit?.PairResult == r && x.Truth.Expected == r), o.Count(x => x.Hit?.PairResult == r))} | {Pct(o.Count(x => x.ScoreResult == r && x.Truth.Expected == r), o.Count(x => x.ScoreResult == r))} |");

        Line("\nWhere each end result was lost (product)");
        foreach (var g in o.GroupBy(x => x.Loss).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)) Line($"| {g.Key} | {g.Count()} |");
        Line("\nWhere each end result was lost (score band)");
        foreach (var g in o.GroupBy(x => x.ScoreLoss).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)) Line($"| {g.Key} | {g.Count()} |");

        Line("\nExtraction errors");
        foreach (var g in o.SelectMany(x => x.FieldErrors.Select(e => (e, x.Case.Html))).GroupBy(x => $"{x.e} ({(x.Html ? "html" : "txt")})").OrderBy(g => g.Key, StringComparer.Ordinal))
            Line($"| {g.Key} | {g.Count()} |");

        Line("\nStated next to the name in the attached text: read by the identity checks");
        foreach (var (tag, group) in new[] { ("text:age", Core.Shared.EvidenceGroup.StatedAge), ("text:birth-date", Core.Shared.EvidenceGroup.StatedBirthDate), ("text:gender", Core.Shared.EvidenceGroup.StatedGender) })
            foreach (var withGender in new[] { true, false })
            {
                var stated = hits.Where(x => x.Truth.Tags.Contains(tag) && x.Truth.Tags.Contains("text:gender") == withGender && !x.Truth.Tags.Contains("text:masked-name")).ToList();
                if (tag != "text:gender" || withGender)
                    Line($"| {tag}{(tag == "text:gender" ? "" : withGender ? " with gender" : " alone")} | {Pct(stated.Count(x => x.Hit!.Identity.Evidence.ContainsKey(group)), stated.Count)} |");
            }

        Line("\nLegal stage: truth -> read (rules)");
        foreach (var g in staged.GroupBy(x => $"{x.Truth.Kind}{(x.Truth.Tags.Contains("text:judgment") ? " judgment" : "")} {x.Truth.Stage} -> {Stage(x)}{(x.Truth.Tags.FirstOrDefault(t => t.StartsWith("text:other", StringComparison.Ordinal)) is { } m ? " [" + m + "]" : "")}{(x.Truth.Tags.Contains("text:masked-name") ? " [masked]" : "")}")
            .OrderBy(g => g.Key, StringComparer.Ordinal))
            Line($"| {g.Key} | {g.Count()} |");

        Line("\nIdentity: stage 3 table vs score band");
        Identity(Line, "all", hits);
        foreach (var k in Enum.GetValues<RecordKind>()) Identity(Line, k.ToString(), [.. hits.Where(x => x.Truth.Kind == k)]);
        foreach (var d in new[] { "0", "1", "2+" }) Identity(Line, "data carried " + d, [.. hits.Where(x => x.DataCarried == d)]);

        Line("\nWrong automatic identity decisions by kind (items, applied)");
        foreach (var g in hits.Where(x => WrongIdentity(x.Hit!.Identity.Decision == IdentityDecision.NoMatch ? IdentityBand.NoMatch : IdentityBand.Review, x.Truth))
            .GroupBy(x => $"stage 3 {x.Truth.Kind}: {Items(x.Score!)}")) Line($"| {g.Key} | {g.Count()} |");
        foreach (var g in hits.Where(x => WrongIdentity(x.Score!.Band, x.Truth)).GroupBy(x => $"score {x.Score!.Band} {x.Truth.Kind}: {Items(x.Score!)} [{string.Join(",", x.Truth.Tags)}]")
            .OrderBy(g => g.Key, StringComparer.Ordinal)) Line($"| {g.Key} | {g.Count()} |");

        Line("\nPROBABLE_MATCH for another person (items)");
        foreach (var g in hits.Where(x => !x.Truth.SamePerson && x.Hit!.Identity.Decision == IdentityDecision.ProbableMatch).GroupBy(x => $"stage 3 row {x.Hit!.Identity.Stage3Row} {x.Truth.Kind}: {Items(x.Score!)} [{string.Join(",", x.Truth.Tags)}]"))
            Line($"| {g.Key} | {g.Count()} |");
        foreach (var g in hits.Where(x => !x.Truth.SamePerson && x.Score!.Band == IdentityBand.ProbableMatch).GroupBy(x => $"score {x.Truth.Kind}: {Items(x.Score!)} [{string.Join(",", x.Truth.Tags)}]"))
            Line($"| {g.Key} | {g.Count()} |");
        Line("\nReview priority of another person's pair (stage 3)");
        foreach (var g in hits.Where(x => !x.Truth.SamePerson).GroupBy(x => x.Truth.Kind).OrderBy(g => g.Key))
            Line($"| {g.Key} | {string.Join(", ", g.GroupBy(x => x.Hit!.ReviewPriority?.ToString() ?? x.Hit!.PairResult.ToString()).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} {p.Count()}"))} |");
        Line("\nSame person: stage 3 decision by kind");
        foreach (var g in hits.Where(x => x.Truth.SamePerson).GroupBy(x => x.Truth.Kind).OrderBy(g => g.Key))
            Line($"| {g.Key} | {string.Join(", ", g.GroupBy(x => x.Hit!.Identity.Decision).OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Count()}"))} |");

        Line("\nVariations of the same person: count, stage 3 decision, score band");
        foreach (var g in hits.Where(x => x.Truth.SamePerson).SelectMany(x => x.Truth.Tags.Where(t => t.StartsWith("name:", StringComparison.Ordinal) || t.StartsWith("dob:", StringComparison.Ordinal) || t.StartsWith("document:", StringComparison.Ordinal)).Select(t => (t, x)))
            .GroupBy(x => x.t).OrderBy(g => g.Key, StringComparer.Ordinal))
            Line($"| {g.Key} | {g.Count()} | {string.Join(", ", g.GroupBy(x => x.x.Hit!.Identity.Decision).OrderBy(d => d.Key).Select(d => $"{d.Key} {d.Count()}"))} | {string.Join(", ", g.GroupBy(x => x.x.Score!.Band).OrderBy(d => d.Key).Select(d => $"{d.Key} {d.Count()}"))} |");

        Line("\nHeld at REVIEW only by a pending rule (would be NO_MATCH if released)");
        foreach (var g in hits.Where(x => x.Score!.Applied.Contains(ScoringRule.HeldPendingValidation) && x.Score.Total <= ScoringPolicy.Proposed.NoMatch && x.Hit!.Identity.HardConflict is null)
            .GroupBy(x => $"{Items(x.Score!)}: {(x.Truth.SamePerson ? "same person" : "another person")}")) Line($"| {g.Key} | {g.Count()} |");

        Line("\nSensitivity (identity, score band)");
        Line("| policy | decided | auto MATCH (precision) | auto NO_MATCH (precision) | PROBABLE_MATCH (same person) |");
        foreach (var (label, p) in Sensitivity)
        {
            var s = SyntheticMeasure.Rescore(o, p);
            Line($"| {label} | {Pct(s.Count(x => x.Score.Band is IdentityBand.Match or IdentityBand.NoMatch), s.Count)} | {Pct(s.Count(x => x.Score.Band == IdentityBand.Match && x.Truth.SamePerson), s.Count(x => x.Score.Band == IdentityBand.Match))} | {Pct(s.Count(x => x.Score.Band == IdentityBand.NoMatch && !x.Truth.SamePerson), s.Count(x => x.Score.Band == IdentityBand.NoMatch))} | {Pct(s.Count(x => x.Score.Band == IdentityBand.ProbableMatch && x.Truth.SamePerson), s.Count(x => x.Score.Band == IdentityBand.ProbableMatch))} |");
        }

        Line("\nGenerated, not run (path not built)");
        foreach (var g in external.GroupBy(x => x.Scenario)) Line($"| {g.Key} | {g.Count()} documents, {g.Sum(x => x.Persons.Count)} persons, customer named in {g.Count(x => x.Persons.Any(p => p.IsCustomer))} |");
        return sb.ToString();
    }

    public static readonly (string Label, ScoringPolicy Policy)[] Sensitivity =
    [
        ("proposed (60 / 30 / 8 / -25)", ScoringPolicy.Proposed),
        ("T_match 40", ScoringPolicy.Proposed with { Match = 40 }),
        ("T_match 50", ScoringPolicy.Proposed with { Match = 50 }),
        ("T_match 70", ScoringPolicy.Proposed with { Match = 70 }),
        ("T_probable 20", ScoringPolicy.Proposed with { Probable = 20 }),
        ("T_probable 40", ScoringPolicy.Proposed with { Probable = 40 }),
        ("T_probable_exact 15", ScoringPolicy.Proposed with { ProbableExact = 15 }),
        ("T_probable_exact 25", ScoringPolicy.Proposed with { ProbableExact = 25 }),
        ("T_no_match -10", ScoringPolicy.Proposed with { NoMatch = -10 }),
        ("T_no_match -40", ScoringPolicy.Proposed with { NoMatch = -40 }),
    ];

    private static bool WrongIdentity(IdentityBand band, CandidateTruth t) =>
        band == IdentityBand.Match && !t.SamePerson || band == IdentityBand.NoMatch && t.SamePerson;

    private static string Items(IdentityScore s) =>
        string.Join(" + ", s.Items.Select(i => $"{i.Code} {i.Points.ToString(CultureInfo.InvariantCulture)}")) is { Length: > 0 } items
            ? items + (s.Applied.Count > 0 ? $" [{string.Join(",", s.Applied)}]" : "") : "no item" + (s.Applied.Count > 0 ? $" [{string.Join(",", s.Applied)}]" : "");

    private static ConditionStatus? Status(PairOutcome x) => x.Hit!.Conditions.FirstOrDefault(c => c.Type == x.Truth.ListType)?.Status;

    private static string Stage(PairOutcome x) => x.Hit!.Conditions.FirstOrDefault(c => c.Type == x.Truth.ListType)?.LegalStage.ToString() ?? "-";

    private static void Identity(Action<string> line, string label, List<PairOutcome> x)
    {
        if (x.Count == 0) return;
        int S3(IdentityDecision d, bool? same = null) => x.Count(p => p.Hit!.Identity.Decision == d && (same is null || p.Truth.SamePerson == same));
        int Sc(IdentityBand b, bool? same = null) => x.Count(p => p.Score!.Band == b && (same is null || p.Truth.SamePerson == same));
        line($"| {label} | {x.Count} | stage 3: decided {Pct(S3(IdentityDecision.NoMatch), x.Count)}, NO_MATCH right {Pct(S3(IdentityDecision.NoMatch, false), S3(IdentityDecision.NoMatch))}, PROBABLE {S3(IdentityDecision.ProbableMatch)} (same {S3(IdentityDecision.ProbableMatch, true)}), REVIEW {S3(IdentityDecision.Review)} "
            + $"| score: decided {Pct(Sc(IdentityBand.Match) + Sc(IdentityBand.NoMatch), x.Count)}, MATCH right {Pct(Sc(IdentityBand.Match, true), Sc(IdentityBand.Match))}, NO_MATCH right {Pct(Sc(IdentityBand.NoMatch, false), Sc(IdentityBand.NoMatch))}, PROBABLE {Sc(IdentityBand.ProbableMatch)} (same {Sc(IdentityBand.ProbableMatch, true)}), REVIEW {Sc(IdentityBand.Review)} |");
    }
}
