using System.Text.RegularExpressions;
using Aml.Core.Normalization;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Identity;

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
                s.DateOfBirth is { } partial && news.PublishedOn is { } on && HardConflictGate.MaxAgeAt(partial, on) >= 0
                    ? $"報導年齡 {stated} 歲；缺少完整出生日期或報導日期，依報導日期 {on} 可能為 {AgeRange(partial, on)}，不比較"
                    : $"報導年齡 {stated} 歲；缺少完整出生日期或報導日期，無法換算");

        var age = HardConflictGate.MaxAgeAt(dob, at); // both dates are complete, so this is the exact age
        var note = $"報導年齡 {stated} 歲；依報導日期 {at} 推算 {age} 歲";
        return Math.Abs(stated - age) <= ToleranceYears
            ? new Evidence(EvidenceGroup.StatedAge, Direction.Neutral, ComparisonStatus.Exact, Strength.None, ReasonCodes.AgeCompatibleAtReport, note)
            : new Evidence(EvidenceGroup.StatedAge, Direction.Conflict, ComparisonStatus.Conflict, Strength.None, ReasonCodes.AgeDifferenceAtReport, note);
    }

    /// <summary>"44 歲" or "43～44 歲": every age the person can have at a possibly imprecise time point.</summary>
    public static string AgeRange(PartialDate dob, PartialDate at)
    {
        var max = HardConflictGate.MaxAgeAt(dob, at);
        var min = Math.Max(0, HardConflictGate.MinAgeAt(dob, at));
        return min == max ? $"{max} 歲" : $"{min}～{max} 歲";
    }

    // Arabic or Chinese numerals; 足歲, 周歲 and 週歲 mean 歲. A number followed by 多/餘 never reaches 歲, so ranges drop out.
    private const string AgeNumber = "(?<n>\\d{1,3}|[〇零一二兩三四五六七八九十廿卅百]{1,5})";
    private const string AgeUnit = "\\s?(?:足|周|週)?歲";
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
        foreach (var sentence in Normalize.Sentences(text))
        {
            if (Respectively(sentence, list) is { } each) return (each, false);
            foreach (var name in list)
            {
                var n = Regex.Escape(name);
                var m = Regex.Match(sentence,
                    $@"{n}\s?[（(，,]?\s?(?:今年|現年|(?<event>時年))?\s?{AgeNumber}{AgeUnit}|{NotThreshold}{AgeNumber}{AgeUnit}的?{n}"
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
        var ages = Regex.Matches(sentence[(at + 2)..].TrimStart('為', ' '), $"\\G{AgeNumber}(?:{AgeUnit})?\\s?[、，,和及]?\\s?")
            .Select(m => Normalize.ChineseNumber(m.Groups["n"].Value)).ToList();
        if (ages.Count != who.Length || ages.Any(a => a is null)) return null;
        var index = Array.FindIndex(who, w => names.Any(n => w.EndsWith(n, StringComparison.Ordinal)));
        return index >= 0 ? ages[index] : null;
    }
}
