using System.ComponentModel;
using Aml.Core.Cases;
using Aml.Core.Identity;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Explanation;

public sealed record ComparisonRow(
    [property: Description("比對項目（固定順序：中文姓名、英文姓名／拼音、國籍、出生日期、性別、證件、地址、職業／雇主、其他識別碼）")] string Field,
    [property: Description("受檢對象的值；證件與參考號碼已遮罩")][property: Sensitivity(Sensitivity.Identifier)] string? SubjectValue,
    [property: Description("名單的值；證件已遮罩")][property: Sensitivity(Sensitivity.Identifier)] string? ProfileValue,
    [property: Description("比對狀態，見 /api/codes")] ComparisonStatus Status,
    [property: Description("差異代碼，多個以「；」連接，見 /api/codes")] string? Difference,
    [property: Description("給複核人員的說明文字，例如報導年齡的推算")][property: Sensitivity(Sensitivity.Personal)] string? Note = null);

public sealed record RelatedParty(
    [property: Description("SUBJECT：銀行資料記載；PROFILE：名單記載")] string Side,
    [property: Description("關係人姓名或公司名稱")][property: Sensitivity(Sensitivity.Personal)] string Name,
    [property: Description("關係，依來源原文")] string Relation,
    [property: Description("是否為公司或組織")] bool IsEntity,
    [property: Description("名單記載的關係人名單編號")] string? ProfileId = null);

// Restates recorded facts only and adds none.
public sealed record ExplanationSummary(
    [property: Description("內部資料：受檢對象的姓名、出生日期、國籍、性別與證件是否提供")][property: Sensitivity(Sensitivity.Personal)] string InternalData,
    [property: Description("外部資料：名單類別、條件、出生日期、國籍、性別與新聞，[S#] 引用來源")][property: Sensitivity(Sensitivity.Personal)] string ExternalData,
    [property: Description("相符、衝突與無法比對的項目")] string AgreementsAndDifferences,
    [property: Description("關鍵佐證或缺口")] string KeyEvidenceOrGaps,
    [property: Description("結論：身分判斷、對外結果、理由碼與第 22 條建議")] string Conclusion);

public sealed record HitExplanation(
    [property: Description("hit 編號，從 1 開始")] int HitNumber,
    [property: Description("受檢對象：客戶或關聯人（關係）與姓名")][property: Sensitivity(Sensitivity.Personal)] string ScreenedSubject,
    [property: Description("名單編號（/api/profiles 路由使用）")] string ProfileId,
    [property: Description("名單鍵（DJ:<名單編號>）")] string ProfileKey,
    [property: Description("名單主要姓名")][property: Sensitivity(Sensitivity.Personal)] string? ProfileName,
    [property: Description("疑似名單表中命中的姓名")][property: Sensitivity(Sensitivity.Personal)] IReadOnlyList<string> MatchedNames,
    [property: Description("疑似名單表標示 Is RCA? = YES")] bool IsRca,
    [property: Description("名單類別／子類別")] IReadOnlyList<string> SourceCategories,
    [property: Description("名單條件（與身分判斷各自獨立）")] IReadOnlyList<SourceCondition> SourceConditions,
    [property: Description("是否附有新聞")] bool AttachedNews,
    [property: Description("固定順序的比對表")] IReadOnlyList<ComparisonRow> Comparison,
    [property: Description("雙方記載的關係人")] IReadOnlyList<RelatedParty> RelatedParties,
    [property: Description("支持同一人的證據")] IReadOnlyList<Evidence> Support,
    [property: Description("衝突的證據")] IReadOnlyList<Evidence> Conflicts,
    [property: Description("缺值或不確定的比對項目")] IReadOnlyList<string> UnknownFields,
    [property: Description("第一階段硬衝突代碼")] string? HardConflict,
    [property: Description("第三階段決策表列號（1～6）")] int? Stage3Row,
    [property: Description("對比式解釋：若證件或完整出生日期可比對，身分判斷會移到哪一列；不代表對外結果")] IReadOnlyList<WouldChangeIf> WouldChangeIf,
    [property: Description("系統身分判斷")] IdentityDecision IdentityDecision,
    [property: Description("此組合的對外結果")] ScreeningAssessment PairResult,
    [property: Description("複核優先序")] ReviewPriority? ReviewPriority,
    [property: Description("對外結果的理由碼")] IReadOnlyList<string> PairReasonCodes,
    [property: Description("第 22 條建議，僅供參考")] DispositionRecommendation Disposition,
    [property: Description("身分判斷的理由碼")] IReadOnlyList<string> ReasonCodes,
    [property: Description("資料品質 finding，見 /api/codes")] IReadOnlyList<string> DataQualityFindings,
    [property: Description("來源；Ref 在摘要中以 [S#] 引用")] IReadOnlyList<ExplanationSource> Sources,
    [property: Description("五段式摘要")] ExplanationSummary Summary);

public sealed record ExplanationSource(
    [property: Description("摘要中的引用編號（S1、S2…）")] string Ref,
    [property: Description("來源編號")] string Id,
    [property: Description("來源名稱或新聞出處與標題")] string Name,
    [property: Description("來源網址")] string? Url,
    [property: Description("發布日期")] string? PublishedOn,
    [property: Description("取得日期")] string? RetrievedOn,
    [property: Description("用途：PROFILE_RECORD、LEGAL_STAGE、CONTEXT")] string UsedFor,
    [property: Description("是否附於名單")] bool AttachedToProfile);

public sealed record CaseExplanation(
    [property: Description("案件層級結果")] ScreeningAssessment CaseResult,
    [property: Description("每組受檢對象與名單的解釋")] IReadOnlyList<HitExplanation> Hits,
    [property: Description("缺名單資料而無法評估的名單鍵")] IReadOnlyList<string> NotEvaluatedProfiles,
    [property: Description("自輸入檔讀入的舊決定，從不成為系統判斷")] IReadOnlyList<ImportedDecision> ImportedDecisions,
    [property: Description("與前一個生效 revision 的差異；第一次分析為 null")] IReadOnlyList<PairChange>? ChangesFromPreviousRevision)
{
    [property: Description("儲存格式版本")]
    public int SchemaVersion { get; init; } = CaseJson.SchemaVersion;
}

public static class SourceUse
{
    public const string ProfileRecord = "PROFILE_RECORD";
    public const string LegalStage = "LEGAL_STAGE";
    public const string Context = "CONTEXT";
}
