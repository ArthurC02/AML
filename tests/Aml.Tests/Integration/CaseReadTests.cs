using Aml.Core.Cases;
using Aml.Infra.Txt;

namespace Aml.Tests.Integration;

/// <summary>Reading Cases back: the revision list and keyset-paged search.</summary>
public sealed class CaseReadTests : ServiceTestBase
{
    [Fact]
    public async Task Revisions_are_listed_with_the_effective_one_and_their_change_counts()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = CaseFiles();
        var input = TxtCaseReader.ReadFiles(files);
        var c = await _caseService.CreateAsync("s", "e", input, ct, files);
        await _caseService.ReanalyzeAsync(c.CaseId, input, ct, files);
        Assert.True(await _cases.AcceptAnalysisAsync(Failed(c.CaseId, 3), CaseStatus.InReview, null, ct));
        var second = (await _cases.GetAnalysisAsync(c.CaseId, 2, ct))!;
        PairChange change = new("DJ:1", PairChangeKind.ResultChanged, "REVIEW", "HIT");
        Assert.True(await _cases.AcceptAnalysisAsync(second with { Revision = 4, Explanation = second.Explanation! with { ChangesFromPreviousRevision = [change, change] } },
            CaseStatus.InReview, null, ct));

        var list = (await _caseService.GetRevisionsAsync(c.CaseId, ct))!;

        Assert.Equal([1, 2, 3, 4], list.Select(r => r.Revision));
        Assert.Equal([false, false, false, true], list.Select(r => r.Effective));
        Assert.Equal([null, 0, null, 2], list.Select(r => r.Changes));
        Assert.Equal(ExecutionStatus.Failed, list[2].ExecutionStatus);
        Assert.Null(await _caseService.GetRevisionsAsync("no-such-case", ct));
    }

    [Fact]
    public async Task Search_pages_through_every_match_once_in_key_order()
    {
        var ct = TestContext.Current.CancellationToken;
        // Two Cases share a creation time, so the id breaks the tie across a page boundary; times one tick apart check the
        // token and the stored text keep full precision.
        DateTimeOffset[] times = [T0, T0.AddTicks(1), T0.AddTicks(1), T0.AddTicks(2), T0.AddTicks(3)];
        for (var i = 0; i < times.Length; i++)
            await _cases.CreateAsync(new CaseRecord($"case-{i}", "subject-a", $"evt-{i}", CaseStatus.Open, 0, null, times[i], times[i]), ct);
        await _cases.CreateAsync(new CaseRecord("case-other", "subject-b", "evt-0", CaseStatus.Open, 0, null, T0, T0), ct);

        var seen = new List<string>();
        (DateTimeOffset, string)? after = null;
        for (var pages = 0; ; pages++)
        {
            var page = await _caseService.SearchAsync("subject-a", null, 2, after, ct);
            seen.AddRange(page.Items.Select(i => i.CaseId));
            if (page.NextPageToken is null) break;
            Assert.True(PageToken.TryDecode(page.NextPageToken, out var key));
            after = key;
            Assert.True(pages < 5, "paging does not end");
        }

        Assert.Equal(["case-0", "case-1", "case-2", "case-3", "case-4"], seen);
        Assert.Equal(["case-0", "case-other"], (await _caseService.SearchAsync(null, "evt-0", 10, null, ct)).Items.Select(i => i.CaseId));
        Assert.Equal(["case-other"], (await _caseService.SearchAsync("subject-b", "evt-0", 10, null, ct)).Items.Select(i => i.CaseId));
    }
}
