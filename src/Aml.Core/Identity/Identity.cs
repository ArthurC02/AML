using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Aml.Core.Normalization;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Identity;

public enum IdentityDecision { NoMatch, Review, ProbableMatch }

public enum Assumption { Agree, Conflict }

/// <summary>
/// What stage 3 would decide if one direct identifier became comparable on both sides. It names the row and code only,
/// never the external result, and holds every other piece of evidence fixed.
/// </summary>
public sealed record WouldChangeIf(
    [property: Description("假設的項目：DOCUMENT 或 DATE_OF_BIRTH")] EvidenceGroup Field,
    [property: Description("假設相符（AGREE）或衝突（CONFLICT）")] Assumption Assumption,
    [property: Description("假設成立時的第三階段列號")] int Stage3Row,
    [property: Description("假設成立時的身分判斷（不是對外結果）")] IdentityDecision Decision,
    [property: Description("假設成立時的理由碼")] string ReasonCode,
    [property: Description("中文說明")] string Text);

public sealed record IdentityResult(
    [property: Description("各證據群組的比對結果")] IReadOnlyDictionary<EvidenceGroup, Evidence> Evidence,
    [property: Description("第一階段硬衝突代碼")] string? HardConflict,
    [property: Description("第三階段決策表列號（1～6）；硬衝突或非自然人規則時可能為 null")] int? Stage3Row,
    [property: Description("身分判斷")] IdentityDecision Decision,
    [property: Description("身分判斷的理由碼，第一個是決定的理由")] IReadOnlyList<string> ReasonCodes);

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

    private static bool IsEmployer(string relation) => relation.Equals("Employer", StringComparison.OrdinalIgnoreCase);

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

/// <summary>
/// Age explicitly stated for the Profile person in attached news (identity-adjudication.md, age stated in a source).
/// Compared at the report date. Within ±2 years is compatible, never support; beyond is a soft conflict, never hard.
/// </summary>
public static class StatedAgeCheck
{
    public const int ToleranceYears = 2;

    public static Evidence? Check(ScreenedSubject s, SourceProfile p, NewsItem? news)
    {
        if (news is null || Find(news.Text, p.Names) is not var (stated, atEvent))
            return null;
        // 時年 is the age when the reported event happened, whose date the text does not give; the report date is not it.
        if (atEvent)
            return new Evidence(EvidenceGroup.StatedAge, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, ReasonCodes.AgeNotComparable,
                $"報導年齡 {stated} 歲（時年，事件當時）；事件日期未知，不與報導日期比較");
        if (s.DateOfBirth is not { Precision: DatePrecision.Day } dob || news.PublishedOn is not { Precision: DatePrecision.Day } at)
            return new Evidence(EvidenceGroup.StatedAge, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, ReasonCodes.AgeNotComparable,
                $"報導年齡 {stated} 歲；缺少完整出生日期或報導日期，無法換算");

        var age = at.Year - dob.Year - ((at.Month, at.Day).CompareTo((dob.Month, dob.Day)) < 0 ? 1 : 0);
        var note = $"報導年齡 {stated} 歲；依報導日期 {at} 推算 {age} 歲";
        return Math.Abs(stated - age) <= ToleranceYears
            ? new Evidence(EvidenceGroup.StatedAge, Direction.Neutral, ComparisonStatus.Exact, Strength.None, ReasonCodes.AgeCompatibleAtReport, note)
            : new Evidence(EvidenceGroup.StatedAge, Direction.Conflict, ComparisonStatus.Conflict, Strength.None, ReasonCodes.AgeDifferenceAtReport, note);
    }

    // Arabic or Chinese numerals; 足歲, 周歲 and 週歲 mean 歲. A number followed by 多/餘 never reaches 歲, so ranges drop out.
    private const string Num = "(?<n>\\d{1,3}|[〇零一二兩三四五六七八九十廿卅百]{1,5})";
    private const string Years = "\\s?(?:足|周|週)?歲";
    // Thresholds and approximations before the number (未滿 18 歲, 約 40 歲) are not a stated age.
    private const string NotThreshold = "(?<![\\d〇零一二兩三四五六七八九十廿卅百]|滿|約|逾|近|超過|未滿|年滿)";

    /// <summary>
    /// "陳志明（45歲）", "陳志明今年四十五歲", "45歲的陳志明", "Chen Chih-Ming, aged 45", "45-year-old Chen Chih-Ming",
    /// "甲、乙分別為 44 歲、45 歲" (one age per person, in order); within one sentence and adjacent to the name.
    /// </summary>
    public static int? FindStatedAge(string text, IEnumerable<string> names) => Find(text, names)?.Age;

    // AtEvent: the age was stated with 時年 (at the time of the event), not as of the report.
    private static (int Age, bool AtEvent)? Find(string text, IEnumerable<string> names)
    {
        var list = names.Where(n => n.Length > 0).ToList();
        foreach (var sentence in text.Split('。', '！', '？', '\n', '；'))
        {
            if (Respectively(sentence, list) is { } each) return (each, false);
            foreach (var name in list)
            {
                var n = Regex.Escape(name);
                var m = Regex.Match(sentence,
                    $@"{n}\s?[（(，,]?\s?(?:今年|現年|(?<event>時年))?\s?{Num}{Years}|{NotThreshold}{Num}{Years}的?{n}"
                    + $@"|{n},?\s+aged\s+(?<n>\d{{1,3}})\b|\b(?<n>\d{{1,3}})-year-old\s+{n}|{n}\s?\((?<n>\d{{1,3}})\)",
                    RegexOptions.IgnoreCase);
                if (m.Success && Normalize.ChineseNumber(m.Groups["n"].Value) is { } age) return (age, m.Groups["event"].Success);
            }
        }
        return null;
    }

    // "甲、乙分別為44歲、45歲" or "甲和乙分別44、45歲": the names before 分別 and the ages after it pair up in order.
    private static int? Respectively(string sentence, IReadOnlyList<string> names)
    {
        var at = sentence.IndexOf("分別", StringComparison.Ordinal);
        if (at <= 0) return null;
        var who = Regex.Match(sentence[..at], "[\\p{IsCJKUnifiedIdeographs}、和及與]+$").Value.Split('、', '和', '及', '與');
        var ages = Regex.Matches(sentence[(at + 2)..].TrimStart('為', ' '), $"\\G{Num}(?:{Years})?\\s?[、，,和及]?\\s?")
            .Select(m => Normalize.ChineseNumber(m.Groups["n"].Value)).ToList();
        if (ages.Count != who.Length || ages.Any(a => a is null)) return null;
        var index = Array.FindIndex(who, w => names.Any(n => w.EndsWith(n, StringComparison.Ordinal)));
        return index >= 0 ? ages[index] : null;
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
        || p.Findings.Any(f => f.StartsWith("UNPARSEABLE", StringComparison.Ordinal))
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
}

/// <summary>Stage 3 decision table (identity-adjudication.md). The first matching row wins.</summary>
public static class IdentityDecisionPolicy
{
    public static IdentityResult Decide(ScreenedSubject s, SourceProfile p, NewsItem? news = null)
    {
        var e = EvidenceBuilder.Build(s, p, news);
        // The person rules do not apply to organizations, vessels and other records; entity identifiers decide instead.
        if (!p.RecordType.Equals("Person", StringComparison.OrdinalIgnoreCase))
            return EntityDecision(s, p, new Dictionary<EvidenceGroup, Evidence>(e));
        if (HardConflictGate.Check(s, p) is { } hard)
        {
            if (!HardConflictGate.Validated.Contains(hard)) return new IdentityResult(e, hard, null, IdentityDecision.Review, [hard]);
            return HardConflictGate.SourceQualityConcern(p, e)
                ? new IdentityResult(e, hard, null, IdentityDecision.Review, [hard, ReasonCodes.SourceQualityDowngrade])
                : new IdentityResult(e, hard, null, IdentityDecision.NoMatch, [hard]);
        }

        var (row, decision, code) = Stage3(e);
        var reasons = new List<string> { code };
        if (e[EvidenceGroup.Gender].Status == ComparisonStatus.Conflict) reasons.Add(ReasonCodes.GenderConflict);
        if (NoBilateralDirectIdentifier(s, p)) reasons.Add(ReasonCodes.NoBilateralDirectIdentifier);
        return new IdentityResult(e, null, row, decision, reasons);
    }

    /// <summary>
    /// Entity evidence groups: a registration number with its jurisdiction, or an IMO number for a vessel. The same key on
    /// both sides is UNIQUE support (PROBABLE_MATCH, externally REVIEW); a different number under the same key is a conflict.
    /// A natural-person subject is never compared with an entity record. Nothing here is automatic MATCH or NO_MATCH.
    /// ponytail: entity groups pending validation with an entity Golden Case (system-overview.md, excluded for Phase 1).
    /// </summary>
    private static IdentityResult EntityDecision(ScreenedSubject s, SourceProfile p, Dictionary<EvidenceGroup, Evidence> e)
    {
        const EvidenceGroup g = EvidenceGroup.EntityIdentifier;
        if (!s.IsEntity) return new IdentityResult(e, null, null, IdentityDecision.Review, [ReasonCodes.NonPersonSubjectReview]);
        var subject = new[] { s.Document, s.TaxId is { State: ValueState.Present, Value: { } tax } ? new Document("REGISTRATION_NUMBER", tax, "TW") : null }
            .OfType<Document>().Where(d => d.Type is "REGISTRATION_NUMBER" or "IMO_NUMBER").ToList();
        var profile = (p.Documents ?? []).Where(d => d.Type is "REGISTRATION_NUMBER" or "IMO_NUMBER").ToList();
        var comparable = subject.SelectMany(a => profile.Where(b => EvidenceBuilder.Comparable(a, b)), (a, b) => (a, b)).ToList();
        if (comparable.Count == 0)
        {
            e[g] = new(g, Direction.Neutral, subject.Count == 0 ? ComparisonStatus.SubjectMissing : profile.Count == 0 ? ComparisonStatus.ProfileMissing : ComparisonStatus.Uncertain);
            return new IdentityResult(e, null, null, IdentityDecision.Review, [ReasonCodes.NonPersonSubjectReview]);
        }
        if (comparable.Any(x => EvidenceBuilder.DocumentNumber(x.a.Number) == EvidenceBuilder.DocumentNumber(x.b.Number)))
        {
            e[g] = new(g, Direction.Support, ComparisonStatus.Exact, Strength.Unique);
            return new IdentityResult(e, null, 2, IdentityDecision.ProbableMatch, [ReasonCodes.EntityIdentifierAgreement]);
        }
        e[g] = new(g, Direction.Conflict, ComparisonStatus.Conflict, Strength.None, ReasonCodes.EntityIdentifierConflict);
        return new IdentityResult(e, null, 1, IdentityDecision.Review, [ReasonCodes.EntityIdentifierConflict]);
    }

    /// <summary>
    /// Contrastive explanation: the same document type and a full DOB, each assumed to agree and to conflict, re-run through
    /// stage 3 only (a full DOB would also reach the stage 1 checks, which are not re-run). Outcomes on the current row are
    /// left out. Empty outside rows 3 to 6 (which also leaves out hard conflicts and non-Person records) and for Profiles
    /// with scattered DOBs. On rows 3 to 6 a document is never comparable (agreeing is row 2, conflicting row 1), and a full
    /// DOB is comparable only when it agrees.
    /// </summary>
    public static IReadOnlyList<WouldChangeIf> Contrast(IdentityResult r)
    {
        if (r.Stage3Row is not (3 or 4 or 5 or 6)) return [];
        var dob = r.Evidence.GetValueOrDefault(EvidenceGroup.DateOfBirth);
        if (dob?.Detail == Findings.ProfileMultipleDob) return [];

        var assumptions = new List<(Evidence Evidence, Assumption Assumption, string Label)>
        {
            (new(EvidenceGroup.Document, Direction.Support, ComparisonStatus.Exact, Strength.Unique), Assumption.Agree, "同類證件相符"),
            (new(EvidenceGroup.Document, Direction.Conflict, ComparisonStatus.Conflict, Strength.None, ReasonCodes.DocumentIdentityConflict), Assumption.Conflict, "同類證件不符"),
        };
        if (dob?.Status != ComparisonStatus.Exact)
        {
            assumptions.Add((new(EvidenceGroup.DateOfBirth, Direction.Support, ComparisonStatus.Exact, Strength.High), Assumption.Agree, "完整出生日期相符"));
            assumptions.Add((new(EvidenceGroup.DateOfBirth, Direction.Conflict, ComparisonStatus.Conflict, Strength.None, ReasonCodes.FullDobConflict), Assumption.Conflict, "完整出生日期不符"));
        }

        var result = new List<WouldChangeIf>();
        foreach (var (evidence, assumption, label) in assumptions)
        {
            var e = new Dictionary<EvidenceGroup, Evidence>(r.Evidence) { [evidence.Group] = evidence };
            var (row, decision, code) = Stage3(e);
            if (row != r.Stage3Row)
                result.Add(new(evidence.Group, assumption, row, decision, code, $"若{label}：身分判斷移至第三階段第 {row} 列 {code}"));
        }
        return result;
    }

    private static (int, IdentityDecision, string) Stage3(IReadOnlyDictionary<EvidenceGroup, Evidence> e)
    {
        ComparisonStatus St(EvidenceGroup g) => e.TryGetValue(g, out var v) ? v.Status : ComparisonStatus.ProfileMissing;
        var supports = e.Values.Where(v => v.IsSupport).ToList();

        // Employment is never a conflict (a different employer is not evidence against identity), so it has no row 1 code.
        var conflict = new[] { EvidenceGroup.DateOfBirth, EvidenceGroup.Document, EvidenceGroup.StatedAge }
            .Select(g => e.GetValueOrDefault(g)).FirstOrDefault(v => v?.Status == ComparisonStatus.Conflict);
        if (conflict is not null)
            return (1, IdentityDecision.Review, conflict.Detail!); // every DOB, document and stated-age conflict carries its code

        if (supports.Any(v => v.Strength == Strength.Unique))
            return (2, IdentityDecision.ProbableMatch, ReasonCodes.UniqueIdentifierAgreement);

        // Opposite genders rule out rows 3 and 4; the pair falls to row 5 or 6 with GENDER_CONFLICT.
        var genderConflict = St(EvidenceGroup.Gender) == ComparisonStatus.Conflict;
        if (!genderConflict && supports.Any(v => v.Strength == Strength.High) && supports.Count >= 2)
            return (3, IdentityDecision.ProbableMatch, ReasonCodes.StrongSupportCombination);

        var prerequisitesAgree = St(EvidenceGroup.Name) == ComparisonStatus.Exact
            && St(EvidenceGroup.Gender) == ComparisonStatus.Exact
            && St(EvidenceGroup.Nationality) == ComparisonStatus.Exact;
        var dobAgrees = e.TryGetValue(EvidenceGroup.DateOfBirth, out var dob) && dob.IsSupport;
        // A document of another type or country is not comparable, which is not unsettled.
        var docOrEmploymentUnsettled = new[] { EvidenceGroup.Document, EvidenceGroup.Employment }
            .Select(g => e.GetValueOrDefault(g))
            .Any(v => v?.Status is ComparisonStatus.Conflict or ComparisonStatus.Uncertain && v.Detail != EvidenceBuilder.NotComparable);
        if (prerequisitesAgree && dobAgrees && !docOrEmploymentUnsettled)
            return (4, IdentityDecision.ProbableMatch, ReasonCodes.ConsistentAtAvailablePrecision);

        return supports.Count > 0
            ? (5, IdentityDecision.Review, ReasonCodes.PartialSupportOnly)
            : (6, IdentityDecision.Review, ReasonCodes.NoNonPrerequisiteSupport);
    }

    // Direct identifier: full DOB or a document key present on both sides.
    private static bool NoBilateralDirectIdentifier(ScreenedSubject s, SourceProfile p) =>
        !(s.DateOfBirth?.Precision == DatePrecision.Day && p.DatesOfBirth.Any(d => d.Precision == DatePrecision.Day))
        && !(s.Document is { } d && (p.Documents ?? []).Any(x => EvidenceBuilder.Comparable(x, d)));
}
