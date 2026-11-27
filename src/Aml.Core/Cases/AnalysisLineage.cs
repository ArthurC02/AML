using System.ComponentModel;
using System.Reflection;
using Aml.Core.Codes;
using Aml.Core.Identity;
using Aml.Core.Screening;
using Aml.Core.Subjects;

namespace Aml.Core.Cases;

public sealed record InputFile(
    [property: Description("輸入檔名")] string Name,
    [property: Description("檔案內容的 SHA-256（multipart 為上傳位元組；JSON 輸入與重新分析為文字的 UTF-8）")] string Sha256);

public sealed record ProfileUse(
    [property: Description("名單編號（不含 DJ: 前綴）")] string ProfileId,
    [property: Description("LIST_DATABASE：內容與名單庫某一版相同；CASE_FILES：只來自案件檔")] string Origin,
    [property: Description("相同內容的名單庫版本；只來自案件檔時為 null")] int? ListVersion,
    [property: Description("正規化匯出文字的 SHA-256，與名單庫的內容雜湊同一算法")] string? ContentHash);

public sealed record PageUse(
    [property: Description("網頁編號")] string PageId,
    [property: Description("正規化網址")] string Url,
    [property: Description("取得時間")] DateTimeOffset? FetchedAt,
    [property: Description("回應內容的 SHA-256")] string? ContentHash,
    [property: Description("內文（UTF-8）的 SHA-256：此 revision 所用的內文版本")] string TextHash);

// Records only what ran: the rule sets, and the news models when they read an attached article; a model outside the
// analysis, such as the Fellegi-Sunter weight, is not (ADR-0023).
public sealed record AnalysisLineage(
    [property: Description("輸入檔與其雜湊")] IReadOnlyList<InputFile> InputFiles,
    [property: Description("分析用到的每一筆名單與其來源版本")] IReadOnlyList<ProfileUse> Profiles,
    [property: Description("名單條件規則版本")] string SourcePolicyVersion,
    [property: Description("代碼字典版本")] string CodeCatalogVersion,
    [property: Description("儲存格式（JSON Schema）版本")] int SchemaVersion,
    [property: Description("應用程式版本（組件 informational version）")] string AppVersion,
    [property: Description("分析基準日（UTC 日期）：PEP 職務是否都已結束以此日判斷")] DateOnly AsOf,
    [property: Description("讀了附加新聞的新聞模型版本；未設定模型或沒有命中附有新聞時為 null")] string? NewsModelVersion = null,
    [property: Description("分析所用的收到網頁與其內文版本；網頁固定在案件輸入檔中，重新分析讀同一版本；沒有網頁時省略")] IReadOnlyList<PageUse>? Pages = null)
{
    public const string ListDatabase = "LIST_DATABASE";
    public const string CaseFiles = "CASE_FILES";

    public static string CurrentAppVersion { get; } =
        typeof(AnalysisLineage).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static AnalysisLineage Of(CaseInput input, IReadOnlyList<ProfileUse> profiles, DateOnly asOf) => new(
        InputFilesByName(input), profiles, SourceConditions.PolicyVersion, CodeCatalog.Version, CaseJson.SchemaVersion, CurrentAppVersion, asOf,
        Pages: input.Pages?.Select(p => new PageUse(p.PageId, p.Url, p.FetchedAt, p.ContentHash, Aml.Core.Pages.PageSnapshots.TextHash(p.Text!))).ToList());

    private static List<InputFile> InputFilesByName(CaseInput input) =>
        (input.SourceHashes ?? new Dictionary<string, string>()).OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new InputFile(f.Key, f.Value)).ToList();
}
