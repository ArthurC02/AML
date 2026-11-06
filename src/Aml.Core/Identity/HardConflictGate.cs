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
    public static readonly IReadOnlySet<string> Validated = new HashSet<string> { ReasonCodes.ImpossibleAgeAtReportedEvent };

    // ponytail: minimum plausible age for an attributed adult role; pending business calibration (open question).
    public const int MinimumRoleAge = 14;

    // ponytail: role constraints pending business approval. Directors and supervisors need full legal capacity
    // (Company Act art. 192, 216): adulthood was 20 before 2023-01-01 and 18 since. The whole title must be listed
    // ("Director of Marketing" is not a board director); add titles as exports show them.
    private static readonly HashSet<string> AdultOnlyTitles = new(StringComparer.OrdinalIgnoreCase)
        { "董事", "董事長", "副董事長", "常務董事", "獨立董事", "監察人", "Director", "Supervisor" };

    /// <summary>
    /// Source-quality concerns that downgrade a validated hard conflict to REVIEW: a dated role without its organization,
    /// a role dated to the year only, a role the Profile's own DOB makes just as impossible (the role date is suspect),
    /// Profile values the reader could not parse, or HIGH or UNIQUE support for the same pair.
    /// ponytail: criteria pending business approval; they only ever make the result more cautious.
    /// </summary>
    public static bool SourceQualityConcern(SourceProfile p, IReadOnlyDictionary<EvidenceGroup, Evidence>? evidence = null) =>
        p.Roles.Any(r => r.From is { } from && (string.IsNullOrWhiteSpace(r.Organization) || from.Precision == DatePrecision.Year
            || p.DatesOfBirth.Any(d => MaxAgeAt(d, from) < MinimumRoleAge)))
        || p.Findings.Any(f => f.StartsWith(Findings.Unparseable, StringComparison.Ordinal))
        || (evidence?.Values.Any(e => e.IsSupport && e.Strength >= Strength.High) ?? false);

    public static string? Check(ScreenedSubject s, SourceProfile p)
    {
        if (s.DateOfBirth is not { } dob) return null;
        if (p.DateOfDeath is { } death && Before(death, dob)) return ReasonCodes.DeathBeforeCustomerBirth;
        if (dob.Precision != DatePrecision.Day) return null;
        var dated = p.Roles.Where(r => r.From is not null).ToList();
        if (dated.Any(r => MaxAgeAt(dob, r.From!.Value) < MinimumRoleAge)) return ReasonCodes.ImpossibleAgeAtReportedEvent;
        if (dated.Any(r => AdultOnlyTitles.Contains(r.Title.Trim())
                && MaxAgeAt(dob, r.From!.Value) < (r.From!.Value.Year < 2023 ? 20 : 18)))
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
        // The birthday has surely not come only when a known month, or a known day of the same month, is earlier.
        var age = at.Year - dob.Year;
        return at.Month < dob.Month || (at.Month == dob.Month && at.Day < dob.Day) ? age - 1 : age;
    }

    /// <summary>Youngest the person can have been: the birthday has surely come only when the known parts say so.</summary>
    public static int MinAgeAt(PartialDate dob, PartialDate at)
    {
        var age = at.Year - dob.Year;
        return at.Month > dob.Month || (at.Month == dob.Month && at.Day >= dob.Day) ? age : age - 1;
    }
}
