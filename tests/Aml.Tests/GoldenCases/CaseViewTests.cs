using System.Reflection;
using System.Text.Json;
using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Core.Shared;
using Aml.Infra.Txt;

namespace Aml.Tests.GoldenCases;

/// <summary>The Case and hit summaries of every Golden Case restate the full record, leave identifiers out and stay small.</summary>
public class CaseViewTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    internal static (CaseRecord Case, AnalysisRecord Revision) Stored(string gc)
    {
        var input = TxtCaseReader.Read(GoldenCaseTests.Folder(gc));
        var analysis = CaseAnalyzer.Analyze(input);
        var explanation = ExplanationBuilder.Build(input, analysis);
        var revision = new AnalysisRecord("c1", 1, CaseLifecycle.Execution(analysis), CaseLifecycle.Aggregate(analysis), analysis, explanation,
            AnalysisLineage.Of(input, []), AnalysisQuality.Of(explanation.Hits, input.Subject.Findings, analysis.NotEvaluatedProfiles.Count), T0);
        var effective = revision.ExecutionStatus == ExecutionStatus.Completed ? 1 : (int?)null;
        return (new CaseRecord("c1", "s1", "e1", CaseLifecycle.AfterAnalysis(analysis, CaseStatus.Open), 1, effective, T0, T0), revision);
    }

    [Theory]
    [MemberData(nameof(SchemaTests.Cases), MemberType = typeof(SchemaTests))]
    public void Summaries_restate_the_full_record(string gc)
    {
        var (c, rev) = Stored(gc);
        var summary = CaseViews.Summary(c, rev);
        var hits = rev.Explanation!.Hits;

        Assert.Equal(rev.CaseResult, summary.Result);
        Assert.Equal(hits.Select(h => h.HitNumber).Order(), (summary.Hits ?? []).Select(l => l.HitNumber).Order());
        foreach (var h in hits)
        {
            var line = summary.Hits!.Single(l => l.HitNumber == h.HitNumber);
            Assert.Equal((h.ProfileKey, h.PairResult, h.ReviewPriority, h.IdentityDecision), (line.ProfileKey, line.PairResult, line.ReviewPriority, line.IdentityDecision));
            var hs = CaseViews.Hit(c.CaseId, 1, h);
            Assert.Equal((h.Stage3Row, h.PairResult), (hs.Stage3Row, hs.PairResult));
            Assert.Equal(h.WouldChangeIf, hs.WouldChangeIf ?? []);
            Assert.All(hs.DecisiveEvidence ?? [], e => Assert.Contains(h.Support.Concat(h.Conflicts), s => (s.Group, s.Status, s.Strength, s.Detail) == (e.Group, e.Status, e.Strength, e.Detail)));
        }
    }

    [Theory]
    [MemberData(nameof(SchemaTests.Cases), MemberType = typeof(SchemaTests))]
    public void Summaries_hold_no_identifier_values_and_are_at_most_30_percent_of_the_record(string gc)
    {
        var (c, rev) = Stored(gc);
        var summary = CaseViews.Summary(c, rev);
        var views = JsonSerializer.Serialize(summary, CaseJson.Options)
            + string.Concat(rev.Explanation!.Hits.Select(h => JsonSerializer.Serialize(CaseViews.Hit(c.CaseId, 1, h), CaseJson.Options)));

        foreach (var value in IdentifierValues(rev)) Assert.DoesNotContain(value, views, StringComparison.Ordinal);
        Assert.True(JsonSerializer.Serialize(summary, CaseJson.Options).Length <= 0.3 * JsonSerializer.Serialize(rev, CaseJson.Options).Length);
    }

    // Values of fields marked IDENTIFIER that hold a digit: numbers and dates, masked or not. ponytail: the digit filter drops
    // the names an IDENTIFIER comparison cell can also hold, which the summary keeps as the PERSONAL profile name.
    private static IEnumerable<string> IdentifierValues(object? o)
    {
        if (o is null or string or ValueType) yield break;
        if (o is System.Collections.IEnumerable items)
        {
            foreach (var item in items)
                foreach (var v in IdentifierValues(item)) yield return v;
            yield break;
        }
        foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            var value = p.GetValue(o);
            if (p.GetCustomAttribute<SensitivityAttribute>()?.Level == Sensitivity.Identifier && value is string s && s.Any(char.IsAsciiDigit))
                yield return s;
            foreach (var v in IdentifierValues(value)) yield return v;
        }
    }
}
