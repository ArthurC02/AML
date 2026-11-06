using System.Text.RegularExpressions;
using Aml.Core.Normalization;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Identity;

/// <summary>
/// Age explicitly stated for the Profile person in attached news (identity-adjudication.md, age stated in a source).
/// Compared at the report date. Within ±2 years is compatible, never support; beyond is a soft conflict, never hard.
/// </summary>
public static partial class StatedAgeCheck
{
    public const int ToleranceYears = 2;

    public static Evidence? Check(ScreenedSubject s, SourceProfile p, NewsItem? news)
    {
        if (news is null || Find(news.Text, p.Names) is not var (stated, statedAtEventTime))
            return null;
        if (statedAtEventTime)
            return NotComparable($"報導年齡 {stated} 歲（時年，事件當時）；事件日期未知，不與報導日期比較");
        if (s.DateOfBirth is not { Precision: DatePrecision.Day } dob || news.PublishedOn is not { Precision: DatePrecision.Day } at)
            return NotComparable(s.DateOfBirth is { } partial && news.PublishedOn is { } on && HardConflictGate.MaxAgeAt(partial, on) >= 0
                ? $"報導年齡 {stated} 歲；缺少完整出生日期或報導日期，依報導日期 {on} 可能為 {AgeRange(partial, on)}，不比較"
                : $"報導年齡 {stated} 歲；缺少完整出生日期或報導日期，無法換算");

        var exactAge = HardConflictGate.MaxAgeAt(dob, at);
        var note = $"報導年齡 {stated} 歲；依報導日期 {at} 推算 {exactAge} 歲";
        return Math.Abs(stated - exactAge) <= ToleranceYears
            ? new Evidence(EvidenceGroup.StatedAge, Direction.Neutral, ComparisonStatus.Exact, Strength.None, ReasonCodes.AgeCompatibleAtReport, note)
            : new Evidence(EvidenceGroup.StatedAge, Direction.Conflict, ComparisonStatus.Conflict, Strength.None, ReasonCodes.AgeDifferenceAtReport, note);
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

    private const int MaxNames = 100;

    /// <summary>
    /// "陳志明（45歲）", "陳志明今年四十五歲", "45歲的陳志明", "Chen Chih-Ming, aged 45", "45-year-old Chen Chih-Ming",
    /// "甲、乙分別為 44 歲、45 歲" (one age per person, in order); within one sentence and adjacent to the name.
    /// </summary>
    public static int? FindStatedAge(string text, IEnumerable<string> names) => Find(text, names)?.Age;

    private static (int Age, bool StatedAtEventTime)? Find(string text, IEnumerable<string> names)
    {
        // ponytail: the first 100 names only. Each name is a regex tried on every sentence that might hold it; a Profile of
        // thousands of aliases against a long article took minutes. Raise the cap if a real Profile needs more.
        var list = names.Where(n => n.Length > 0).Take(MaxNames).ToList();
        var matchers = list.Select(n => (Name: n, Regex: new Lazy<Regex>(() => AgeNextToName(n), isThreadSafe: false))).ToArray();
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

    private static Regex AgeNextToName(string name)
    {
        var n = Regex.Escape(name);
        return new Regex(
            $@"{n}\s?[（(，,]?\s?(?:今年|現年|(?<event>時年))?\s?{AgeNumber}{AgeUnit}|{NotThresholdOrApproximation}{AgeNumber}{AgeUnit}的?{n}"
            + $@"|{n},?\s+aged\s+(?<n>\d{{1,3}})\b|\b(?<n>\d{{1,3}})-year-old\s+{n}|{n}\s?\((?<n>\d{{1,3}})\)",
            RegexOptions.IgnoreCase);
    }

    // 時年 states the age at the reported event, not at the report date.
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
