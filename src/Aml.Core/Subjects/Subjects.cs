using System.ComponentModel;
using Aml.Core.Shared;

namespace Aml.Core.Subjects;

public enum SubjectRole { Customer, RelatedParty }

public sealed record Address(string Role, string Raw, string? Normalized, string? Country, ValueState State = ValueState.Present, string? Detail = null)
{
    /// <summary>The block's country field contradicts the address text (FIELD_CONFLICT).</summary>
    public bool Conflict { get; init; }
}

public sealed record Document(
    [property: Description("證件類型，例如 TW_NATIONAL_ID、PASSPORT")] string? Type,
    [property: Description("證件號碼")][property: Sensitivity(Sensitivity.Identifier)] string Number,
    [property: Description("發證國（ISO 3166-1 alpha-2）")] string? IssuingCountry = null)
{
    private const string TaiwanTypePrefix = "TW_";
    private const string Taiwan = "TW";

    [property: Description("發證國；台灣證件類型隱含 TW")]
    public string? Country => IssuingCountry ?? (Type?.StartsWith(TaiwanTypePrefix, StringComparison.Ordinal) == true ? Taiwan : null);
}

public sealed record Employment(string Organization, string? Title, PartialDate? From, PartialDate? To);

public sealed record Relative(string Name, string Relation, bool IsEntity = false);

// Only this customer's or related party's own data is compared with a Profile.
public sealed record ScreenedSubject(
    SubjectRole Role,
    string? Relationship,
    IReadOnlyList<string> Names,
    Gender? Gender,
    PartialDate? DateOfBirth,
    IReadOnlyList<string> Nationalities,
    Document? Document,
    string? ReferenceNumber,
    FieldValue? TaxId,
    string? Occupation,
    IReadOnlyList<Address> Addresses,
    IReadOnlyList<string> Findings,
    IReadOnlyList<Employment>? Employments = null,
    IReadOnlyList<Relative>? Relatives = null,
    string? PartyType = null)
{
    /// <summary>A legal person or group (客戶類型 法人): compared on entity identifiers, never on DOB or nationality rules.</summary>
    public bool IsEntity => PartyType is { } t && !IsNaturalPersonType(t);

    private static bool IsNaturalPersonType(string partyType) =>
        NaturalPersonWords.Any(w => partyType.Contains(w, StringComparison.Ordinal))
        || NaturalPersonLabels.Contains(partyType.Trim(), StringComparer.OrdinalIgnoreCase);

    private static readonly string[] NaturalPersonWords = ["個", "自然人"];
    private static readonly string[] NaturalPersonLabels = ["Person", "Individual", "Natural Person"];
}

public sealed record SourceCategory([property: Description("名單類別")] string Category, [property: Description("名單子類別")] string? Subcategory);

public sealed record ProfileRole(
    [property: Description("職稱")] string Title,
    [property: Description("機構")] string Organization,
    [property: Description("起始日期")] PartialDate? From,
    [property: Description("結束日期")] PartialDate? To = null);

public sealed record ProfileRelationship(
    [property: Description("關係人的名單編號")] string ProfileId,
    [property: Description("關係人姓名或名稱")][property: Sensitivity(Sensitivity.Personal)] string Name,
    [property: Description("關係，依來源原文")] string Relation,
    [property: Description("是否為公司或組織")] bool IsEntity = false);

public sealed record OtherIdentifier([property: Description("識別碼名稱")] string Label, [property: Description("號碼")][property: Sensitivity(Sensitivity.Identifier)] string Number);

public sealed record OfficialListEntry(
    [property: Description("發布機關")] string Authority,
    [property: Description("名單名稱")] string List,
    [property: Description("狀態")] string Status,
    [property: Description("罪名")] string? Offence);

public sealed record SourceProfile(
    [property: Description("名單鍵（DJ:<名單編號>）")] string ProfileKey,
    [property: Description("性別")] Gender? Gender,
    [property: Description("姓名與別名")][property: Sensitivity(Sensitivity.Personal)] IReadOnlyList<string> Names,
    [property: Description("出生日期（可能只有年或年月）")][property: Sensitivity(Sensitivity.Personal)] IReadOnlyList<PartialDate> DatesOfBirth,
    [property: Description("國籍（ISO 3166-1 alpha-2）")] IReadOnlyList<string> Citizenship,
    [property: Description("地址國家")] IReadOnlyList<string> AddressCountries,
    [property: Description("名單類別")] IReadOnlyList<SourceCategory> Categories,
    [property: Description("關鍵字")] IReadOnlyList<string> Keywords,
    [property: Description("職務")] IReadOnlyList<ProfileRole> Roles,
    [property: Description("關係人")] IReadOnlyList<ProfileRelationship> Relationships,
    [property: Description("官方名單記錄")] OfficialListEntry? OfficialList,
    [property: Description("資料品質 finding")] IReadOnlyList<string> Findings,
    [property: Description("名單引用的來源")] IReadOnlyList<SourceReference> Sources,
    [property: Description("身分證件")] IReadOnlyList<Document>? Documents = null,
    [property: Description("地址")][property: Sensitivity(Sensitivity.Personal)] IReadOnlyList<string>? AddressLines = null,
    [property: Description("死亡日期")][property: Sensitivity(Sensitivity.Personal)] PartialDate? DateOfDeath = null,
    [property: Description("記錄類型：Person 或組織、船舶等")] string RecordType = "Person",
    [property: Description("非證件的識別碼")] IReadOnlyList<OtherIdentifier>? OtherIdentifiers = null,
    [property: Description("指控相關國家")] IReadOnlyList<string>? AllegationCountries = null);

public sealed record SourceReference(
    [property: Description("來源編號")] string Id,
    [property: Description("來源名稱")] string Name,
    [property: Description("網址")] string? Url,
    [property: Description("發布日期（ISO，依來源精度）")] string? PublishedOn,
    [property: Description("取得日期（ISO）")] string? RetrievedOn);

public sealed record NewsItem(string ProfileId, string? DocumentId, string Title, string? Publisher, PartialDate? PublishedOn, string Text);

/// <summary>A source flag exactly as read (extraction-and-normalization.md#source-flags). NO is a value, not unknown.</summary>
public enum FlagValue { Missing, Yes, No, Unparseable }

// Is RCA? is kept as read, so a NO can be told from a blank, but it is not one of the three list flags.
public sealed record ListFlags(FlagValue Pep, FlagValue Blacklist, FlagValue AdverseNews, FlagValue Rca = FlagValue.Missing)
{
    public static readonly ListFlags None = new(FlagValue.Missing, FlagValue.Missing, FlagValue.Missing);

    public bool AllMissing => Pep == FlagValue.Missing && Blacklist == FlagValue.Missing && AdverseNews == FlagValue.Missing;

    public static ListFlags Merge(IEnumerable<ListFlags> rows)
    {
        var list = rows.ToList();
        return new(Strongest(list.Select(f => f.Pep)), Strongest(list.Select(f => f.Blacklist)), Strongest(list.Select(f => f.AdverseNews)), Strongest(list.Select(f => f.Rca)));
    }

    private static FlagValue Strongest(IEnumerable<FlagValue> v) =>
        v.Contains(FlagValue.Yes) ? FlagValue.Yes : v.Contains(FlagValue.Unparseable) ? FlagValue.Unparseable
        : v.Contains(FlagValue.No) ? FlagValue.No : FlagValue.Missing;
}

// Legacy columns become an ImportedDecision only, never evidence.
public sealed record CandidateRow(string MatchedName, string ProfileId, string? LegacyReason, bool IsRca, ListFlags? Flags = null);

public sealed record ImportedDecision(
    [property: Description("來源欄位")] string Field,
    [property: Description("原文內容")][property: Sensitivity(Sensitivity.Personal)] string Value,
    [property: Description("是否經獨立驗證；舊決定永遠不是系統判斷")] bool IndependentlyVerified = false);

public sealed record CaseInput(
    ScreenedSubject Subject,
    IReadOnlyList<CandidateRow> Rows,
    IReadOnlyDictionary<string, SourceProfile> Profiles,
    IReadOnlyDictionary<string, NewsItem> NewsByProfileId,
    IReadOnlyList<ImportedDecision> ImportedDecisions,
    IReadOnlyDictionary<string, string>? SourceHashes = null);
