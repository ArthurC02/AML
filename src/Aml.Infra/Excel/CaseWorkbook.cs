using System.Globalization;
using Aml.Core.Cases;
using Aml.Core.Codes;
using Aml.Core.Explanation;
using Aml.Core.Screening;
using Aml.Core.Shared;
using static Aml.Core.Cases.CaseJson;

namespace Aml.Infra.Excel;

/// <summary>
/// One analysis revision of a Case as a workbook of nine sheets, each one table that restates the stored revision and
/// adds nothing (ADR-0027): 說明 (the Case, lineage, quality and a data dictionary), 命中, 比對, 證據, 來源, 新聞人物,
/// 新聞事件 (the news digest, ADR-0028), 變更 and 代碼.
/// The hit number joins the sheets. Without identifier values (the default) the workbook follows the summary views: no
/// IDENTIFIER field, no compared value and, of personal data, only names.
/// </summary>
public static class CaseWorkbook
{
    private const string Join = "；";

    private sealed record Column(string Header, string Field, string Meaning, bool Identifying = false);

    private sealed record Table(string Name, IReadOnlyList<Column> Columns, IEnumerable<IReadOnlyList<object?>> Rows);

    public static void Write(Stream output, CaseRecord c, AnalysisRecord revision, bool withIdentifierValues) =>
        Xlsx.Write(output, Sheets(c, revision, withIdentifierValues), revision.CreatedAt);

    public static IReadOnlyList<Sheet> Sheets(CaseRecord c, AnalysisRecord r, bool withIdentifierValues)
    {
        var hits = r.Explanation?.Hits ?? [];
        var tables = new[] { Hits(hits), Comparison(hits), Evidence(hits), Sources(hits), NewsPeople(hits), NewsEvents(hits), Changes(r.Explanation?.ChangesFromPreviousRevision) }
            .Select(t => Shown(t, withIdentifierValues)).ToList();
        var codes = Codes(tables);
        var about = About(c, r, withIdentifierValues, tables.Append(codes));
        return [.. new[] { about }.Concat(tables).Append(codes).Select(t => new Sheet(t.Name, [.. t.Columns.Select(col => col.Header)], [.. t.Rows]))];
    }

    // Drops the identifying columns, and their cells, unless identifier values were asked for.
    private static Table Shown(Table t, bool withIdentifierValues)
    {
        if (withIdentifierValues) return t;
        var keep = t.Columns.Select((col, i) => (col, i)).Where(x => !x.col.Identifying).Select(x => x.i).ToList();
        return new(t.Name, [.. keep.Select(i => t.Columns[i])], t.Rows.Select(row => (IReadOnlyList<object?>)[.. keep.Select(i => row[i])]));
    }

    private static Table Hits(IReadOnlyList<HitExplanation> hits) => new("命中",
    [
        new("命中編號", "hitNumber", "hit 編號，各工作表以此對應"),
        new("受檢對象", "screenedSubject", "客戶或關聯人（關係）與姓名", Identifying: true),
        new("名單編號", "profileId", "名單編號"),
        new("名單鍵", "profileKey", "名單鍵（DJ:<名單編號>）"),
        new("名單名稱", "profileName", "名單主要姓名"),
        new("命中的姓名", "matchedNames", "疑似名單表中命中的姓名", Identifying: true),
        new("名單類別", "sourceCategories", "名單類別／子類別"),
        new("RCA", "isRca", "疑似名單表標示 Is RCA? = YES"),
        new("附新聞", "attachedNews", "是否附有新聞"),
        new("名單條件", "sourceConditions[status=HIT].type", "狀態為 HIT 的名單條件"),
        new("身分判斷", "identityDecision", "系統身分判斷"),
        new("第三階段列號", "stage3Row", "第三階段決策表列號（1～6）"),
        new("硬衝突", "hardConflict", "第一階段硬衝突代碼"),
        new("身分理由碼", "reasonCodes", "身分判斷的理由碼"),
        new("對外結果", "pairResult", "此組合的對外結果"),
        new("複核優先序", "reviewPriority", "複核優先序"),
        new("對外理由碼", "pairReasonCodes", "對外結果的理由碼"),
        new("第 22 條建議", "disposition.recommendation", "第 22 條建議，僅供參考"),
        new("第 22 條理由碼", "disposition.reasonCodes", "第 22 條理由碼"),
        new("待查核事項", "disposition.pendingChecks", "尚未執行的查核事項"),
        new("資料品質", "dataQualityFindings", "資料品質 finding"),
        new("無法比對項目", "unknownFields", "缺值或不確定的比對項目"),
        new("若條件改變", "wouldChangeIf[].text", "若證件或完整出生日期可比對，身分判斷會移到哪一列；不代表對外結果"),
        new("摘要：內部資料", "summary.internalData", "受檢對象的姓名、出生日期、國籍、性別與證件是否提供", Identifying: true),
        new("摘要：外部資料", "summary.externalData", "名單類別、條件、出生日期、國籍、性別與新聞，[S#] 引用來源工作表", Identifying: true),
        new("摘要：相符與差異", "summary.agreementsAndDifferences", "相符、衝突與無法比對的項目"),
        new("摘要：關鍵佐證或缺口", "summary.keyEvidenceOrGaps", "關鍵佐證或缺口"),
        new("摘要：結論", "summary.conclusion", "身分判斷、對外結果、理由碼與第 22 條建議"),
        new("新聞摘要", "newsDigest.summary", "附加新聞的人物與事件，依固定句型組成，逐句換行；[S#] 引用來源工作表，細節見新聞人物、新聞事件", Identifying: true),
    ], hits.Select(h => (IReadOnlyList<object?>)
    [
        h.HitNumber, h.ScreenedSubject, h.ProfileId, h.ProfileKey, h.ProfileName, Joined(h.MatchedNames), Joined(h.SourceCategories),
        YesNo(h.IsRca), YesNo(h.AttachedNews), Joined(h.SourceConditions.Where(s => s.Status == ConditionStatus.Hit).Select(s => Code(s.Type))),
        Code(h.IdentityDecision), h.Stage3Row, h.HardConflict, Joined(h.ReasonCodes), Code(h.PairResult), h.ReviewPriority is { } p ? Code(p) : null,
        Joined(h.PairReasonCodes), h.Disposition.Recommendation is { } d ? Code(d) : null, Joined(h.Disposition.ReasonCodes), Joined(h.Disposition.PendingChecks),
        Joined(h.DataQualityFindings), Joined(h.UnknownFields), Joined(h.WouldChangeIf.Select(w => w.Text)),
        h.Summary.InternalData, h.Summary.ExternalData, h.Summary.AgreementsAndDifferences, h.Summary.KeyEvidenceOrGaps, h.Summary.Conclusion,
        h.NewsDigest is { } n ? string.Join("\n", n.Summary) : null,
    ]));

    private static Table NewsPeople(IReadOnlyList<HitExplanation> hits) => new("新聞人物",
    [
        new("命中編號", "hitNumber", "hit 編號"),
        new("姓名", "newsDigest.people[].name", "報導中的姓名或匿稱", Identifying: true),
        new("與名單人物", "newsDigest.people[].match", "PROFILE_NAME、PROFILE_SURNAME 或 OTHER_PERSON"),
        new("身分或職稱", "newsDigest.people[].roles", "報導中緊接在姓名前的身分或職稱"),
        new("出現次數", "newsDigest.people[].mentions", "出現次數"),
    ], hits.SelectMany(h => (h.NewsDigest?.People ?? []).Select(p => (IReadOnlyList<object?>)
        [h.HitNumber, p.Name, Code(p.Match), Joined(p.Roles), p.Mentions])));

    private static Table NewsEvents(IReadOnlyList<HitExplanation> hits) => new("新聞事件",
    [
        new("命中編號", "hitNumber", "hit 編號"),
        new("事件", "newsDigest.events[].type", "事件類型"),
        new("確定程度", "newsDigest.events[].certainty", "STATED、NEGATED 或 HEDGED"),
        new("當事人", "newsDigest.events[].actor", "事件當事人；空白為報導未指明", Identifying: true),
        new("與名單人物", "newsDigest.events[].actorMatch", "當事人與名單人物的關係"),
        new("日期", "newsDigest.events[].date", "日期原文"),
        new("機關", "newsDigest.events[].agency", "地檢署、法院、調查局等"),
        new("罪名", "newsDigest.events[].offences", "同句提到的罪名或違法類型"),
        new("金額", "newsDigest.events[].amount", "金額原文"),
        new("刑度", "newsDigest.events[].penalty", "刑度原文"),
        new("原文", "newsDigest.events[].quote", "事件所在句子的原文", Identifying: true),
    ], hits.SelectMany(h => (h.NewsDigest?.Events ?? []).Select(e => (IReadOnlyList<object?>)
        [h.HitNumber, Code(e.Type), Code(e.Certainty), e.Actor, e.ActorMatch is { } m ? Code(m) : null, e.Date, e.Agency, Joined(e.Offences), e.Amount, e.Penalty, e.Quote])));

    private static Table Comparison(IReadOnlyList<HitExplanation> hits) => new("比對",
    [
        new("命中編號", "hitNumber", "hit 編號"),
        new("比對項目", "comparison[].field", "固定順序：中文姓名、英文姓名／拼音、國籍、出生日期、性別、證件、地址、職業／雇主、其他識別碼"),
        new("受檢對象的值", "comparison[].subjectValue", "受檢對象的值；證件與參考號碼已遮罩", Identifying: true),
        new("名單的值", "comparison[].profileValue", "名單的值；證件已遮罩", Identifying: true),
        new("比對狀態", "comparison[].status", "比對狀態"),
        new("差異碼", "comparison[].difference", "差異代碼，多個以「；」連接"),
        new("備註", "comparison[].note", "給複核人員的說明文字", Identifying: true),
    ], hits.SelectMany(h => h.Comparison.Select(row => (IReadOnlyList<object?>)
        [h.HitNumber, row.Field, row.SubjectValue, row.ProfileValue, Code(row.Status), row.Difference, row.Note])));

    private static Table Evidence(IReadOnlyList<HitExplanation> hits) => new("證據",
    [
        new("命中編號", "hitNumber", "hit 編號"),
        new("證據群組", "support[] / conflicts[].group", "證據群組"),
        new("方向", "direction", "前提、支持、衝突或中性"),
        new("比對狀態", "status", "比對狀態"),
        new("強度", "strength", "支持強度（UNIQUE > HIGH > MEDIUM > LOW > NONE）"),
        new("細節代碼", "detail", "細節代碼"),
        new("說明", "note", "給複核人員的說明文字", Identifying: true),
    ], hits.SelectMany(h => h.Support.Concat(h.Conflicts).Select(e => (IReadOnlyList<object?>)
        [h.HitNumber, Code(e.Group), Code(e.Direction), Code(e.Status), Code(e.Strength), e.Detail, e.Note])));

    private static Table Sources(IReadOnlyList<HitExplanation> hits) => new("來源",
    [
        new("命中編號", "hitNumber", "hit 編號"),
        new("引用編號", "sources[].ref", "摘要中以 [S#] 引用"),
        new("來源編號", "sources[].id", "來源編號"),
        new("名稱", "sources[].name", "來源名稱或新聞出處與標題", Identifying: true),
        new("網址", "sources[].url", "來源網址", Identifying: true),
        new("發布日期", "sources[].publishedOn", "發布日期"),
        new("取得日期", "sources[].retrievedOn", "取得日期"),
        new("用途", "sources[].usedFor", "PROFILE_RECORD、LEGAL_STAGE 或 CONTEXT"),
        new("附於名單", "sources[].attachedToProfile", "是否附於名單"),
    ], hits.SelectMany(h => h.Sources.Select(s => (IReadOnlyList<object?>)
        [h.HitNumber, s.Ref, s.Id, s.Name, s.Url, s.PublishedOn, s.RetrievedOn, s.UsedFor, YesNo(s.AttachedToProfile)])));

    private static Table Changes(IReadOnlyList<PairChange>? changes) => new("變更",
    [
        new("變更種類", "changesFromPreviousRevision[].kind", "與前一個生效 revision 相比：ADDED、REMOVED、RESULT_CHANGED、IDENTITY_CHANGED 或 REASONS_CHANGED"),
        new("名單編號", "profileId", "名單編號"),
        new("名單鍵", "profileKey", "名單鍵"),
        new("變更前", "from", "變更前"),
        new("變更後", "to", "變更後"),
    ], (changes ?? []).Select(ch => (IReadOnlyList<object?>)[Code(ch.Kind), ch.ProfileId, ch.ProfileKey, ch.From, ch.To]));

    // Every catalogued code the other sheets show, a value joined with "；" (or "," in a change) read as several codes.
    private static Table Codes(IEnumerable<Table> tables)
    {
        var used = tables.SelectMany(t => t.Rows).SelectMany(row => row).OfType<string>()
            .SelectMany(v => v.Split(['；', ','])).Select(CodeCatalog.Find).OfType<CodeEntry>()
            .DistinctBy(e => e.Code).OrderBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Code, StringComparer.Ordinal).ToList();
        return new("代碼",
        [
            new("代碼", "code", "代碼（/api/codes/{code}）"),
            new("類別", "kind", "代碼類別"),
            new("中文名稱", "name", "中文名稱"),
            new("說明", "description", "一行說明"),
        ], used.Select(e => (IReadOnlyList<object?>)[e.Code, e.Kind, e.Name, e.Description]));
    }

    private static Table About(CaseRecord c, AnalysisRecord r, bool withIdentifierValues, IEnumerable<Table> tables)
    {
        var rows = new List<IReadOnlyList<object?>>
        {
            Item("案件", "案件編號", c.CaseId),
            Item("案件", "受檢對象參考碼", c.SubjectRef),
            Item("案件", "觸發事件編號", c.TriggerEventId),
            Item("案件", "案件狀態", Code(c.Status)),
            Item("案件", "revision", r.Revision),
            Item("案件", "生效 revision", c.EffectiveRevision),
            Item("案件", "執行狀態", Code(r.ExecutionStatus)),
            Item("案件", "分析時間（UTC）", r.CreatedAt),
            Item("案件", "案件結果", Code(r.CaseResult)),
            // The case conclusion speaks of the effective revision; another revision only says which one is effective.
            Item("案件", "結論", r.Revision == c.EffectiveRevision ? CaseViews.Summary(c, r).Conclusion
                : $"此 revision 不是生效 revision；{(c.EffectiveRevision is { } e ? $"生效 revision 為 {e}" : "尚無生效 revision")}"),
            Item("案件", "無法評估的名單", Joined(r.Result.NotEvaluatedProfiles)),
            Item("案件", "內容", withIdentifierValues
                ? "完整：含識別值與比對值，依個人資料規定保管"
                : "摘要：不含識別值與比對值，個人資料只含姓名"),
            Item("案件", "與前一生效 revision 的變更", r.Explanation?.ChangesFromPreviousRevision is { } changes ? $"{changes.Count} 項，見「變更」" : "第一次分析，無前一個生效 revision"),
        };
        if (r.Lineage is { } l)
        {
            rows.AddRange(l.InputFiles.Select(f => Item("沿革", $"輸入檔 {f.Name}", f.Sha256)));
            rows.AddRange(l.Profiles.Select(p => Item("沿革", $"名單 {p.ProfileId}",
                $"{p.Origin}{(p.ListVersion is { } v ? $" 第 {v} 版" : "")}{(p.ContentHash is { } h ? $"，內容雜湊 {h}" : "")}")));
            rows.Add(Item("沿革", "名單條件規則版本", l.SourcePolicyVersion));
            rows.Add(Item("沿革", "代碼字典版本", l.CodeCatalogVersion));
            rows.Add(Item("沿革", "儲存格式版本", l.SchemaVersion));
            rows.Add(Item("沿革", "應用程式版本", l.AppVersion));
            rows.Add(Item("沿革", "分析基準日", l.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }
        if (r.Quality is { } q)
        {
            rows.Add(Item("品質", "hit 數", q.Hits));
            rows.Add(Item("品質", "比對格數", q.ComparisonCells));
            rows.Add(Item("品質", "缺值格數", q.MissingCells));
            rows.Add(Item("品質", "不確定格數", q.UncertainCells));
            rows.Add(Item("品質", "衝突格數", q.ConflictCells));
            rows.Add(Item("品質", "缺值率", q.MissingRate));
            rows.Add(Item("品質", "不確定率", q.UncertainRate));
            rows.Add(Item("品質", "衝突率", q.ConflictRate));
            rows.Add(Item("品質", "無法評估的名單數", q.NotEvaluatedProfiles));
            rows.AddRange(q.FindingCounts.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => Item("品質", $"finding {f.Key}", f.Value)));
        }
        rows.AddRange(tables.SelectMany(t => t.Columns.Select(col => Item("欄位", $"{t.Name}．{col.Header}", $"{col.Field}：{col.Meaning}"))));
        return new("說明", [new("類別", "", ""), new("項目", "", ""), new("值", "", "")], rows);

        static IReadOnlyList<object?> Item(string group, string name, object? value) => [group, name, value];
    }

    private static string? Joined(IEnumerable<string> values) => string.Join(Join, values) is { Length: > 0 } s ? s : null;

    private static string YesNo(bool value) => value ? "是" : "否";
}
