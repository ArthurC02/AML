using System.ComponentModel;
using Aml.Core.Identity;
using Aml.Core.Pages;
using Aml.Core.Ports;
using Aml.Core.Retrieval;
using Aml.Core.Screening;
using Aml.Core.Subjects;

namespace Aml.Core.Cases;

public sealed record HitResult(
    [property: Description("名單（讀入時的結構化內容）")] SourceProfile Profile,
    [property: Description("疑似名單表中合併為此 hit 的列數；收到網頁的人物（WEB:）為 0")] int CandidateRows,
    [property: Description("名單條件")] IReadOnlyList<SourceCondition> Conditions,
    [property: Description("身分判斷與證據")] IdentityResult Identity,
    [property: Description("此組合的對外結果")] ScreeningAssessment PairResult,
    [property: Description("複核優先序")] ReviewPriority? ReviewPriority,
    [property: Description("對外結果的理由碼")] IReadOnlyList<string> PairReasonCodes,
    [property: Description("第 22 條建議，僅供參考")] DispositionRecommendation Disposition,
    [property: Description("AML 風險評分；沒有核准的政策時為 null")] RiskAssessment? Risk = null);

public sealed record CaseAnalysis(
    [property: Description("每筆名單一個 hit")] IReadOnlyList<HitResult> Hits,
    [property: Description("缺名單資料而無法評估的名單鍵；有任何一筆時 revision 不完整")] IReadOnlyList<string> NotEvaluatedProfiles,
    [property: Description("自輸入檔讀入的舊決定")] IReadOnlyList<ImportedDecision> ImportedDecisions,
    [property: Description("分析所用的收到網頁：每頁每個人與受檢對象姓名的比對，候選人與非候選人都列出；沒有網頁時省略")] IReadOnlyList<PageScreening>? Pages = null)
{
    [property: Description("儲存格式版本")]
    public int SchemaVersion { get; init; } = CaseJson.SchemaVersion;
}

public static class CaseAnalyzer
{
    /// <param name="risk">An approved AML risk policy; without one no pair is scored.</param>
    /// <param name="asOf">The analysis date (whether a PEP's roles have all ended); today (UTC) when not given. A service
    /// that stores the analysis passes its own clock's date.</param>
    /// <param name="stageReaders">The configured stage models that vote with the rules on the stage in attached news; none: the rules alone.</param>
    public static CaseAnalysis Analyze(CaseInput input, RiskPolicy? risk = null, DateOnly? asOf = null, IReadOnlyList<IStageReader>? stageReaders = null)
    {
        var today = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var rowsByProfile = input.Rows.GroupBy(r => r.ProfileId).ToList();
        var hits = rowsByProfile.Where(g => input.Profiles.ContainsKey(g.Key)).Select(g => Hit(input, g, risk, today, stageReaders)).ToList();
        var notEvaluated = rowsByProfile.Where(g => !input.Profiles.ContainsKey(g.Key)).Select(g => ProfileKeys.KeyOf(g.Key)).ToList();
        // A received page's persons, screened by name (SOURCE_PAGE): each candidate is a hit, read from the pinned page.
        var pages = input.Pages?.Select(p => PageCandidates.Screen(input.Subject, p)).ToList();
        foreach (var (page, screened) in (input.Pages ?? []).Zip(pages ?? []))
            hits.AddRange(page.Persons!.Zip(screened.Persons).Where(x => x.Second.Candidate).Select(x => PageHit(input, page, x.First, risk, today)));
        return new CaseAnalysis(hits, notEvaluated, input.ImportedDecisions, pages);
    }

    private static HitResult Hit(CaseInput input, IGrouping<string, CandidateRow> rows, RiskPolicy? risk, DateOnly today, IReadOnlyList<IStageReader>? stageReaders)
    {
        var profile = input.Profiles[rows.Key];
        var news = input.NewsByProfileId.GetValueOrDefault(rows.Key);
        var conditions = SourceConditions.Determine(profile, rows.Any(r => r.IsRca), news?.Text, ListFlags.Merge(rows.Select(r => r.Flags ?? ListFlags.None)), today, stageReaders);
        return Pair(input.Subject, profile, rows.Count(), conditions, news, risk, today);
    }

    // The stage was read and voted when the page was read; it is taken from the pinned page, so the models are not asked again.
    private static HitResult PageHit(CaseInput input, PageRecord page, PagePerson person, RiskPolicy? risk, DateOnly today)
    {
        var profile = PageCandidates.Profile(person);
        return Pair(input.Subject, profile, 0, SourceConditions.FromPage(person, profile, page.Text!), PageCandidates.News(page, person), risk, today);
    }

    private static HitResult Pair(ScreenedSubject subject, SourceProfile profile, int rows, IReadOnlyList<SourceCondition> conditions, NewsItem? news,
        RiskPolicy? risk, DateOnly today)
    {
        var identity = IdentityDecisionPolicy.Decide(subject, profile, news);
        var pair = AssessmentPolicy.Assess(conditions, identity);
        var disposition = DispositionPolicy.Recommend(subject, profile, conditions, identity, news is not null, today);
        return new HitResult(profile, rows, conditions, identity, pair.Result, pair.Priority, pair.ReasonCodes, disposition,
            RiskScoring.Assess(conditions, identity, risk));
    }
}
