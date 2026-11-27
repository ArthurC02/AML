using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Identity;

/// <summary>
/// Stage 1 hard conflict gate. Only a validated rule closes a pair as NO_MATCH; a pending rule still fires and records its
/// code, but the pair stays REVIEW. A Profile DOB more than <see cref="EvidenceBuilder.DobToleranceYears"/> years from the
/// customer's (FULL_DOB_CONFLICT, DOB_CONFLICT), and so is every birth date or age attached news or a received page states
/// for the person (STATED_DOB_CONFLICT), is validated by the user's decisions of 2026-11-27; DOCUMENT_IDENTITY_CONFLICT is
/// pending and reaches REVIEW through stage 3 row 1.
/// </summary>
public static class HardConflictGate
{
    public static readonly IReadOnlySet<string> ValidatedReasonCodes = new HashSet<string>
        { ReasonCodes.ImpossibleAgeAtReportedEvent, ReasonCodes.FullDobConflict, ReasonCodes.DobConflict, ReasonCodes.StatedDobConflict };

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
    /// Source-quality concerns that downgrade a validated hard conflict to REVIEW: Profile values the reader could not parse,
    /// or HIGH or UNIQUE support for the same pair; for a role rule also a dated role without its organization, a role dated to
    /// the year only, or a role the Profile's own DOB makes just as impossible (the role date is suspect); for a DOB conflict
    /// (<paramref name="rule"/>) scattered Profile DOBs instead, since the role dates say nothing about the birth date.
    /// ponytail: criteria pending business approval; they only ever make the result more cautious.
    /// </summary>
    public static bool SourceQualityConcern(SourceProfile p, IReadOnlyDictionary<EvidenceGroup, Evidence>? evidence = null, string? rule = null) =>
        (rule is ReasonCodes.FullDobConflict or ReasonCodes.DobConflict
            ? EvidenceBuilder.AreScattered(EvidenceBuilder.ProfileDatesOfBirth(p))
            : p.Roles.Any(r => r.From is { } from && IsSuspectRoleDate(p, r, from)))
        || p.Findings.Any(f => f.StartsWith(Findings.Unparseable, StringComparison.Ordinal))
        || (evidence?.Values.Any(e => e.IsSupport && e.Strength >= Strength.High) ?? false);

    private static bool IsSuspectRoleDate(SourceProfile p, ProfileRole role, PartialDate from) =>
        string.IsNullOrWhiteSpace(role.Organization)
        || from.Precision == DatePrecision.Year
        || p.DatesOfBirth.Any(d => MaxAgeAt(d, from) < MinimumRoleAge);

    /// <summary>
    /// The first rule that fires: death before birth, impossible age, the DOB conflict, impossible role, and the values the
    /// text <paramref name="news"/> states next to the name, as compared in <paramref name="evidence"/> (<see cref="StatedConflict"/>).
    /// </summary>
    public static string? Check(ScreenedSubject s, SourceProfile p, IReadOnlyDictionary<EvidenceGroup, Evidence>? evidence = null, NewsItem? news = null)
    {
        if (s.DateOfBirth is not { } dob) return null;
        if (p.DateOfDeath is { } death && Before(death, dob)) return ReasonCodes.DeathBeforeCustomerBirth;
        var role = RoleConflict(dob, p);
        if (role == ReasonCodes.ImpossibleAgeAtReportedEvent) return role;
        return EvidenceBuilder.DateOfBirthEvidence(s, p) is { Direction: Direction.Conflict, Detail: { } dobConflict } ? dobConflict
            : role ?? StatedConflict(s, dob, p, evidence, news);
    }

    /// <summary>
    /// The birth dates and the age at the report date that attached news or a received page states next to the name (user,
    /// 2026-11-27): when at least one was compared and every one compared is more than
    /// <see cref="EvidenceBuilder.DobToleranceYears"/> years from the customer's DOB (an age by the births it allows,
    /// <see cref="StatedAgeCheck"/>), STATED_DOB_CONFLICT. A value within two years, or one not comparable (no report date, an
    /// age at the event, no full customer DOB for an age), never closes. Nor does it when the Profile has a DOB of its own
    /// (reaching here, that DOB agrees with the customer's, and the news may be about someone else), or when HIGH or UNIQUE
    /// support agrees (a document outranks the text). The far value must also be read next to a full name form
    /// (<see cref="StatedAgeCheck.IsFullForm"/>): a short alias may stand inside another person's name; what such an alias
    /// reads still blocks, but never concludes.
    /// </summary>
    private static string? StatedConflict(ScreenedSubject s, PartialDate dob, SourceProfile p, IReadOnlyDictionary<EvidenceGroup, Evidence>? evidence, NewsItem? news)
    {
        // An agreeing document (or any HIGH support) outranks what a text states next to a name, as in stage 3 row 2.
        if (evidence is null || p.DatesOfBirth.Count > 0 || evidence.Values.Any(v => v.IsSupport && v.Strength >= Strength.High)) return null;
        var compared = new[] { EvidenceGroup.StatedAge, EvidenceGroup.StatedBirthDate }.Select(evidence.GetValueOrDefault)
            .OfType<Evidence>().Where(v => v.Status is not (ComparisonStatus.Uncertain or ComparisonStatus.SubjectMissing)).ToList();
        bool FarDates(List<PartialDate> dates) => dates.Count > 0 && dates.TrueForAll(d => dob.IsMoreThanYearsApart(d, EvidenceBuilder.DobToleranceYears));
        // A news birth date is a CONFLICT whenever none agrees; it closes only when every one is more than two years off.
        bool Far(Evidence v) => v.Status == ComparisonStatus.Conflict && (v.Group != EvidenceGroup.StatedBirthDate || FarDates(NewsStatedCheck.StatedBirthDates(p, news)));
        if (compared.Count == 0 || !compared.All(Far)) return null;

        var age = StatedAgeCheck.Check(s, p, news, fullFormsOnly: true);
        if (age?.Status == ComparisonStatus.Exact) return null; // compatible, read next to a full form
        return age?.Status == ComparisonStatus.Conflict || FarDates(NewsStatedCheck.StatedBirthDates(p, news, fullFormsOnly: true))
            ? ReasonCodes.StatedDobConflict : null;
    }

    /// <summary>Impossible age, else impossible role, at a dated role start; both need the customer's DOB to the day.</summary>
    public static string? RoleConflict(PartialDate dob, SourceProfile p)
    {
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
