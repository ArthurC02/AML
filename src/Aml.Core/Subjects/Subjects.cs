using Aml.Core.Shared;

namespace Aml.Core.Subjects;

public enum SubjectRole { Customer, RelatedParty }

public sealed record Address(string Role, string Raw, string? Normalized, string? Country, ValueState State = ValueState.Present, string? Detail = null)
{
    /// <summary>The block's country field contradicts the address text (FIELD_CONFLICT).</summary>
    public bool Conflict { get; init; }
}

/// <summary>An identity document. A Taiwan document type implies issuing country TW.</summary>
public sealed record Document(string? Type, string Number, string? IssuingCountry = null)
{
    public string? Country => IssuingCountry ?? (Type?.StartsWith("TW_", StringComparison.Ordinal) == true ? "TW" : null);
}

/// <summary>An employment with its organization and, when the source states it, its period.</summary>
public sealed record Employment(string Organization, string? Title, PartialDate? From, PartialDate? To);

/// <summary>A person or company related to the screened subject, as named in bank data.</summary>
public sealed record Relative(string Name, string Relation, bool IsEntity = false);

/// <summary>The customer or related party actually compared with a Profile. Only this person's data is used.</summary>
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
    /// <summary>A legal person or group (客戶類型 法人), compared on entity identifiers, never on DOB or nationality rules.</summary>
    public bool IsEntity => PartyType is { } t && !t.Contains('個', StringComparison.Ordinal) && !t.Equals("Person", StringComparison.OrdinalIgnoreCase);
}

public sealed record SourceCategory(string Category, string? Subcategory);

public sealed record ProfileRole(string Title, string Organization, PartialDate? From, PartialDate? To = null);

public sealed record ProfileRelationship(string ProfileId, string Name, string Relation, bool IsEntity = false);

/// <summary>A labelled identifier that is not an identity document (reference number, registry key).</summary>
public sealed record OtherIdentifier(string Label, string Number);

public sealed record OfficialListEntry(string Authority, string List, string Status, string? Offence);

public sealed record SourceProfile(
    string ProfileKey,
    Gender? Gender,
    IReadOnlyList<string> Names,
    IReadOnlyList<PartialDate> DatesOfBirth,
    IReadOnlyList<string> Citizenship,
    IReadOnlyList<string> AddressCountries,
    IReadOnlyList<SourceCategory> Categories,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<ProfileRole> Roles,
    IReadOnlyList<ProfileRelationship> Relationships,
    OfficialListEntry? OfficialList,
    IReadOnlyList<string> Findings,
    IReadOnlyList<SourceReference> Sources,
    IReadOnlyList<Document>? Documents = null,
    IReadOnlyList<string>? AddressLines = null,
    PartialDate? DateOfDeath = null,
    string RecordType = "Person",
    IReadOnlyList<OtherIdentifier>? OtherIdentifiers = null,
    IReadOnlyList<string>? AllegationCountries = null);

/// <summary>A source the Profile cites. Dates are ISO strings as far as the source states them.</summary>
public sealed record SourceReference(string Id, string Name, string? Url, string? PublishedOn, string? RetrievedOn);

/// <summary>A news item attached to a Profile in the input.</summary>
public sealed record NewsItem(string ProfileId, string? DocumentId, string Title, string? Publisher, PartialDate? PublishedOn, string Text);

/// <summary>A source flag exactly as read (extraction-and-normalization.md#source-flags). NO is a value, not unknown.</summary>
public enum FlagValue { Missing, Yes, No, Unparseable }

/// <summary>The three list flags, plus Is RCA? kept as read so a NO can be told from a blank; Is RCA? is not one of the three.</summary>
public sealed record ListFlags(FlagValue Pep, FlagValue Blacklist, FlagValue AdverseNews, FlagValue Rca = FlagValue.Missing)
{
    public static readonly ListFlags None = new(FlagValue.Missing, FlagValue.Missing, FlagValue.Missing);

    public bool AllMissing => Pep == FlagValue.Missing && Blacklist == FlagValue.Missing && AdverseNews == FlagValue.Missing;

    /// <summary>Several rows of one Profile: YES wins, then UNPARSEABLE, then NO.</summary>
    public static ListFlags Merge(IEnumerable<ListFlags> rows)
    {
        var list = rows.ToList();
        static FlagValue One(IEnumerable<FlagValue> v) =>
            v.Contains(FlagValue.Yes) ? FlagValue.Yes : v.Contains(FlagValue.Unparseable) ? FlagValue.Unparseable
            : v.Contains(FlagValue.No) ? FlagValue.No : FlagValue.Missing;
        return new(One(list.Select(f => f.Pep)), One(list.Select(f => f.Blacklist)), One(list.Select(f => f.AdverseNews)), One(list.Select(f => f.Rca)));
    }
}

/// <summary>One row of the candidate table. Legacy columns are ImportedDecision only, never evidence.</summary>
public sealed record CandidateRow(string MatchedName, string ProfileId, string? LegacyReason, bool IsRca, ListFlags? Flags = null);

public sealed record ImportedDecision(string Field, string Value, bool IndependentlyVerified = false);

public sealed record CaseInput(
    ScreenedSubject Subject,
    IReadOnlyList<CandidateRow> Rows,
    IReadOnlyDictionary<string, SourceProfile> Profiles,
    IReadOnlyDictionary<string, NewsItem> NewsByProfileId,
    IReadOnlyList<ImportedDecision> LegacyDecisions,
    IReadOnlyDictionary<string, string>? SourceHashes = null);
