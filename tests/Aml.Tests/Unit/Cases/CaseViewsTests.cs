using System.Buffers.Text;
using System.Text;
using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Core.Identity;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Infra.Txt;
using Aml.Tests.GoldenCases;

namespace Aml.Tests.Unit.Cases;

/// <summary>Case and hit summaries: hit order and limit, a Case without an effective revision, evidence without support; page tokens.</summary>
public class CaseViewsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    private static readonly Lazy<(CaseAnalysis Analysis, HitExplanation Hit)> Gc03 = new(() =>
    {
        var input = TxtCaseReader.Read(GoldenCaseTests.Folder("GC-03"));
        var analysis = CaseAnalyzer.Analyze(input);
        return (analysis, ExplanationBuilder.Build(input, analysis).Hits[0]);
    });

    private static CaseRecord Case(int current, int? effective) => new("c1", "s1", "e1", CaseStatus.InReview, current, effective, T0, T0);

    private static AnalysisRecord Revision(int number, IReadOnlyList<HitExplanation> hits)
    {
        var (analysis, _) = Gc03.Value;
        var explanation = new CaseExplanation(ScreeningAssessment.Review, hits, [], [], null);
        return new AnalysisRecord("c1", number, ExecutionStatus.Completed, ScreeningAssessment.Review, analysis, explanation, null, null, T0);
    }

    [Fact]
    public void Hits_are_ordered_by_priority_then_number_and_the_eleventh_is_omitted()
    {
        var hit = Gc03.Value.Hit;
        var hits = Enumerable.Range(1, 11).Reverse()
            .Select(n => hit with { HitNumber = n, ReviewPriority = n == 11 ? ReviewPriority.High : n % 2 == 0 ? ReviewPriority.Low : ReviewPriority.Normal }).ToList();

        var s = CaseViews.Summary(Case(1, 1), Revision(1, hits));

        Assert.Equal([11, 1, 3, 5, 7, 9, 2, 4, 6, 8], s.Hits!.Select(l => l.HitNumber));
        Assert.Equal(1, s.Omitted);
        Assert.Equal("/api/cases/c1/hits/11?revision=1", s.Hits![0].Link);
        Assert.Equal("/api/cases/c1/revisions/1", s.Revision);
    }

    [Fact]
    public void Without_an_effective_revision_the_result_is_null_and_the_latest_revision_is_linked()
    {
        var partial = CaseViews.Summary(Case(2, null), null);
        Assert.Null(partial.Result);
        Assert.Null(partial.Hits);
        Assert.Equal("/api/cases/c1/revisions/2", partial.Revision);
        Assert.Contains("revision 2 未完成", partial.Conclusion, StringComparison.Ordinal);

        var never = CaseViews.Summary(Case(0, null), null);
        Assert.Null(never.Revision);
        Assert.Equal("尚未分析", never.Conclusion);
    }

    [Fact]
    public void A_newer_incomplete_revision_is_flagged_and_does_not_replace_the_result()
    {
        var s = CaseViews.Summary(Case(3, 2), Revision(2, [Gc03.Value.Hit]));
        Assert.True(s.NewerRevisionIncomplete);
        Assert.Equal(2, s.EffectiveRevision);
        Assert.Contains("revision 3 未完成", s.Conclusion, StringComparison.Ordinal);
        Assert.False(CaseViews.Summary(Case(2, 2), Revision(2, [Gc03.Value.Hit])).NewerRevisionIncomplete);
    }

    private static Evidence E(EvidenceGroup g, Strength s, Direction d = Direction.Support, string? detail = null) =>
        new(g, d, d == Direction.Support ? ComparisonStatus.Exact : ComparisonStatus.Conflict, s, detail);

    private static readonly Evidence Gender = E(EvidenceGroup.Gender, Strength.None, Direction.Conflict, ReasonCodes.GenderConflict);
    private static readonly Evidence Address = E(EvidenceGroup.Address, Strength.Low);

    private static EvidenceGroup[] Decisive(HitExplanation h) => [.. CaseViews.Hit("c1", 1, h).DecisiveEvidence?.Select(e => e.Group) ?? []];

    [Fact]
    public void On_rows_2_to_5_the_strongest_support_decides()
    {
        var hit = Gc03.Value.Hit with
        {
            Stage3Row = 4,
            Support = [E(EvidenceGroup.Name, Strength.Low), E(EvidenceGroup.DateOfBirth, Strength.High), E(EvidenceGroup.Employment, Strength.Medium), E(EvidenceGroup.Document, Strength.Unique)],
            Conflicts = [Gender],
        };
        Assert.Equal([EvidenceGroup.Document, EvidenceGroup.DateOfBirth, EvidenceGroup.Employment], Decisive(hit));
        Assert.Null(CaseViews.Hit("c1", 1, hit).EvidenceNote);
        Assert.Equal(EvidenceGroup.Document, Decisive(hit with { Stage3Row = 5 })[0]);
    }

    private static Evidence Conflict(EvidenceGroup g, string? detail = null) => E(g, Strength.None, Direction.Conflict, detail);

    [Fact]
    public void Otherwise_the_conflicts_named_by_the_reason_codes_decide_in_their_order()
    {
        var hit = Gc03.Value.Hit;
        // Row 1 by a full DOB conflict with a gender conflict too: the evidence is held in group order (gender first), the
        // reason codes put the DOB first, and a conflict no reason code names is left out.
        var row1 = hit with
        {
            Stage3Row = 1,
            Support = [Address],
            Conflicts = [Conflict(EvidenceGroup.Gender, ReasonCodes.GenderConflict), Conflict(EvidenceGroup.Nationality), Conflict(EvidenceGroup.DateOfBirth, ReasonCodes.FullDobConflict)],
            ReasonCodes = [ReasonCodes.FullDobConflict, ReasonCodes.GenderConflict],
        };
        Assert.Equal([EvidenceGroup.DateOfBirth, EvidenceGroup.Gender], Decisive(row1));

        // A hard conflict, even when downgraded for source quality, leads with the evidence carrying its code; the codes say why.
        var hard = hit with
        {
            Stage3Row = null,
            HardConflict = ReasonCodes.ImpossibleAgeAtReportedEvent,
            Support = [Address],
            Conflicts = [Conflict(EvidenceGroup.Gender, ReasonCodes.GenderConflict), Conflict(EvidenceGroup.TemporalRole, ReasonCodes.ImpossibleAgeAtReportedEvent)],
            ReasonCodes = [ReasonCodes.ImpossibleAgeAtReportedEvent, ReasonCodes.SourceQualityDowngrade],
        };
        var h = CaseViews.Hit("c1", 1, hard);
        Assert.Equal([EvidenceGroup.TemporalRole], h.DecisiveEvidence!.Select(e => e.Group));
        Assert.Equal(ReasonCodes.ImpossibleAgeAtReportedEvent, h.HardConflict);
        Assert.Equal([ReasonCodes.ImpossibleAgeAtReportedEvent, ReasonCodes.SourceQualityDowngrade], h.ReasonCodes!);
        Assert.Null(h.EvidenceNote);

        // Rules with no evidence of their own list none: a death before the subject's birth, a non-Person subject.
        var death = CaseViews.Hit("c1", 1, hit with
        {
            Stage3Row = null,
            HardConflict = ReasonCodes.DeathBeforeCustomerBirth,
            Support = [Address],
            Conflicts = [Conflict(EvidenceGroup.Nationality)],
            ReasonCodes = [ReasonCodes.DeathBeforeCustomerBirth],
        });
        Assert.Null(death.DecisiveEvidence);
        Assert.Null(death.EvidenceNote);
        var entity = CaseViews.Hit("c1", 1, hit with { Stage3Row = null, Support = [Address], Conflicts = [], ReasonCodes = [ReasonCodes.NonPersonSubjectReview] });
        Assert.Null(entity.DecisiveEvidence);
        Assert.Equal([ReasonCodes.NonPersonSubjectReview], entity.ReasonCodes!);
    }

    [Fact]
    public void Row_6_without_conflicts_has_only_the_note()
    {
        var none = CaseViews.Hit("c1", 1, Gc03.Value.Hit with { Stage3Row = 6, Support = [], Conflicts = [] });
        Assert.Null(none.DecisiveEvidence);
        Assert.Equal("僅前提條件相符", none.EvidenceNote);
        Assert.Equal("/api/cases/c1/hits/1?view=detail&revision=1", none.Detail);
    }

    [Fact]
    public void Only_source_conditions_that_are_hit_are_listed()
    {
        var c = Gc03.Value.Hit.SourceConditions[0];
        var hit = Gc03.Value.Hit with { SourceConditions = [c with { Type = ListType.Pep, Status = ConditionStatus.NotHit }, c with { Type = ListType.Sie, Status = ConditionStatus.Hit }] };
        Assert.Equal([ListType.Sie], CaseViews.Hit("c1", 1, hit).SourceConditions!);
    }

    [Fact]
    public void A_page_token_round_trips_its_key()
    {
        var at = new DateTimeOffset(2026, 11, 4, 1, 2, 3, TimeSpan.Zero).AddTicks(1234567);
        Assert.True(PageToken.TryDecode(PageToken.Encode(at, "019a-c|1"), out var key));
        Assert.Equal((at, "019a-c|1"), key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("abc")]
    [InlineData("123|")]
    [InlineData("|id")]
    [InlineData("-5|id")]
    [InlineData("99999999999999999999|id")]
    [InlineData("3155378976000000000|id")] // one tick past DateTimeOffset.MaxValue
    public void A_token_this_api_did_not_issue_is_rejected(string text)
    {
        var token = text.Contains(' ', StringComparison.Ordinal) ? text : Base64Url.EncodeToString(Encoding.UTF8.GetBytes(text));
        Assert.False(PageToken.TryDecode(token, out _));
    }
}
