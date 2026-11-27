using System.Text.RegularExpressions;
using Aml.Core.Explanation;
using Aml.Core.Normalization;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Identity;

/// <summary>
/// Age explicitly stated for the Profile person in attached news (identity-adjudication.md, age stated in a source).
/// Compared at the report date by the births the age allows. Within two years of the customer's DOB is compatible, never
/// support; beyond is a conflict, which closes the pair as STATED_DOB_CONFLICT when no other stated value and no Profile DOB
/// says otherwise (<see cref="HardConflictGate"/>).
/// </summary>
public static partial class StatedAgeCheck
{
    /// <param name="fullFormsOnly">Read only next to a <see cref="IsFullForm"/> name, a Latin one as a whole word: what a
    /// stated value must be read next to before it may conclude (HardConflictGate).</param>
    /// <summary>
    /// Several news items: each is read at its own report date, then one reading stands for them all, as within one item
    /// the first age found does: a compatible age when any item gives one (the pair stays open, a far age elsewhere is in
    /// the note), else a difference when any item gives one, else not comparable. Every item's note is kept.
    /// </summary>
    public static Evidence? Check(ScreenedSubject s, SourceProfile p, IReadOnlyList<NewsItem> news, bool fullFormsOnly = false)
    {
        var read = news.Select(n => Check(s, p, n, fullFormsOnly)).OfType<Evidence>().ToList();
        if (read.Count <= 1) return read.FirstOrDefault();
        var stands = read.FirstOrDefault(e => e.Status == ComparisonStatus.Exact) ?? read.FirstOrDefault(e => e.Status == ComparisonStatus.Conflict) ?? read[0];
        return stands with { Note = string.Join("；", read.Select(e => e.Note).OfType<string>()) };
    }

    public static Evidence? Check(ScreenedSubject s, SourceProfile p, NewsItem? news, bool fullFormsOnly = false)
    {
        if (news is null || Find(news.Text, p.Names, fullFormsOnly) is not var (stated, statedAtEventTime))
            return null;
        if (statedAtEventTime)
            return NotComparable($"報導年齡 {stated} 歲（時年，事件當時）；事件日期未知，不與報導日期比較");
        if (s.DateOfBirth is not { Precision: DatePrecision.Day } dob || news.PublishedOn is not { Precision: DatePrecision.Day } at)
            return NotComparable(s.DateOfBirth is { } partial && news.PublishedOn is { } on && HardConflictGate.MaxAgeAt(partial, on) >= 0
                ? $"報導年齡 {stated} 歲；缺少完整出生日期或報導日期，依報導日期 {on} 可能為 {AgeRange(partial, on)}，不比較"
                : $"報導年齡 {stated} 歲；缺少完整出生日期或報導日期，無法換算");

        var exactAge = HardConflictGate.MaxAgeAt(dob, at);
        var note = $"報導年齡 {stated} 歲；依報導日期 {at} 推算 {exactAge} 歲";
        // The two-year rule of a birth date, on the births the age allows. Away from 29 February that is a ±2 age difference;
        // at it the two differ by a day.
        return !BirthsMoreThanYearsFrom(dob, stated, at, EvidenceBuilder.DobToleranceYears)
            ? new Evidence(EvidenceGroup.StatedAge, Direction.Neutral, ComparisonStatus.Exact, Strength.None, ReasonCodes.AgeCompatibleAtReport, note)
            : new Evidence(EvidenceGroup.StatedAge, Direction.Conflict, ComparisonStatus.Conflict, Strength.None, ReasonCodes.AgeDifferenceAtReport, note);
    }

    /// <summary>
    /// Every birth an age stated at a date allows (from the day after the date age + 1 years before to the date age years
    /// before) more than <paramref name="years"/> calendar years from the DOB, as <see cref="PartialDate.IsMoreThanYearsApart"/>
    /// counts years. An age reaching before year 1 is a misreading, never a conflict.
    /// </summary>
    private static bool BirthsMoreThanYearsFrom(PartialDate dob, int age, PartialDate at, int years)
    {
        var (on, born) = (at.LatestDay, dob.LatestDay);
        if (age + 1 >= on.Year) return false;
        var (earliest, latest) = (on.AddYears(-(age + 1)).AddDays(1), on.AddYears(-age));
        return born > latest.AddYears(years) || earliest > born.AddYears(years);
    }

    private static Evidence NotComparable(string note) =>
        new(EvidenceGroup.StatedAge, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, ReasonCodes.AgeNotComparable, note);

    /// <summary>"44 歲" or "43～44 歲": every age the person can have at a possibly imprecise time point.</summary>
    public static string AgeRange(PartialDate dob, PartialDate at)
    {
        var max = HardConflictGate.MaxAgeAt(dob, at);
        var min = Math.Max(0, HardConflictGate.MinAgeAt(dob, at));
        return min == max ? $"{max} 歲" : $"{min}～{max} 歲";
    }

    // 足歲, 周歲 and 週歲 mean 歲; 40多歲 never matches, so approximate ages drop out.
    private const string AgeNumber = "(?<n>\\d{1,3}|[〇零一二兩三四五六七八九十廿卅百]{1,5})";
    private const string AgeUnit = "\\s?(?:足|周|週)?歲";
    private const string NotThresholdOrApproximation = "(?<![\\d〇零一二兩三四五六七八九十廿卅百]|滿|約|逾|近|超過|未滿|年滿)";
    private const string Respectively = "分別";

    // 張文華（男，45歲）: a gender may stand before the age in the bracket after the name.
    private const string GenderFirst = "(?:[男女][，,、]\\s?)?";

    private const int MaxNames = 100;

    /// <summary>
    /// "張文華（45歲）", "張文華今年四十五歲", "45歲的張文華", "Chang Wen-Hua, aged 45", "45-year-old Chang Wen-Hua",
    /// "甲、乙分別為 44 歲、45 歲" (one age per person, in order); within one sentence and adjacent to the name.
    /// </summary>
    public static int? FindStatedAge(string text, IEnumerable<string> names) => Find(text, names)?.Age;

    /// <summary>
    /// A name form a stated value may conclude by: a CJK name of three characters or more (a shorter one may sit inside
    /// another person's name, 文華 in 張文華) that names somebody, or any other name, matched as a whole word. An anonymised
    /// form (林姓男子, 歐陽男) or a masked one (歐陽○明, 王某某) fits anyone of that surname and is never full.
    /// </summary>
    internal static bool IsFullForm(string name) =>
        !IsCjkName(name) || (name.Count(CjkText.IsCjk) >= MinFullCjkForm && !NamesNobody(name));

    // 姓 marks an anonymised form only right after the surname (林姓男子, 歐陽姓主嫌, whatever noun follows); elsewhere it
    // is read as a given-name character, so it cannot swallow a real name by position alone.
    private static bool NamesNobody(string name) =>
        CjkText.IsMasked(name) || NewsDigests.AnonymisedSurname(name) is not null
        || name.IndexOf('姓', StringComparison.Ordinal) == NewsDigests.Surname(name).Length;

    /// <summary>A name written with any CJK character: matched where it appears, since CJK text has no word boundaries.</summary>
    internal static bool IsCjkName(string name) => name.Any(CjkText.IsCjk);

    private const int MinFullCjkForm = 3;

    /// <summary>The name stands alone in a Latin text: no letter, digit, hyphen or apostrophe next to it on either side.</summary>
    private const string WordBefore = @"(?<![\p{L}\p{M}\p{Nd}'’-])", WordAfter = @"(?![\p{L}\p{M}\p{Nd}'’-])";

    private static (int Age, bool StatedAtEventTime)? Find(string text, IEnumerable<string> names, bool fullFormsOnly = false)
    {
        // ponytail: the first 100 names only. Each name is a regex tried on every sentence that might hold it; a Profile of
        // thousands of aliases against a long article took minutes. Raise the cap if a real Profile needs more.
        var list = names.Where(n => n.Length > 0 && (!fullFormsOnly || IsFullForm(n))).Take(MaxNames).ToList();
        var matchers = list.Select(n => (Name: n, Regex: new Lazy<Regex>(() => AgeNextToName(n, fullFormsOnly && !IsCjkName(n)), isThreadSafe: false))).ToArray();
        foreach (var sentence in CjkText.Sentences(text))
        {
            if (AgeFromRespectivelyList(sentence, list) is { } each) return (each, false);
            if (!AgeMarker().IsMatch(sentence)) continue;
            foreach (var (name, regex) in matchers)
                if (CanAppearIn(sentence, name) && StatedAge(regex.Value.Match(sentence)) is { } found) return found;
        }
        return null;
    }

    // Every form AgeNextToName matches holds one of these, so a sentence without any states no age.
    [GeneratedRegex("歲|aged|year-old|\\(", RegexOptions.IgnoreCase)]
    private static partial Regex AgeMarker();

    // Every form holds the name, matched case-insensitively. A name of Chinese ideographs and uncased non-letters matches
    // only where it appears verbatim; any other name may match under case equivalences an ordinal search misses (İ, ß),
    // so it is always tried.
    private static bool CanAppearIn(string sentence, string name) =>
        !name.All(HasNoCaseEquivalent) || sentence.Contains(name, StringComparison.Ordinal);

    private static bool HasNoCaseEquivalent(char c) =>
        CjkText.IsCjk(c) || (!char.IsLetter(c) && char.ToUpperInvariant(c) == c && char.ToLowerInvariant(c) == c);

    private static Regex AgeNextToName(string name, bool asWord)
    {
        var n = asWord ? $"{WordBefore}{Regex.Escape(name)}{WordAfter}" : Regex.Escape(name);
        return new Regex(
            $@"{n}\s?[（(，,]?\s?{GenderFirst}(?:今年|現年|(?<event>時年))?\s?{AgeNumber}{AgeUnit}|(?:(?<event>時年)\s?)?{NotThresholdOrApproximation}{AgeNumber}{AgeUnit}的?{n}"
            + $@"|{n},?\s+aged\s+(?<n>\d{{1,3}})\b|\b(?<n>\d{{1,3}})-year-old\s+{n}|{n}\s?\((?<n>\d{{1,3}})\)",
            RegexOptions.IgnoreCase);
    }

    // 時年 states the age at the reported event, not at the report date, after the name or before the age that precedes it.
    private static (int Age, bool StatedAtEventTime)? StatedAge(Match m) =>
        m.Success && ChineseNumerals.Parse(m.Groups["n"].Value) is { } age ? (age, m.Groups["event"].Success) : null;

    // "甲、乙分別為44歲、45歲": the names before 分別 pair up in order with the ages after it.
    private static int? AgeFromRespectivelyList(string sentence, IReadOnlyList<string> names)
    {
        var at = sentence.IndexOf(Respectively, StringComparison.Ordinal);
        if (at <= 0) return null;
        var who = Regex.Match(sentence[..at], "[\\p{IsCJKUnifiedIdeographs}、和及與]+$").Value.Split('、', '和', '及', '與');
        var ages = Regex.Matches(sentence[(at + Respectively.Length)..].TrimStart('為', ' '), $"\\G{AgeNumber}(?:{AgeUnit})?\\s?[、，,和及]?\\s?")
            .Select(m => ChineseNumerals.Parse(m.Groups["n"].Value)).ToList();
        if (ages.Count != who.Length || ages.Any(a => a is null)) return null;
        var index = Array.FindIndex(who, w => names.Any(n => w.EndsWith(n, StringComparison.Ordinal)));
        return index >= 0 ? ages[index] : null;
    }
}
