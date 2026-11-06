using System.Globalization;
using Aml.Core.Normalization;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Identity;

public static class EvidenceBuilder
{
    public static IReadOnlyDictionary<EvidenceGroup, Evidence> Build(ScreenedSubject s, SourceProfile p, NewsItem? news = null)
    {
        var e = new Dictionary<EvidenceGroup, Evidence>();
        void Add(Evidence? evidence)
        {
            if (evidence is not null) e[evidence.Group] = evidence;
        }

        foreach (var prerequisite in HitPrerequisites(s, p)) Add(prerequisite);
        Add(DateOfBirthEvidence(s, p));
        Add(DocumentEvidence(s.Document, p.Documents ?? []));
        Add(OtherIdentifierEvidence(s.ReferenceNumber, p.OtherIdentifiers ?? []));
        Add(TemporalEvidence(s, p, news));
        Add(EmploymentEvidence(s, p));
        Add(RelativeEvidence(EvidenceGroup.Relationship, SubjectRelatives(s, isEntity: false),
            p.Relationships.Where(r => !r.IsEntity).ToList(), Strength.Medium));
        Add(RelativeEvidence(EvidenceGroup.CompanyAssociation, SubjectRelatives(s, isEntity: true),
            p.Relationships.Where(r => r.IsEntity && !IsEmployer(r.Relation)).ToList(), Strength.Low));
        Add(AddressEvidence(s, p));
        Add(StatedAgeCheck.Check(s, p, news));
        return e;
    }

    private static IEnumerable<Evidence> HitPrerequisites(ScreenedSubject s, SourceProfile p)
    {
        var (nameStatus, nameDetail) = NameComparison.CompareNames(s.Names, p.Names);
        var gender = Compare(s.Gender is null, p.Gender is null, () => s.Gender == p.Gender);
        var nationality = Compare(s.Nationalities.Count == 0, p.Citizenship.Count == 0, () => s.Nationalities.Intersect(p.Citizenship).Any());
        return
        [
            Prerequisite(EvidenceGroup.Name, nameStatus, nameDetail),
            Prerequisite(EvidenceGroup.Gender, gender, gender == ComparisonStatus.Conflict ? ReasonCodes.GenderConflict : null),
            Prerequisite(EvidenceGroup.Nationality, nationality),
        ];
    }

    private static Evidence Prerequisite(EvidenceGroup g, ComparisonStatus status, string? detail = null) =>
        new(g, Direction.Prerequisite, status, Strength.None, detail);

    private static List<Relative> SubjectRelatives(ScreenedSubject s, bool isEntity) =>
        s.Relatives?.Where(r => r.IsEntity == isEntity).ToList() ?? [];

    /// <summary>Detail of a value present on both sides that cannot be compared (other document type or country, occupation category only).</summary>
    public const string NotComparable = EvidenceDetails.NotComparable;

    /// <summary>
    /// Two documents say something about identity only for the same type and a known, equal issuing country; an IMO number
    /// identifies a vessel whatever its flag. The person, entity and direct-identifier rules all use this one test.
    /// </summary>
    public static bool AreComparable(Document a, Document b) =>
        a.Type == b.Type && (a.Type == "IMO_NUMBER" || a.Country is not null && a.Country == b.Country);

    internal static string DocumentNumber(string n) => NameComparison.NameKey(CjkText.HalfWidth(n));

    /// <summary>Comparable documents with equal numbers are unique support, different ones a conflict.</summary>
    public static Evidence DocumentEvidence(Document? subject, IReadOnlyList<Document> profile)
    {
        const EvidenceGroup g = EvidenceGroup.Document;
        if (subject is null) return new(g, Direction.Neutral, ComparisonStatus.SubjectMissing);
        if (profile.Count == 0) return new(g, Direction.Neutral, ComparisonStatus.ProfileMissing);
        var comparable = profile.Where(d => AreComparable(d, subject)).ToList();
        if (comparable.Count == 0) return new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, NotComparable);
        return comparable.Any(d => DocumentNumber(d.Number) == DocumentNumber(subject.Number))
            ? new(g, Direction.Support, ComparisonStatus.Exact, Strength.Unique)
            : new(g, Direction.Conflict, ComparisonStatus.Conflict, Strength.None, ReasonCodes.DocumentIdentityConflict);
    }

    /// <summary>
    /// A labelled identifier that is not an identity document: the same number on both sides is LOW support (its nature is
    /// unconfirmed); a different number is never a conflict.
    /// </summary>
    public static Evidence? OtherIdentifierEvidence(string? subject, IReadOnlyList<OtherIdentifier> profile)
    {
        const EvidenceGroup g = EvidenceGroup.OtherIdentifier;
        if (subject is null) return profile.Count > 0 ? new(g, Direction.Neutral, ComparisonStatus.SubjectMissing) : null;
        if (profile.Count == 0) return new(g, Direction.Neutral, ComparisonStatus.ProfileMissing);
        return profile.Any(x => DocumentNumber(x.Number) == DocumentNumber(subject))
            ? new(g, Direction.Support, ComparisonStatus.Exact, Strength.Low, EvidenceDetails.SameIdentifierNatureUnconfirmed)
            : new(g, Direction.Neutral, ComparisonStatus.Uncertain);
    }

    /// <summary>
    /// Age at every dated event (role start, report date) against the approved role constraints. Plausible is recorded as
    /// TEMPORALLY_PLAUSIBLE and is never support; an impossible age or role carries the hard-conflict code.
    /// </summary>
    public static Evidence? TemporalEvidence(ScreenedSubject s, SourceProfile p, NewsItem? news)
    {
        const EvidenceGroup g = EvidenceGroup.TemporalRole;
        if (s.DateOfBirth is not { Precision: DatePrecision.Day } dob) return null;
        var events = p.Roles.Select(r => r.From).OfType<PartialDate>().ToList();
        if (news?.PublishedOn is { } at) events.Add(at);
        if (events.Count == 0) return null;
        var hard = HardConflictGate.Check(s, p);
        if (hard is ReasonCodes.ImpossibleAgeAtReportedEvent or ReasonCodes.ImpossibleRoleAtReportedEvent)
            return new(g, Direction.Conflict, ComparisonStatus.Conflict, Strength.None, hard);
        var youngest = events.Min(d => HardConflictGate.MaxAgeAt(dob, d));
        var note = $"最早事件時最多 {youngest} 歲";
        return youngest >= HardConflictGate.MinimumRoleAge
            ? new(g, Direction.Neutral, ComparisonStatus.Exact, Strength.None, ReasonCodes.TemporallyPlausible, note)
            : new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, null, note);
    }

    /// <summary>
    /// Same organization counts only when both sides state overlapping periods; without periods it stays UNCERTAIN.
    /// A different employer is never a conflict (people hold several roles).
    /// </summary>
    public static Evidence EmploymentEvidence(ScreenedSubject s, SourceProfile p)
    {
        const EvidenceGroup g = EvidenceGroup.Employment;
        var profile = ProfileEmployments(p);
        var subject = s.Employments ?? [];
        if (subject.Count == 0) return EmploymentWithoutSubjectEmployer(hasOccupation: s.Occupation is not null, profileHasEmployment: profile.Count > 0);
        if (profile.Count == 0) return new(g, Direction.Neutral, ComparisonStatus.ProfileMissing);

        var sameOrganization = subject.SelectMany(a => profile, (a, b) => (a, b))
            .Where(x => NameComparison.NameKey(x.a.Organization, true) == NameComparison.NameKey(x.b.Organization, true)).ToList();
        if (sameOrganization.Count == 0) return new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, EvidenceDetails.DifferentOrganization);
        return sameOrganization.Any(x => PeriodsOverlapByYear(x.a, x.b))
            ? new(g, Direction.Support, ComparisonStatus.Exact, Strength.Medium, EvidenceDetails.SameOrganizationSamePeriod)
            : new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, EvidenceDetails.SameOrganizationNoCommonPeriod);
    }

    private static List<Employment> ProfileEmployments(SourceProfile p) =>
        p.Roles.Select(r => new Employment(r.Organization, r.Title, r.From, r.To))
            .Concat(p.Relationships.Where(r => IsEmployer(r.Relation)).Select(r => new Employment(r.Name, null, null, null)))
            .ToList();

    private static Evidence EmploymentWithoutSubjectEmployer(bool hasOccupation, bool profileHasEmployment)
    {
        const EvidenceGroup g = EvidenceGroup.Employment;
        return hasOccupation && profileHasEmployment
            ? new(g, Direction.Neutral, ComparisonStatus.SubjectMissing, Strength.None, NotComparable)
            : new(g, Direction.Neutral, hasOccupation ? ComparisonStatus.ProfileMissing : ComparisonStatus.SubjectMissing);
    }

    private static bool PeriodsOverlapByYear(Employment a, Employment b) =>
        a.From is { } af && b.From is { } bf && Math.Max(af.Year, bf.Year) <= Math.Min(EndYearOrOpen(a), EndYearOrOpen(b));

    private static int EndYearOrOpen(Employment e) => e.To?.Year ?? int.MaxValue;

    /// <summary>
    /// A relative or related company named on both sides with the same relation and an EXACT name is support.
    /// An undated relationship holds only as of the source capture date, so nothing is projected backwards.
    /// </summary>
    public static Evidence? RelativeEvidence(EvidenceGroup g, IReadOnlyList<Relative> subject, IReadOnlyList<ProfileRelationship> profile, Strength strength)
    {
        if (subject.Count == 0)
            return profile.Count > 0 ? new(g, Direction.Neutral, ComparisonStatus.SubjectMissing) : null;
        if (profile.Count == 0) return new(g, Direction.Neutral, ComparisonStatus.ProfileMissing);
        var same = subject.FirstOrDefault(a => profile.Any(b => CanonicalRelation(a.Relation) == CanonicalRelation(b.Relation)
            && NameComparison.CompareNames([a.Name], [b.Name]).Status == ComparisonStatus.Exact));
        return same is not null
            ? new(g, Direction.Support, ComparisonStatus.Exact, strength, $"{EvidenceDetails.SameNamedRelative}:{CanonicalRelation(same.Relation)}")
            : new(g, Direction.Neutral, ComparisonStatus.Uncertain);
    }

    internal static bool IsEmployer(string relation) => relation.Equals("Employer", StringComparison.OrdinalIgnoreCase);

    // ponytail: relation names seen in DJ exports and bank data; extend as new labels appear.
    private static string CanonicalRelation(string raw) => raw.Trim().ToUpperInvariant() switch
    {
        "WIFE" or "HUSBAND" or "配偶" or "妻" or "夫" => "SPOUSE",
        "FATHER" or "MOTHER" or "父" or "母" or "父親" or "母親" => "PARENT",
        "SON" or "DAUGHTER" or "子" or "女" or "兒子" or "女兒" => "CHILD",
        "BROTHER" or "SISTER" or "兄弟" or "姊妹" => "SIBLING",
        var alreadyCanonicalOrUnseen => alreadyCanonicalOrUnseen,
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
        var sharesCountry = s.Addresses.Any(a => a.Country is { } c && p.AddressCountries.Contains(c));
        if (!sharesCountry || subjectText.Count == 0 || profileText.Count == 0)
            return new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, EvidenceDetails.SoftDifference);

        var keys = subjectText.Select(TaiwanAddresses.Key).OfType<string>().ToHashSet();
        if (profileText.Select(TaiwanAddresses.Key).Any(k => k is not null && keys.Contains(k)))
            return new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, EvidenceDetails.SameAddressNoPeriod);
        var jaccard = subjectText.SelectMany(a => profileText, AddressCompare.Jaccard).Max();
        var similarity = jaccard >= AddressCompare.JaccardThreshold ? "SIMILAR" : "DISSIMILAR";
        return new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None,
            string.Create(CultureInfo.InvariantCulture, $"{EvidenceDetails.SoftDifference}（Jaccard {jaccard:0.00}，{similarity}）"));
    }

    private static Evidence DateOfBirthEvidence(ScreenedSubject s, SourceProfile p)
    {
        const EvidenceGroup g = EvidenceGroup.DateOfBirth;
        if (s.DateOfBirth is not { } dob) return new(g, Direction.Neutral, ComparisonStatus.SubjectMissing);
        var dobs = ProfileDatesOfBirth(p);
        if (dobs.Count == 0) return new(g, Direction.Neutral, ComparisonStatus.ProfileMissing);

        var agreeing = dobs.Where(dob.AgreesWith).OrderByDescending(d => d.Precision).ToList();
        if (agreeing.Count == 0) return DobConflictEvidence(dob, dobs);
        var precision = (DatePrecision)Math.Min((int)dob.Precision, (int)agreeing[0].Precision);
        var status = precision == DatePrecision.Day ? ComparisonStatus.Exact : ComparisonStatus.Partial;
        // Scattered DOBs are source ambiguity: agreeing with one of them is never support (never stage 3 row 4).
        if (AreScattered(dobs))
            return new(g, Direction.Neutral, status, Strength.None, Findings.ProfileMultipleDob);
        return new(g, Direction.Support, status, SupportStrength(precision),
            precision == DatePrecision.Year ? ReasonCodes.BirthYearMatchFullDateNotComparable : null);
    }

    private static Evidence DobConflictEvidence(PartialDate dob, IReadOnlyList<PartialDate> profileDobs)
    {
        var bothFullDates = dob.Precision == DatePrecision.Day && profileDobs.Any(d => d.Precision == DatePrecision.Day);
        return new(EvidenceGroup.DateOfBirth, Direction.Conflict, ComparisonStatus.Conflict, Strength.None,
            bothFullDates ? ReasonCodes.FullDobConflict : ReasonCodes.DobConflict);
    }

    // ponytail: 3 values or a span over 2 years is a placeholder cut-off pending calibration.
    private const int ScatteredDobCount = 3;
    private const int ScatteredDobSpanYears = 2;

    private static bool AreScattered(IReadOnlyList<PartialDate> dobs) =>
        dobs.Count >= ScatteredDobCount || dobs.Max(d => d.Year) - dobs.Min(d => d.Year) > ScatteredDobSpanYears;

    private static Strength SupportStrength(DatePrecision agreedPrecision) => agreedPrecision switch
    {
        DatePrecision.Day => Strength.High,
        DatePrecision.Month => Strength.Medium,
        _ => Strength.Low,
    };

    /// <summary>The Profile's distinct DOBs without a coarser value a more precise one already covers (1970 next to 1970-05-01).</summary>
    public static IReadOnlyList<PartialDate> ProfileDatesOfBirth(SourceProfile p) =>
        p.DatesOfBirth.Distinct().Where(d => !p.DatesOfBirth.Any(x => x.Precision > d.Precision && d.AgreesWith(x))).ToList();

    private static ComparisonStatus Compare(bool subjectMissing, bool profileMissing, Func<bool> agrees) =>
        subjectMissing ? ComparisonStatus.SubjectMissing
        : profileMissing ? ComparisonStatus.ProfileMissing
        : agrees() ? ComparisonStatus.Exact : ComparisonStatus.Conflict;
}
