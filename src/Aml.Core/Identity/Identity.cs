using System.Globalization;
using System.Text.RegularExpressions;
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
    public static IReadOnlyDictionary<EvidenceGroup, Evidence> Build(ScreenedSubject s, SourceProfile p, NewsItem? news = null)
    {
        var e = new Dictionary<EvidenceGroup, Evidence>();
        void Add(EvidenceGroup g, Direction d, ComparisonStatus st, Strength str = Strength.None, string? detail = null) =>
            e[g] = new Evidence(g, d, st, str, detail);

        // Hit prerequisites: recorded, never support.
        var (nameStatus, nameDetail) = Normalize.CompareNames(s.Names, p.Names);
        Add(EvidenceGroup.Name, Direction.Prerequisite, nameStatus, detail: nameDetail);
        Add(EvidenceGroup.Gender, Direction.Prerequisite,
            Compare(s.Gender is null, p.Gender is null, () => s.Gender == p.Gender));
        Add(EvidenceGroup.Nationality, Direction.Prerequisite,
            Compare(s.Nationalities.Count == 0, p.Citizenship.Count == 0, () => s.Nationalities.Intersect(p.Citizenship).Any()));

        AddDateOfBirth(s, p, Add);

        // A reference number never becomes DOCUMENT evidence.
        e[EvidenceGroup.Document] = DocumentEvidence(s.Document, p.Documents ?? []);
        if (s.ReferenceNumber is not null)
            Add(EvidenceGroup.OtherIdentifier, Direction.Neutral, ComparisonStatus.ProfileMissing);

        e[EvidenceGroup.Employment] = EmploymentEvidence(s, p);
        if (RelativeEvidence(EvidenceGroup.Relationship, s.Relatives?.Where(r => !r.IsEntity).ToList() ?? [],
                p.Relationships.Where(r => !r.IsEntity).ToList(), Strength.Medium) is { } rel)
            e[EvidenceGroup.Relationship] = rel;
        if (RelativeEvidence(EvidenceGroup.CompanyAssociation, s.Relatives?.Where(r => r.IsEntity).ToList() ?? [],
                p.Relationships.Where(r => r.IsEntity && !IsEmployer(r.Relation)).ToList(), Strength.Low) is { } company)
            e[EvidenceGroup.CompanyAssociation] = company;
        e[EvidenceGroup.Address] = AddressEvidence(s, p);
        if (StatedAgeCheck.Check(s, p, news) is { } age) e[EvidenceGroup.StatedAge] = age;
        return e;
    }

    /// <summary>Comparable only for the same type and issuing country; equal numbers are unique support, different ones a conflict.</summary>
    public static Evidence DocumentEvidence(Document? subject, IReadOnlyList<Document> profile)
    {
        const EvidenceGroup g = EvidenceGroup.Document;
        if (subject is null) return new(g, Direction.Neutral, ComparisonStatus.SubjectMissing);
        if (profile.Count == 0) return new(g, Direction.Neutral, ComparisonStatus.ProfileMissing);
        var comparable = profile.Where(d => d.Type == subject.Type && d.Country is not null && d.Country == subject.Country).ToList();
        if (comparable.Count == 0) return new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, "NOT_COMPARABLE");
        return comparable.Any(d => DocumentNumber(d.Number) == DocumentNumber(subject.Number))
            ? new(g, Direction.Support, ComparisonStatus.Exact, Strength.Unique)
            : new(g, Direction.Conflict, ComparisonStatus.Conflict, Strength.None, ReasonCodes.DocumentIdentityConflict);
    }

    private static string DocumentNumber(string n) => Normalize.NameKey(Normalize.HalfWidth(n));

    /// <summary>
    /// Same organization counts only when both sides state overlapping periods; without periods it stays UNCERTAIN.
    /// A different employer is never a conflict (people hold several roles).
    /// </summary>
    public static Evidence EmploymentEvidence(ScreenedSubject s, SourceProfile p)
    {
        const EvidenceGroup g = EvidenceGroup.Employment;
        var profile = p.Roles.Select(r => new Employment(r.Organization, r.Title, r.From, r.To))
            .Concat(p.Relationships.Where(r => IsEmployer(r.Relation)).Select(r => new Employment(r.Name, null, null, null)))
            .ToList();
        var subject = s.Employments ?? [];
        if (subject.Count == 0)
            // An occupation category alone is not structured evidence.
            return new(g, Direction.Neutral, s.Occupation is null ? ComparisonStatus.SubjectMissing
                : profile.Count > 0 ? ComparisonStatus.Uncertain : ComparisonStatus.ProfileMissing);
        if (profile.Count == 0) return new(g, Direction.Neutral, ComparisonStatus.ProfileMissing);

        var same = subject.SelectMany(a => profile, (a, b) => (a, b))
            .Where(x => Normalize.NameKey(x.a.Organization, true) == Normalize.NameKey(x.b.Organization, true)).ToList();
        if (same.Count == 0) return new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, "DIFFERENT_ORGANIZATION");
        return same.Any(x => Overlap(x.a, x.b))
            ? new(g, Direction.Support, ComparisonStatus.Exact, Strength.Medium, "SAME_ORGANIZATION_SAME_PERIOD")
            : new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, "SAME_ORGANIZATION_NO_COMMON_PERIOD");
    }

    // Both periods need a start; an open end runs to the present. Compared by year, the coarsest common precision.
    private static bool Overlap(Employment a, Employment b) =>
        a.From is { } af && b.From is { } bf && Math.Max(af.Year, bf.Year) <= Math.Min(a.To?.Year ?? int.MaxValue, b.To?.Year ?? int.MaxValue);

    /// <summary>
    /// A relative or related company named on both sides with the same relation and an EXACT name is support.
    /// An undated relationship holds only as of the source capture date, so nothing is projected backwards.
    /// </summary>
    public static Evidence? RelativeEvidence(EvidenceGroup g, IReadOnlyList<Relative> subject, IReadOnlyList<ProfileRelationship> profile, Strength strength)
    {
        if (subject.Count == 0)
            return profile.Count > 0 ? new(g, Direction.Neutral, ComparisonStatus.SubjectMissing) : null;
        if (profile.Count == 0) return new(g, Direction.Neutral, ComparisonStatus.ProfileMissing);
        var same = subject.Any(a => profile.Any(b => Relation(a.Relation) == Relation(b.Relation)
            && Normalize.CompareNames([a.Name], [b.Name]).Status == ComparisonStatus.Exact));
        return same
            ? new(g, Direction.Support, ComparisonStatus.Exact, strength, "SAME_NAMED_" + Relation(subject[0].Relation))
            : new(g, Direction.Neutral, ComparisonStatus.Uncertain);
    }

    private static bool IsEmployer(string relation) => relation.Equals("Employer", StringComparison.OrdinalIgnoreCase);

    // ponytail: relation names seen in DJ exports and bank data; extend as new labels appear.
    private static string Relation(string raw) => raw.Trim().ToUpperInvariant() switch
    {
        "WIFE" or "HUSBAND" or "SPOUSE" or "配偶" or "妻" or "夫" => "SPOUSE",
        "FATHER" or "MOTHER" or "PARENT" or "父" or "母" or "父親" or "母親" => "PARENT",
        "SON" or "DAUGHTER" or "CHILD" or "子" or "女" or "兒子" or "女兒" => "CHILD",
        "BROTHER" or "SISTER" or "SIBLING" or "兄弟" or "姊妹" => "SIBLING",
        var other => other,
    };

    /// <summary>
    /// Location differences are weak: never a conflict, never blocking, and never support without validity periods on
    /// both sides, which neither source supplies. The detail says whether the structured key matched, or the Jaccard.
    /// </summary>
    public static Evidence AddressEvidence(ScreenedSubject s, SourceProfile p)
    {
        const EvidenceGroup g = EvidenceGroup.Address;
        if (s.Addresses.All(a => a.Country is null)) return new(g, Direction.Neutral, ComparisonStatus.SubjectMissing);
        if (p.AddressCountries.Count == 0) return new(g, Direction.Neutral, ComparisonStatus.ProfileMissing);

        var subjectText = s.Addresses.Where(a => a.State == ValueState.Present).Select(a => a.Normalized ?? a.Raw).ToList();
        var profileText = p.AddressLines ?? [];
        if (!s.Addresses.Any(a => a.Country is { } c && p.AddressCountries.Contains(c)) || subjectText.Count == 0 || profileText.Count == 0)
            return new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, "SOFT_DIFFERENCE");

        var keys = subjectText.Select(AddressCompare.TaiwanKey).OfType<string>().ToHashSet();
        if (profileText.Select(AddressCompare.TaiwanKey).Any(k => k is not null && keys.Contains(k)))
            return new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, "SAME_ADDRESS_NO_PERIOD");
        var jaccard = subjectText.SelectMany(a => profileText, AddressCompare.Jaccard).Max();
        return new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None,
            string.Create(CultureInfo.InvariantCulture, $"SOFT_DIFFERENCE（Jaccard {jaccard:0.00}）"));
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
}

/// <summary>
/// Age explicitly stated for the Profile person in attached news (identity-adjudication.md, age stated in a source).
/// Compared at the report date. Within ±2 years is compatible, never support; beyond is a soft conflict, never hard.
/// </summary>
public static class StatedAgeCheck
{
    public const int ToleranceYears = 2;

    public static Evidence? Check(ScreenedSubject s, SourceProfile p, NewsItem? news)
    {
        if (news is null || FindStatedAge(news.Text, p.Names.Where(n => n.Any(c => c is >= '㐀' and <= '鿿'))) is not { } stated)
            return null;
        if (s.DateOfBirth is not { Precision: DatePrecision.Day } dob || news.PublishedOn is not { Precision: DatePrecision.Day } at)
            return new Evidence(EvidenceGroup.StatedAge, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, ReasonCodes.AgeNotComparable,
                $"報導年齡 {stated} 歲；缺少完整出生日期或報導日期，無法換算");

        var age = at.Year - dob.Year - ((at.Month, at.Day).CompareTo((dob.Month, dob.Day)) < 0 ? 1 : 0);
        var note = $"報導年齡 {stated} 歲；依報導日期 {at} 推算 {age} 歲";
        return Math.Abs(stated - age) <= ToleranceYears
            ? new Evidence(EvidenceGroup.StatedAge, Direction.Neutral, ComparisonStatus.Exact, Strength.None, ReasonCodes.AgeCompatibleAtReport, note)
            : new Evidence(EvidenceGroup.StatedAge, Direction.Conflict, ComparisonStatus.Conflict, Strength.None, ReasonCodes.AgeDifferenceAtReport, note);
    }

    /// <summary>"陳志明（45歲）", "陳志明今年45歲", "45歲的陳志明", within one sentence and adjacent to the name.</summary>
    public static int? FindStatedAge(string text, IEnumerable<string> names)
    {
        foreach (var sentence in text.Split('。', '！', '？', '\n'))
        foreach (var name in names)
        {
            var n = Regex.Escape(name);
            var m = Regex.Match(sentence, $@"{n}[（(，,]?(?:今年|現年|時年)?(\d{{1,3}})歲|(\d{{1,3}})歲的?{n}");
            if (m.Success)
                return int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        return null;
    }
}

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
    // (Company Act art. 192, 216): adulthood was 20 before 2023-01-01 and 18 since.
    private static readonly string[] AdultOnlyTitles = ["董事", "監察人", "Director", "Supervisor"];

    public static string? Check(ScreenedSubject s, SourceProfile p)
    {
        if (s.DateOfBirth is not { } dob) return null;
        if (p.DateOfDeath is { } death && Before(death, dob)) return ReasonCodes.DeathBeforeCustomerBirth;
        if (dob.Precision != DatePrecision.Day) return null;
        var dated = p.Roles.Where(r => r.From is not null).ToList();
        if (dated.Any(r => MaxAgeAt(dob, r.From!.Value) < MinimumRoleAge)) return ReasonCodes.ImpossibleAgeAtReportedEvent;
        if (dated.Any(r => AdultOnlyTitles.Any(t => r.Title.Contains(t, StringComparison.OrdinalIgnoreCase))
                && MaxAgeAt(dob, r.From!.Value) < (r.From!.Value.Year < 2023 ? 20 : 18)))
            return ReasonCodes.ImpossibleRoleAtReportedEvent;
        return null;
    }

    /// <summary>True only when every date <paramref name="a"/> can stand for precedes every date <paramref name="b"/> can.</summary>
    public static bool Before(PartialDate a, PartialDate b) =>
        a.Year != b.Year ? a.Year < b.Year
        : a.Month is null || b.Month is null ? false
        : a.Month != b.Month ? a.Month < b.Month
        : a.Day is not null && b.Day is not null && a.Day < b.Day;

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
    public static IdentityResult Decide(ScreenedSubject s, SourceProfile p, NewsItem? news = null)
    {
        var e = EvidenceBuilder.Build(s, p, news);
        // Phase 1: the person rules do not apply to organizations, vessels and other records.
        if (!p.RecordType.Equals("Person", StringComparison.OrdinalIgnoreCase))
            return new IdentityResult(e, null, null, IdentityDecision.Review, [ReasonCodes.NonPersonSubjectReview]);
        if (HardConflictGate.Check(s, p) is { } hard)
            return new IdentityResult(e, hard, null,
                HardConflictGate.Validated.Contains(hard) ? IdentityDecision.NoMatch : IdentityDecision.Review, [hard]);

        var (row, decision, code) = Stage3(e);
        var reasons = new List<string> { code };
        if (NoBilateralDirectIdentifier(s, p)) reasons.Add(ReasonCodes.NoBilateralDirectIdentifier);
        return new IdentityResult(e, null, row, decision, reasons);
    }

    private static (int, IdentityDecision, string) Stage3(IReadOnlyDictionary<EvidenceGroup, Evidence> e)
    {
        ComparisonStatus St(EvidenceGroup g) => e.TryGetValue(g, out var v) ? v.Status : ComparisonStatus.ProfileMissing;
        var supports = e.Values.Where(v => v.IsSupport).ToList();

        var conflict = new[] { EvidenceGroup.DateOfBirth, EvidenceGroup.Document, EvidenceGroup.Employment, EvidenceGroup.StatedAge }
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
        !(s.DateOfBirth?.Precision == DatePrecision.Day && p.DatesOfBirth.Any(d => d.Precision == DatePrecision.Day))
        && !(s.Document is { } d && (p.Documents ?? []).Any(x => x.Type == d.Type && x.Country is not null && x.Country == d.Country));
}
