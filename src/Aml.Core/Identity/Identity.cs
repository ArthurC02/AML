using Aml.Core.Normalization;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Identity;

public enum IdentityDecision { NoMatch, Review, ProbableMatch }

public sealed record IdentityResult(
    IReadOnlyDictionary<EvidenceGroup, Evidence> Evidence,
    string? HardConflict,
    int? Stage3Row,
    IdentityDecision Decision,
    IReadOnlyList<string> ReasonCodes);

/// <summary>Builds per-group evidence for one screened subject versus one Profile.</summary>
public static class EvidenceBuilder
{
    public static IReadOnlyDictionary<EvidenceGroup, Evidence> Build(ScreenedSubject s, SourceProfile p)
    {
        var e = new Dictionary<EvidenceGroup, Evidence>();
        void Add(EvidenceGroup g, Direction d, ComparisonStatus st, Strength str = Strength.None, string? detail = null) =>
            e[g] = new Evidence(g, d, st, str, detail);

        // Hit prerequisites: recorded, never support.
        var profileKeys = p.Names.SelectMany(Normalize.NameKeys).ToHashSet();
        Add(EvidenceGroup.Name, Direction.Prerequisite,
            s.Names.Count == 0 ? ComparisonStatus.SubjectMissing
            : s.Names.Any(n => Normalize.NameKeys(n).Any(profileKeys.Contains)) ? ComparisonStatus.Exact : ComparisonStatus.Partial);
        Add(EvidenceGroup.Gender, Direction.Prerequisite,
            Compare(s.Gender is null, p.Gender is null, () => s.Gender == p.Gender));
        Add(EvidenceGroup.Nationality, Direction.Prerequisite,
            Compare(s.Nationalities.Count == 0, p.Citizenship.Count == 0, () => s.Nationalities.Intersect(p.Citizenship).Any()));

        AddDateOfBirth(s, p, Add);

        // No document on the Profile side in Phase 1 samples; a reference number never becomes DOCUMENT evidence.
        Add(EvidenceGroup.Document, Direction.Neutral, Missing(s.Document is null, profileMissing: true));
        if (s.ReferenceNumber is not null)
            Add(EvidenceGroup.OtherIdentifier, Direction.Neutral, ComparisonStatus.ProfileMissing);

        // Free text (news job titles, occupation categories) is not structured evidence in Phase 1.
        var profileEmployment = p.Roles.Count > 0 || p.Relationships.Any(r => r.Relation.Equals("Employer", StringComparison.OrdinalIgnoreCase));
        Add(EvidenceGroup.Employment, Direction.Neutral,
            s.Occupation is null ? ComparisonStatus.SubjectMissing : profileEmployment ? ComparisonStatus.Uncertain : ComparisonStatus.ProfileMissing);
        var profileFamily = p.Relationships.Any(r => !r.Relation.Equals("Employer", StringComparison.OrdinalIgnoreCase));
        if (profileFamily)
            Add(EvidenceGroup.Relationship, Direction.Neutral, ComparisonStatus.SubjectMissing); // subject family data is not supplied
        // Location differences are weak: never a conflict, never blocking.
        Add(EvidenceGroup.Address, Direction.Neutral,
            s.Addresses.All(a => a.Country is null) ? ComparisonStatus.SubjectMissing
            : p.AddressCountries.Count == 0 ? ComparisonStatus.ProfileMissing : ComparisonStatus.Uncertain,
            detail: p.AddressCountries.Count > 0 ? "SOFT_DIFFERENCE" : null);
        return e;
    }

    private static void AddDateOfBirth(ScreenedSubject s, SourceProfile p, Action<EvidenceGroup, Direction, ComparisonStatus, Strength, string?> add)
    {
        if (s.DateOfBirth is not { } dob) { add(EvidenceGroup.DateOfBirth, Direction.Neutral, ComparisonStatus.SubjectMissing, Strength.None, null); return; }
        if (p.DatesOfBirth.Count == 0) { add(EvidenceGroup.DateOfBirth, Direction.Neutral, ComparisonStatus.ProfileMissing, Strength.None, null); return; }

        // A Profile with several DOBs is source ambiguity: one agreeing value is support, the others are not conflicts.
        var agreeing = p.DatesOfBirth.Where(dob.AgreesWith).OrderByDescending(d => d.Precision).ToList();
        if (agreeing.Count == 0)
        {
            var full = dob.Precision == DatePrecision.Day && p.DatesOfBirth.Any(d => d.Precision == DatePrecision.Day);
            add(EvidenceGroup.DateOfBirth, Direction.Conflict, ComparisonStatus.Conflict, Strength.None,
                full ? ReasonCodes.FullDobConflict : ReasonCodes.DobConflict);
            return;
        }
        var precision = (DatePrecision)Math.Min((int)dob.Precision, (int)agreeing[0].Precision);
        add(EvidenceGroup.DateOfBirth, Direction.Support,
            precision == DatePrecision.Day ? ComparisonStatus.Exact : ComparisonStatus.Partial,
            precision switch { DatePrecision.Day => Strength.High, DatePrecision.Month => Strength.Medium, _ => Strength.Low },
            precision == DatePrecision.Year ? ReasonCodes.BirthYearMatchFullDateNotComparable : null);
    }

    private static ComparisonStatus Compare(bool subjectMissing, bool profileMissing, Func<bool> agrees) =>
        subjectMissing ? ComparisonStatus.SubjectMissing
        : profileMissing ? ComparisonStatus.ProfileMissing
        : agrees() ? ComparisonStatus.Exact : ComparisonStatus.Conflict;

    private static ComparisonStatus Missing(bool subjectMissing, bool profileMissing) =>
        subjectMissing ? ComparisonStatus.SubjectMissing : profileMissing ? ComparisonStatus.ProfileMissing : ComparisonStatus.Uncertain;
}

/// <summary>Stage 1 hard conflict gate.</summary>
public static class HardConflictGate
{
    // ponytail: minimum plausible age for an attributed adult role; pending business calibration (open question).
    public const int MinimumRoleAge = 14;

    public static string? Check(ScreenedSubject s, SourceProfile p)
    {
        if (s.DateOfBirth is not { Precision: DatePrecision.Day } dob) return null;
        foreach (var role in p.Roles)
            if (role.From is { } at && MaxAgeAt(dob, at) < MinimumRoleAge)
                return ReasonCodes.ImpossibleAgeAtReportedEvent;
        return null;
    }

    /// <summary>Oldest the person can have been at a possibly imprecise time point.</summary>
    public static int MaxAgeAt(PartialDate dob, PartialDate at)
    {
        var age = at.Year - dob.Year;
        if (at.Month is null) return age;
        if (at.Month < dob.Month) return age - 1;
        if (at.Month > dob.Month || at.Day is null) return age;
        return at.Day < dob.Day ? age - 1 : age;
    }
}

/// <summary>Stage 3 decision table (identity-adjudication.md). The first matching row wins.</summary>
public static class IdentityDecisionPolicy
{
    public static IdentityResult Decide(ScreenedSubject s, SourceProfile p)
    {
        var e = EvidenceBuilder.Build(s, p);
        if (HardConflictGate.Check(s, p) is { } hard)
            return new IdentityResult(e, hard, null, IdentityDecision.NoMatch, [hard]);

        var (row, decision, code) = Stage3(e);
        var reasons = new List<string> { code };
        if (NoBilateralDirectIdentifier(s, p)) reasons.Add(ReasonCodes.NoBilateralDirectIdentifier);
        return new IdentityResult(e, null, row, decision, reasons);
    }

    private static (int, IdentityDecision, string) Stage3(IReadOnlyDictionary<EvidenceGroup, Evidence> e)
    {
        ComparisonStatus St(EvidenceGroup g) => e.TryGetValue(g, out var v) ? v.Status : ComparisonStatus.ProfileMissing;
        var supports = e.Values.Where(v => v.IsSupport).ToList();

        var conflict = new[] { EvidenceGroup.DateOfBirth, EvidenceGroup.Document, EvidenceGroup.Employment }
            .Select(g => e.GetValueOrDefault(g)).FirstOrDefault(v => v?.Status == ComparisonStatus.Conflict);
        if (conflict is not null)
            return (1, IdentityDecision.Review, conflict.Detail ?? conflict.Group switch
            {
                EvidenceGroup.Document => ReasonCodes.DocumentIdentityConflict,
                EvidenceGroup.Employment => ReasonCodes.EmploymentConflict,
                _ => ReasonCodes.DobConflict,
            });

        if (supports.Any(v => v.Strength == Strength.Unique))
            return (2, IdentityDecision.ProbableMatch, ReasonCodes.UniqueIdentifierAgreement);

        if (supports.Any(v => v.Strength == Strength.High) && supports.Count >= 2)
            return (3, IdentityDecision.ProbableMatch, ReasonCodes.StrongSupportCombination);

        var prerequisitesAgree = St(EvidenceGroup.Name) == ComparisonStatus.Exact
            && St(EvidenceGroup.Gender) == ComparisonStatus.Exact
            && St(EvidenceGroup.Nationality) == ComparisonStatus.Exact;
        var dobAgrees = St(EvidenceGroup.DateOfBirth) is ComparisonStatus.Exact or ComparisonStatus.Partial;
        var docOrEmploymentUnsettled = new[] { EvidenceGroup.Document, EvidenceGroup.Employment }
            .Any(g => St(g) is ComparisonStatus.Conflict or ComparisonStatus.Uncertain);
        if (prerequisitesAgree && dobAgrees && !docOrEmploymentUnsettled)
            return (4, IdentityDecision.ProbableMatch, ReasonCodes.ConsistentAtAvailablePrecision);

        return supports.Count > 0
            ? (5, IdentityDecision.Review, ReasonCodes.PartialSupportOnly)
            : (6, IdentityDecision.Review, ReasonCodes.NoNonPrerequisiteSupport);
    }

    // Direct identifier: full DOB or a document key present on both sides.
    private static bool NoBilateralDirectIdentifier(ScreenedSubject s, SourceProfile p) =>
        !(s.DateOfBirth?.Precision == DatePrecision.Day && p.DatesOfBirth.Any(d => d.Precision == DatePrecision.Day));
}
