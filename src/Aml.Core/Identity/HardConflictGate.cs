using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Identity;

/// <summary>
/// Stage 1 hard conflict gate. Only a rule validated by a Golden Case closes a pair as NO_MATCH; a pending rule
/// still fires and records its code, but the pair stays REVIEW. FULL_DOB_CONFLICT and DOCUMENT_IDENTITY_CONFLICT
/// are pending and reach REVIEW through stage 3 row 1.
/// </summary>
public static class HardConflictGate
{
    public static readonly IReadOnlySet<string> ValidatedReasonCodes = new HashSet<string> { ReasonCodes.ImpossibleAgeAtReportedEvent };

    // ponytail: minimum plausible age for an attributed adult role; pending business calibration (open question).
    public const int MinimumRoleAge = 14;

    // ponytail: role constraints pending business approval. Directors and supervisors need full legal capacity
    // (Company Act art. 192, 216): adulthood was 20 before 2023-01-01 and 18 since. The whole title must be listed
    // ("Director of Marketing" is not a board director); add titles as exports show them.
    private static readonly HashSet<string> AdultOnlyTitles = new(StringComparer.OrdinalIgnoreCase)
        { "董事", "董事長", "副董事長", "常務董事", "獨立董事", "監察人", "Director", "Supervisor" };

    private const int AdultAgeChangeYear = 2023;
    private const int AdultAgeBeforeChange = 20;
    private const int AdultAgeSinceChange = 18;

    private static int AdultAgeIn(int year) => year < AdultAgeChangeYear ? AdultAgeBeforeChange : AdultAgeSinceChange;

    /// <summary>
    /// Source-quality concerns that downgrade a validated hard conflict to REVIEW: a dated role without its organization,
    /// a role dated to the year only, a role the Profile's own DOB makes just as impossible (the role date is suspect),
    /// Profile values the reader could not parse, or HIGH or UNIQUE support for the same pair.
    /// ponytail: criteria pending business approval; they only ever make the result more cautious.
    /// </summary>
    public static bool SourceQualityConcern(SourceProfile p, IReadOnlyDictionary<EvidenceGroup, Evidence>? evidence = null) =>
        p.Roles.Any(r => r.From is { } from && IsSuspectRoleDate(p, r, from))
        || p.Findings.Any(f => f.StartsWith(Findings.Unparseable, StringComparison.Ordinal))
        || (evidence?.Values.Any(e => e.IsSupport && e.Strength >= Strength.High) ?? false);

    private static bool IsSuspectRoleDate(SourceProfile p, ProfileRole role, PartialDate from) =>
        string.IsNullOrWhiteSpace(role.Organization)
        || from.Precision == DatePrecision.Year
        || p.DatesOfBirth.Any(d => MaxAgeAt(d, from) < MinimumRoleAge);

    public static string? Check(ScreenedSubject s, SourceProfile p)
    {
        if (s.DateOfBirth is not { } dob) return null;
        if (p.DateOfDeath is { } death && Before(death, dob)) return ReasonCodes.DeathBeforeCustomerBirth;
        if (dob.Precision != DatePrecision.Day) return null;
        var roleStarts = p.Roles.Where(r => r.From is not null).Select(r => (Role: r, From: r.From!.Value)).ToList();
        if (roleStarts.Any(x => MaxAgeAt(dob, x.From) < MinimumRoleAge)) return ReasonCodes.ImpossibleAgeAtReportedEvent;
        if (roleStarts.Any(x => AdultOnlyTitles.Contains(x.Role.Title.Trim()) && MaxAgeAt(dob, x.From) < AdultAgeIn(x.From.Year)))
            return ReasonCodes.ImpossibleRoleAtReportedEvent;
        return null;
    }

    /// <summary>
    /// True only when every date <paramref name="a"/> can stand for precedes every date <paramref name="b"/> can. A comparison
    /// with a missing month or day is false (lifted operators), so an unknown part never orders two dates.
    /// </summary>
    public static bool Before(PartialDate a, PartialDate b) =>
        a.Year != b.Year ? a.Year < b.Year
        : a.Month != b.Month ? a.Month < b.Month
        : a.Day < b.Day;

    /// <summary>Oldest the person can have been at a possibly imprecise time point.</summary>
    public static int MaxAgeAt(PartialDate dob, PartialDate at)
    {
        var age = at.Year - dob.Year;
        return BirthdaySurelyStillToCome(dob, at) ? age - 1 : age;
    }

    /// <summary>Youngest the person can have been at a possibly imprecise time point.</summary>
    public static int MinAgeAt(PartialDate dob, PartialDate at)
    {
        var age = at.Year - dob.Year;
        return BirthdaySurelyCome(dob, at) ? age : age - 1;
    }

    private static bool BirthdaySurelyStillToCome(PartialDate dob, PartialDate at) =>
        at.Month < dob.Month || (at.Month == dob.Month && at.Day < dob.Day);

    private static bool BirthdaySurelyCome(PartialDate dob, PartialDate at) =>
        at.Month > dob.Month || (at.Month == dob.Month && at.Day >= dob.Day);
}
