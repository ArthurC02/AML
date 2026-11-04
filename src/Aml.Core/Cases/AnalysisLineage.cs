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

/// <summary>What one revision was computed from: input files, Profile versions and the versions of every rule set.</summary>
public sealed record AnalysisLineage(
    [property: Description("輸入檔與其雜湊")] IReadOnlyList<InputFile> InputFiles,
    [property: Description("分析用到的每一筆名單與其來源版本")] IReadOnlyList<ProfileUse> Profiles,
    [property: Description("名單條件規則版本")] string SourcePolicyVersion,
    [property: Description("Fellegi-Sunter 影子模型版本")] string ModelVersion,
    [property: Description("代碼字典版本")] string CodeCatalogVersion,
    [property: Description("儲存格式（JSON Schema）版本")] int SchemaVersion,
    [property: Description("應用程式版本（組件 informational version）")] string AppVersion)
{
    public const string ListDatabase = "LIST_DATABASE";
    public const string CaseFiles = "CASE_FILES";

    public static string CurrentAppVersion { get; } =
        typeof(AnalysisLineage).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static AnalysisLineage Of(CaseInput input, IReadOnlyList<ProfileUse> profiles) => new(
        (input.SourceHashes ?? new Dictionary<string, string>()).OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new InputFile(f.Key, f.Value)).ToList(),
        profiles, SourceConditions.PolicyVersion, FellegiSunter.ModelVersion, CodeCatalog.Version, CaseJson.SchemaVersion, CurrentAppVersion);
}
