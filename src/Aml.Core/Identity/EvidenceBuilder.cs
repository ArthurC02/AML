using System.Globalization;
using Aml.Core.Normalization;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Identity;

/// <summary>Builds per-group evidence for one screened subject versus one Profile.</summary>
public static class EvidenceBuilder
{
    public static IReadOnlyDictionary<EvidenceGroup, Evidence> Build(ScreenedSubject s, SourceProfile p, NewsItem? news = null)
    {
        var e = new Dictionary<EvidenceGroup, Evidence>();
        void Add(EvidenceGroup g, Direction d, ComparisonStatus status, Strength strength = Strength.None, string? detail = null) =>
            e[g] = new Evidence(g, d, status, strength, detail);

        // Hit prerequisites: recorded, never support.
        var (nameStatus, nameDetail) = Normalize.CompareNames(s.Names, p.Names);
        Add(EvidenceGroup.Name, Direction.Prerequisite, nameStatus, detail: nameDetail);
        var gender = Compare(s.Gender is null, p.Gender is null, () => s.Gender == p.Gender);
        Add(EvidenceGroup.Gender, Direction.Prerequisite, gender, detail: gender == ComparisonStatus.Conflict ? ReasonCodes.GenderConflict : null);
        Add(EvidenceGroup.Nationality, Direction.Prerequisite,
            Compare(s.Nationalities.Count == 0, p.Citizenship.Count == 0, () => s.Nationalities.Intersect(p.Citizenship).Any()));

        e[EvidenceGroup.DateOfBirth] = DateOfBirthEvidence(s, p);

        // A reference number never becomes DOCUMENT evidence.
        e[EvidenceGroup.Document] = DocumentEvidence(s.Document, p.Documents ?? []);
        if (OtherIdentifierEvidence(s.ReferenceNumber, p.OtherIdentifiers ?? []) is { } other) e[EvidenceGroup.OtherIdentifier] = other;
        if (TemporalEvidence(s, p, news) is { } temporal) e[EvidenceGroup.TemporalRole] = temporal;

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

    /// <summary>Detail of a value present on both sides that cannot be compared (other document type or country, occupation category only).</summary>
    public const string NotComparable = EvidenceDetails.NotComparable;

    /// <summary>
    /// Two documents say something about identity only for the same type and a known, equal issuing country; an IMO number
    /// identifies a vessel whatever its flag. The person, entity and direct-identifier rules all use this one test.
    /// </summary>
    public static bool Comparable(Document a, Document b) =>
        a.Type == b.Type && (a.Type == "IMO_NUMBER" || a.Country is not null && a.Country == b.Country);

    internal static string DocumentNumber(string n) => Normalize.NameKey(Normalize.HalfWidth(n));

    /// <summary>Comparable documents with equal numbers are unique support, different ones a conflict.</summary>
    public static Evidence DocumentEvidence(Document? subject, IReadOnlyList<Document> profile)
    {
        const EvidenceGroup g = EvidenceGroup.Document;
        if (subject is null) return new(g, Direction.Neutral, ComparisonStatus.SubjectMissing);
        if (profile.Count == 0) return new(g, Direction.Neutral, ComparisonStatus.ProfileMissing);
        var comparable = profile.Where(d => Comparable(d, subject)).ToList();
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
        return youngest >= HardConflictGate.MinimumRoleAge
            ? new(g, Direction.Neutral, ComparisonStatus.Exact, Strength.None, ReasonCodes.TemporallyPlausible,
                $"最早事件時最多 {youngest} 歲")
            : new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, null, $"最早事件時最多 {youngest} 歲");
    }

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
            // An occupation category alone is not structured evidence: the employer is missing, so nothing is comparable.
            return s.Occupation is not null && profile.Count > 0
                ? new(g, Direction.Neutral, ComparisonStatus.SubjectMissing, Strength.None, NotComparable)
                : new(g, Direction.Neutral, s.Occupation is null ? ComparisonStatus.SubjectMissing : ComparisonStatus.ProfileMissing);
        if (profile.Count == 0) return new(g, Direction.Neutral, ComparisonStatus.ProfileMissing);

        var same = subject.SelectMany(a => profile, (a, b) => (a, b))
            .Where(x => Normalize.NameKey(x.a.Organization, true) == Normalize.NameKey(x.b.Organization, true)).ToList();
        if (same.Count == 0) return new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, EvidenceDetails.DifferentOrganization);
        return same.Any(x => Overlap(x.a, x.b))
            ? new(g, Direction.Support, ComparisonStatus.Exact, Strength.Medium, EvidenceDetails.SameOrganizationSamePeriod)
            : new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, EvidenceDetails.SameOrganizationNoCommonPeriod);
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
        var same = subject.FirstOrDefault(a => profile.Any(b => Relation(a.Relation) == Relation(b.Relation)
            && Normalize.CompareNames([a.Name], [b.Name]).Status == ComparisonStatus.Exact));
        return same is not null
            ? new(g, Direction.Support, ComparisonStatus.Exact, strength, $"{EvidenceDetails.SameNamedRelative}:{Relation(same.Relation)}")
            : new(g, Direction.Neutral, ComparisonStatus.Uncertain);
    }

    internal static bool IsEmployer(string relation) => relation.Equals("Employer", StringComparison.OrdinalIgnoreCase);

    // ponytail: relation names seen in DJ exports and bank data; extend as new labels appear.
    private static string Relation(string raw) => raw.Trim().ToUpperInvariant() switch
    {
        "WIFE" or "HUSBAND" or "配偶" or "妻" or "夫" => "SPOUSE",
        "FATHER" or "MOTHER" or "父" or "母" or "父親" or "母親" => "PARENT",
        "SON" or "DAUGHTER" or "子" or "女" or "兒子" or "女兒" => "CHILD",
        "BROTHER" or "SISTER" or "兄弟" or "姊妹" => "SIBLING",
        var other => other, // SPOUSE, PARENT, CHILD and SIBLING name themselves
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
            return new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, EvidenceDetails.SoftDifference);

        var keys = subjectText.Select(AddressCompare.TaiwanKey).OfType<string>().ToHashSet();
        if (profileText.Select(AddressCompare.TaiwanKey).Any(k => k is not null && keys.Contains(k)))
            return new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, EvidenceDetails.SameAddressNoPeriod);
        var jaccard = subjectText.SelectMany(a => profileText, AddressCompare.Jaccard).Max();
        return new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None,
            string.Create(CultureInfo.InvariantCulture, $"{EvidenceDetails.SoftDifference}（Jaccard {jaccard:0.00}，{(jaccard >= AddressCompare.JaccardThreshold ? "SIMILAR" : "DISSIMILAR")}）"));
    }

    private static Evidence DateOfBirthEvidence(ScreenedSubject s, SourceProfile p)
    {
        const EvidenceGroup g = EvidenceGroup.DateOfBirth;
        if (s.DateOfBirth is not { } dob) return new(g, Direction.Neutral, ComparisonStatus.SubjectMissing);
        var dobs = ProfileDatesOfBirth(p);
        if (dobs.Count == 0) return new(g, Direction.Neutral, ComparisonStatus.ProfileMissing);

        // A Profile with several DOBs is source ambiguity: one agreeing value is support, the others are not conflicts.
        var agreeing = dobs.Where(dob.AgreesWith).OrderByDescending(d => d.Precision).ToList();
        if (agreeing.Count == 0)
        {
            var full = dob.Precision == DatePrecision.Day && dobs.Any(d => d.Precision == DatePrecision.Day);
            return new(g, Direction.Conflict, ComparisonStatus.Conflict, Strength.None, full ? ReasonCodes.FullDobConflict : ReasonCodes.DobConflict);
        }
        var precision = (DatePrecision)Math.Min((int)dob.Precision, (int)agreeing[0].Precision);
        var status = precision == DatePrecision.Day ? ComparisonStatus.Exact : ComparisonStatus.Partial;
        // Agreeing with one of many scattered values is no rare coincidence: recorded, never support, never row 4.
        // ponytail: 3 values or a span over 2 years is a placeholder cut-off pending calibration.
        if (dobs.Count >= 3 || dobs.Max(d => d.Year) - dobs.Min(d => d.Year) > 2)
            return new(g, Direction.Neutral, status, Strength.None, Findings.ProfileMultipleDob);
        return new(g, Direction.Support, status,
            precision switch { DatePrecision.Day => Strength.High, DatePrecision.Month => Strength.Medium, _ => Strength.Low },
            precision == DatePrecision.Year ? ReasonCodes.BirthYearMatchFullDateNotComparable : null);
    }

    /// <summary>The Profile's distinct DOBs without a coarser value a more precise one already covers (1970 next to 1970-05-01).</summary>
    public static IReadOnlyList<PartialDate> ProfileDatesOfBirth(SourceProfile p) =>
        p.DatesOfBirth.Distinct().Where(d => !p.DatesOfBirth.Any(x => x.Precision > d.Precision && d.AgreesWith(x))).ToList();

    private static ComparisonStatus Compare(bool subjectMissing, bool profileMissing, Func<bool> agrees) =>
        subjectMissing ? ComparisonStatus.SubjectMissing
        : profileMissing ? ComparisonStatus.ProfileMissing
        : agrees() ? ComparisonStatus.Exact : ComparisonStatus.Conflict;
}
