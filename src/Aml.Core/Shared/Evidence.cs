namespace Aml.Core.Shared;

public enum EvidenceGroup { Name, Gender, Nationality, DateOfBirth, Document, OtherIdentifier, Employment, Relationship, Address, StatedAge }

public enum Direction { Prerequisite, Support, Conflict, Neutral }

public enum ComparisonStatus { Exact, Partial, Conflict, Uncertain, SubjectMissing, ProfileMissing }

public enum Strength { None, Low, Medium, High, Unique }

public sealed record Evidence(EvidenceGroup Group, Direction Direction, ComparisonStatus Status, Strength Strength = Strength.None, string? Detail = null, string? Note = null)
{
    public bool IsSupport => Direction == Direction.Support;
}

public static class ReasonCodes
{
    public const string ImpossibleAgeAtReportedEvent = "IMPOSSIBLE_AGE_AT_REPORTED_EVENT";
    public const string FullDobConflict = "FULL_DOB_CONFLICT";
    public const string DobConflict = "DOB_CONFLICT";
    public const string DocumentIdentityConflict = "DOCUMENT_IDENTITY_CONFLICT";
    public const string EmploymentConflict = "EMPLOYMENT_CONFLICT";
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
    public const string ArticleTwentyTwoUnresolvedEscalation = "ART22_UNRESOLVED_ESCALATION";
}

public static class Findings
{
    public const string HistoricalAdminDivision = "HISTORICAL_ADMIN_DIVISION";
    public const string PostalCodeOnly = "POSTAL_CODE_ONLY";
    public const string DuplicatedAddressLine = "DUPLICATED_ADDRESS_LINE";
    public const string ProfileMultipleDob = "PROFILE_MULTIPLE_DOB";
    public const string VariantCharacters = "VARIANT_CHARACTERS";
}
