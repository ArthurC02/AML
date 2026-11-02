using Aml.Core.Shared;

namespace Aml.Core.Subjects;

public enum SubjectRole { Customer, RelatedParty }

public sealed record Address(string Role, string Raw, string? Normalized, string? Country, ValueState State = ValueState.Present, string? Detail = null);

public sealed record Document(string? Type, string Number);

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
    IReadOnlyList<string> Findings);

public sealed record SourceCategory(string Category, string? Subcategory);

public sealed record ProfileRole(string Title, string Organization, PartialDate? From);

public sealed record ProfileRelationship(string ProfileId, string Name, string Relation);

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
    IReadOnlyList<SourceReference> Sources);

/// <summary>A source the Profile cites. Dates are ISO strings as far as the source states them.</summary>
public sealed record SourceReference(string Id, string Name, string? Url, string? PublishedOn, string? RetrievedOn);

/// <summary>A news item attached to a Profile in the input.</summary>
public sealed record NewsItem(string ProfileId, string? DocumentId, string Title, string? Publisher, PartialDate? PublishedOn, string Text);

/// <summary>One row of the candidate table. Legacy columns are ImportedDecision only, never evidence.</summary>
public sealed record CandidateRow(string MatchedName, string ProfileId, string? LegacyReason, bool IsRca);

public sealed record ImportedDecision(string Field, string Value, bool IndependentlyVerified = false);

public sealed record CaseInput(
    ScreenedSubject Subject,
    IReadOnlyList<CandidateRow> Rows,
    IReadOnlyDictionary<string, SourceProfile> Profiles,
    IReadOnlyDictionary<string, NewsItem> NewsByProfileId,
    IReadOnlyList<ImportedDecision> LegacyDecisions);
