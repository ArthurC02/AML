using System.Text.RegularExpressions;
using Aml.Core.Normalization;
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
public static partial class HardConflictGate
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
    public static string? Check(ScreenedSubject s, SourceProfile p, IReadOnlyDictionary<EvidenceGroup, Evidence>? evidence = null, NewsItem? news = null) =>
        Check(s, p, evidence, news is null ? [] : [news]);

    /// <summary>As <see cref="Check(ScreenedSubject, SourceProfile, IReadOnlyDictionary{EvidenceGroup, Evidence}?, NewsItem?)"/>, the stated values read across every news item of the Profile.</summary>
    public static string? Check(ScreenedSubject s, SourceProfile p, IReadOnlyDictionary<EvidenceGroup, Evidence>? evidence, IReadOnlyList<NewsItem> news)
    {
        if (s.DateOfBirth is not { } dob) return null;
        if (p.DateOfDeath is { } death && Before(death, dob)) return ReasonCodes.DeathBeforeCustomerBirth;
        var role = RoleConflict(dob, p);
        if (role == ReasonCodes.ImpossibleAgeAtReportedEvent) return role;
        return EvidenceBuilder.DateOfBirthEvidence(s, p) is { Direction: Direction.Conflict, Detail: { } dobConflict } ? dobConflict
            : role ?? StatedConflict(s, dob, p, evidence, news);
    }

    /// <summary>The attached item that grounds the hint: its place (from 1), its header date, the role read and the customer's oldest age that day.</summary>
    public sealed record DocumentAge(int Item, PartialDate On, string Role, int MaxAge);

    /// <summary>
    /// Implausible age at a dated document, advisory (user, 2026-11-27: a hint until real labelled Cases measure it; it is
    /// no hard conflict and never concludes): the customer, born on a known day, was under <see cref="MinimumRoleAge"/> at
    /// most on the header date (to the day) of an attached item in which the rules read for the Profile person, under a full
    /// name form (<see cref="StatedAgeCheck.IsFullForm"/>), a role a child cannot hold (<see cref="RoleAChildCannotHold"/>);
    /// a customer born after the header date (a negative age) too. One such item is enough. Silent when the Profile has a
    /// DOB of its own (the birth-date rules decide), and for an item whose stated age is compatible or stated birth date
    /// agrees with the customer's (the item may be about the customer).
    /// </summary>
    public static DocumentAge? ImplausibleAgeAtDocument(ScreenedSubject s, SourceProfile p, IReadOnlyList<NewsItem> news)
    {
        if (s.DateOfBirth is not { Precision: DatePrecision.Day } dob || p.DatesOfBirth.Count > 0) return null;
        var names = p.Names.Where(StatedAgeCheck.IsFullForm).ToList();
        if (names.Count == 0) return null;
        for (var i = 0; i < news.Count; i++)
        {
            if (news[i].PublishedOn is not { Precision: DatePrecision.Day } on || MaxAgeAt(dob, on) >= MinimumRoleAge) continue;
            if (StatedAgeCheck.Check(s, p, news[i])?.Status == ComparisonStatus.Exact
                || NewsStatedCheck.CheckBirthDate(s, p, news[i], null)?.Detail == ReasonCodes.NewsBirthDateAgrees) continue;
            if (RoleAChildCannotHold(news[i].Text, names) is { } role) return new(i + 1, on, role, MaxAgeAt(dob, on));
        }
        return null;
    }

    /// <summary>The notes' event that grounds the hint: its date, the act word read, the customer's oldest age then (negative: not yet born) and the sentence's place.</summary>
    public sealed record NotesAge(PartialDate On, string Act, int MaxAge, string Location);

    /// <summary>
    /// The same advisory hint from the Profile's own English notes: the customer, born on a known day, was under
    /// <see cref="MinimumRoleAge"/> at most at the earliest event the notes date for the Profile person
    /// (<see cref="NotesEvents"/>), or not yet born. An event dated to the year only suffices: the oldest age is taken over
    /// every day it can stand for. Silent when the Profile has a DOB of its own, has no Latin name form, when the notes state
    /// an age of the person (its reference date is not read), or a birth date within
    /// <see cref="EvidenceBuilder.DobToleranceYears"/> years of the customer's.
    /// </summary>
    public static NotesAge? ImplausibleAgeInNotes(ScreenedSubject s, SourceProfile p)
    {
        if (s.DateOfBirth is not { Precision: DatePrecision.Day } dob || p.DatesOfBirth.Count > 0 || p.Notes is not { } notes) return null;
        var latin = p.Names.Where(n => n.Any(char.IsAsciiLetter)).ToList();
        if (latin.Count == 0 || NotesEvents.Read(notes, latin) is not { Events.Count: > 0 } r || r.StatesAge
            || r.BirthDates.Any(b => !dob.IsMoreThanYearsApart(b, EvidenceBuilder.DobToleranceYears))) return null;
        var first = r.Events.MinBy(e => MaxAgeAt(dob, e.On))!;
        var age = MaxAgeAt(dob, first.On);
        return age < MinimumRoleAge ? new(first.On, first.Act, age, first.Location) : null;
    }

    /// <summary>
    /// The word a child cannot be called that stands directly before a whole full-name mention of the Profile person in
    /// <paramref name="text"/>: a suspect word or a company officer word, optionally after 前. Read from the mentions alone:
    /// no event the digest attributes, no role of an alias folded into the person. Null when any mention of a full name is
    /// directly preceded by a victim, complainant, witness, child or official word, or when none is preceded by a
    /// qualifying word. A mention is whole when the name is followed by punctuation, whitespace, the end, or one of
    /// <see cref="DocumentAgeWords.SafeFollowers"/> (陳志明宏, 陳志明國小 are other names). Silent for the whole item when it
    /// holds a minor or child cue or an age under 18 anywhere, or a nominal-officer or used-name cue; a mention whose
    /// sentence holds a negation, mistaken-identity, misuse or harm cue, or whose word follows 非, does not qualify.
    /// </summary>
    internal static string? RoleAChildCannotHold(string text, IReadOnlyList<string> fullNames)
    {
        // A minor or child context, or a nominal officer or used name, anywhere in the item: silent, on purpose for the whole item.
        if (DocumentAgeWords.MinorCues.Concat(DocumentAgeWords.NominalCues).Any(w => text.Contains(w, StringComparison.Ordinal)) || StatesMinorAge(text)) return null;
        // Sentence ends found once; each sentence's cues are checked once (linear in the text, whatever the mentions).
        var ends = new List<int>();
        for (var i = text.AsSpan().IndexOfAny(DocumentAgeWords.SentenceEnds); i >= 0;)
        {
            ends.Add(i);
            var next = text.AsSpan(i + 1).IndexOfAny(DocumentAgeWords.SentenceEnds);
            i = next < 0 ? -1 : i + 1 + next;
        }
        var cueBySentence = new Dictionary<int, bool>();
        bool SentenceHasCue(int at)
        {
            var k = ends.BinarySearch(at);
            k = k < 0 ? ~k : k; // the sentence ends at ends[k], or at the text's end
            if (cueBySentence.TryGetValue(k, out var hit)) return hit;
            var from = k == 0 ? 0 : ends[k - 1] + 1;
            var sentence = text.AsSpan(from, (k < ends.Count ? ends[k] : text.Length) - from);
            hit = false;
            foreach (var c in DocumentAgeWords.SentenceCues) hit |= sentence.Contains(c, StringComparison.Ordinal);
            return cueBySentence[k] = hit;
        }

        string? basis = null;
        var mentions = 0;
        foreach (var name in fullNames)
            for (var at = text.IndexOf(name, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(name, at + name.Length, StringComparison.OrdinalIgnoreCase))
            {
                if (++mentions > DocumentAgeWords.MaxMentions) return null;
                // Any such mention blocks, whole or not: a victim, witness, child or official word, or a kinship word, before the name.
                if (WordBefore(text, at, DocumentAgeWords.Disqualifying) is not null || WordBefore(text, at, DocumentAgeWords.Kinship) is not null) return null;
                if (!IsWhole(text, at + name.Length) || WordBefore(text, at, DocumentAgeWords.Qualifying) is not { } word) continue;
                var wordAt = at - word.Length;
                if (WordBefore(text, wordAt, DocumentAgeWords.Kinship) is not null) return null; // 兒子董事陳志明
                if (basis is not null) continue;
                if (wordAt > 0 && text[wordAt - 1] == DocumentAgeWords.NegationBeforeRole) continue; // 非董事陳志明
                if (WordBefore(text, wordAt, DocumentAgeWords.NotRolePrefixes) is not null) continue; // 原被告, 準被告: another word
                if (SentenceHasCue(at)) continue;
                basis = word;
            }
        return basis;
    }

    // 12歲, 十二歲, 未滿18歲, 年僅12: any age under 18 stated anywhere. ponytail: the first 1000 ages only; a longer list is no article.
    private static bool StatesMinorAge(string text) =>
        AgeAnywhere().Matches(text).Take(DocumentAgeWords.MaxMentions)
            .Any(m => m.Groups["under"].Success || ChineseNumerals.Parse(m.Groups["n"].Value) is < DocumentAgeWords.MinorAge);

    [GeneratedRegex("(?<under>未滿)?\\s?(?<n>\\d{1,3}|[〇零一二兩三四五六七八九十廿]{1,4})\\s?(?:足|周|週)?歲|年僅\\s?(?<n>\\d{1,3}|[〇零一二兩三四五六七八九十廿]{1,4})")]
    private static partial Regex AgeAnywhere();

    private static string? WordBefore(string text, int at, string[] words)
    {
        var before = text.AsSpan(0, at);
        foreach (var w in words)
            if (before.EndsWith(w, StringComparison.Ordinal))
                return before.Length > w.Length && before[^(w.Length + 1)] == '前' ? "前" + w : w;
        return null;
    }

    private static bool IsWhole(string text, int end) =>
        end >= text.Length || !char.IsLetterOrDigit(text[end]) || DocumentAgeWords.SafeFollowers.Any(f => text.AsSpan(end).StartsWith(f, StringComparison.Ordinal));

    /// <summary>
    /// The birth dates and the age at the report date that attached news or a received page states next to the name (user,
    /// 2026-11-27): when at least one was compared and every one compared is more than
    /// <see cref="EvidenceBuilder.DobToleranceYears"/> years from the customer's DOB (an age by the births it allows,
    /// <see cref="StatedAgeCheck"/>), STATED_DOB_CONFLICT. A value within two years, or one not comparable (no report date, an
    /// age at the event, no full customer DOB for an age), never closes. Nor does it when the Profile has a DOB of its own
    /// (reaching here, that DOB agrees with the customer's, and the news may be about someone else), or when HIGH or UNIQUE
    /// support agrees (a document outranks the text). The far value must also be read next to a full name form
    /// (<see cref="StatedAgeCheck.IsFullForm"/>): a short alias may stand inside another person's name; what such an alias
    /// reads still blocks, but never concludes. Across several news items the same holds of all their values together: a
    /// compatible age or a near birth date in any item keeps the pair open.
    /// </summary>
    private static string? StatedConflict(ScreenedSubject s, PartialDate dob, SourceProfile p, IReadOnlyDictionary<EvidenceGroup, Evidence>? evidence, IReadOnlyList<NewsItem> news)
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
