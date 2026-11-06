using System.ComponentModel;
namespace Aml.Core.Shared;

public enum EvidenceGroup { Name, Gender, Nationality, DateOfBirth, Document, OtherIdentifier, Employment, Relationship, CompanyAssociation, Address, StatedAge, TemporalRole, EntityIdentifier }

public enum Direction { Prerequisite, Support, Conflict, Neutral }

public enum ComparisonStatus { Exact, Partial, Conflict, Uncertain, SubjectMissing, ProfileMissing }

public enum Strength { None, Low, Medium, High, Unique }

public sealed record Evidence(
    [property: Description("證據群組")] EvidenceGroup Group,
    [property: Description("方向：前提、支持、衝突或中性")] Direction Direction,
    [property: Description("比對狀態")] ComparisonStatus Status,
    [property: Description("支持強度（UNIQUE > HIGH > MEDIUM > LOW > NONE）")] Strength Strength = Strength.None,
    [property: Description("細節代碼，見 /api/codes")] string? Detail = null,
    [property: Description("給複核人員的說明文字")][property: Sensitivity(Sensitivity.Personal)] string? Note = null)
{
    [property: Description("是否為支持同一人的證據")]
    public bool IsSupport => Direction == Direction.Support;
}

public static class ReasonCodes
{
    public const string TemporallyPlausible = "TEMPORALLY_PLAUSIBLE";
    public const string ImpossibleAgeAtReportedEvent = "IMPOSSIBLE_AGE_AT_REPORTED_EVENT";
    public const string FullDobConflict = "FULL_DOB_CONFLICT";
    public const string DobConflict = "DOB_CONFLICT";
    public const string DocumentIdentityConflict = "DOCUMENT_IDENTITY_CONFLICT";
    public const string UniqueIdentifierAgreement = "UNIQUE_IDENTIFIER_AGREEMENT";
    public const string StrongSupportCombination = "STRONG_SUPPORT_COMBINATION";
    public const string ConsistentAtAvailablePrecision = "CONSISTENT_AT_AVAILABLE_PRECISION";
    public const string PartialSupportOnly = "PARTIAL_SUPPORT_ONLY";
    public const string NoNonPrerequisiteSupport = "NO_NON_PREREQUISITE_SUPPORT";
    public const string NoBilateralDirectIdentifier = "NO_BILATERAL_DIRECT_IDENTIFIER";
    public const string BirthYearMatchFullDateNotComparable = "BIRTH_YEAR_MATCH_FULL_DATE_NOT_COMPARABLE";
    public const string AgeCompatibleAtReport = "AGE_COMPATIBLE_AT_REPORT";
    public const string AgeDifferenceAtReport = "AGE_DIFFERENCE_AT_REPORT";
    public const string AgeNotComparable = "AGE_NOT_COMPARABLE";
    public const string ImpossibleRoleAtReportedEvent = "IMPOSSIBLE_ROLE_AT_REPORTED_EVENT";
    public const string DeathBeforeCustomerBirth = "DEATH_BEFORE_CUSTOMER_BIRTH";
    public const string NonPersonSubjectReview = "NON_PERSON_SUBJECT_REVIEW";
    public const string GenderConflict = "GENDER_CONFLICT";
    public const string SourceQualityDowngrade = "SOURCE_QUALITY_DOWNGRADE";
    public const string EntityIdentifierAgreement = "ENTITY_IDENTIFIER_AGREEMENT";
    public const string EntityIdentifierConflict = "ENTITY_IDENTIFIER_CONFLICT";
}

/// <summary>Evidence detail codes that are not reason codes.</summary>
public static class EvidenceDetails
{
    public const string NotComparable = "NOT_COMPARABLE";
    public const string NotADocument = "NOT_A_DOCUMENT";
    public const string SameIdentifierNatureUnconfirmed = "SAME_IDENTIFIER_NATURE_UNCONFIRMED";
    public const string DifferentOrganization = "DIFFERENT_ORGANIZATION";
    public const string SameOrganizationSamePeriod = "SAME_ORGANIZATION_SAME_PERIOD";
    public const string SameOrganizationNoCommonPeriod = "SAME_ORGANIZATION_NO_COMMON_PERIOD";
    /// <summary>Followed by the relation: SAME_NAMED_RELATIVE:SPOUSE.</summary>
    public const string SameNamedRelative = "SAME_NAMED_RELATIVE";
    public const string SoftDifference = "SOFT_DIFFERENCE";
    public const string SameAddressNoPeriod = "SAME_ADDRESS_NO_PERIOD";
    public const string NotIdenticalAfterNormalization = "NOT_IDENTICAL_AFTER_NORMALIZATION";
}

public static class Findings
{
    public const string HistoricalAdminDivision = "HISTORICAL_ADMIN_DIVISION";
    public const string PostalCodeOnly = "POSTAL_CODE_ONLY";
    public const string DuplicatedAddressLine = "DUPLICATED_ADDRESS_LINE";
    public const string ProfileMultipleDob = "PROFILE_MULTIPLE_DOB";
    public const string VariantCharacters = "VARIANT_CHARACTERS";
    public const string RomanizationVariant = "ROMANIZATION_VARIANT";
    /// <summary><c>UNPARSEABLE:&lt;field&gt;</c>: a value the reader could not read.</summary>
    public const string Unparseable = "UNPARSEABLE";
    /// <summary><c>AMBIGUOUS:&lt;field&gt;</c>: a country value that maps to no country unambiguously; it is left out of comparison.</summary>
    public const string Ambiguous = "AMBIGUOUS";
}
