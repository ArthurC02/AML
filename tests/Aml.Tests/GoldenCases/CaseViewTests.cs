using System.Reflection;
using System.Text.Encodings.Web;
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
        // Not escaped, so a Chinese name or address in a view reads as itself rather than as escape sequences.
        var plain = new JsonSerializerOptions(CaseJson.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var views = JsonSerializer.Serialize(summary, plain)
            + string.Concat(rev.Explanation!.Hits.Select(h => JsonSerializer.Serialize(CaseViews.Hit(c.CaseId, 1, h), plain)));

        // A comparison cell is IDENTIFIER whatever it compares. Each whole cell over one character (男, 女) is checked, and
        // so is each joined value (、) and a masked number without its label ("PASSPORT ", "參考號碼 "): a part with a
        // character from U+3400 up (CJK) from 2 characters, any other part from 4 (TW, VN or a 3-digit number is no
        // identifier on its own). The views show the Profile's main name, so a value inside it proves nothing.
        var shown = rev.Explanation.Hits.Select(h => h.ProfileName).OfType<string>().ToList();
        var identifiers = Values(rev, Sensitivity.Identifier)
            .SelectMany(cell => Parts(cell).Where(v => v.Length >= (v.Any(ch => ch >= '㐀') ? 2 : 4)).Prepend(cell).Where(v => v.Length > 1))
            .Where(v => !shown.Any(n => n.Contains(v, StringComparison.Ordinal))).Distinct().ToList();
        Assert.NotEmpty(identifiers);
        foreach (var value in identifiers) Assert.DoesNotContain(value, views, StringComparison.Ordinal);
        Assert.True(JsonSerializer.Serialize(summary, CaseJson.Options).Length <= 0.3 * JsonSerializer.Serialize(rev, CaseJson.Options).Length);
    }

    // A cell's joined values; a masked number ("{Type} A12*****89") loses its label.
    internal static IEnumerable<string> Parts(string cell) =>
        cell.Split('、').Select(p => p.Contains('*') && p.LastIndexOf(' ') is var at and > 0 ? p[(at + 1)..] : p);

    // Every non-empty value of the fields marked with the level, masked or not.
    private static IEnumerable<string> Values(object? o, Sensitivity level)
    {
        if (o is null or string or ValueType) yield break;
        if (o is System.Collections.IEnumerable items)
        {
            foreach (var item in items)
                foreach (var v in Values(item, level)) yield return v;
            yield break;
        }
        foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            var value = p.GetValue(o);
            if (p.GetCustomAttribute<SensitivityAttribute>()?.Level == level && value is string { Length: > 0 } s)
                yield return s;
            foreach (var v in Values(value, level)) yield return v;
        }
    }
}
