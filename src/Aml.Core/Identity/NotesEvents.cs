using System.Globalization;
using System.Text.RegularExpressions;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Identity;

/// <summary>
/// Dated events of the Profile person read from the vendor's English notes, for the advisory implausible age
/// (identity-adjudication.md#implausible-age-at-a-dated-document-advisory; extraction-and-normalization.md#profile-notes).
/// Only the narrative parts are read (<see cref="NotesEventWords.ReadLabels"/>), sentence by sentence. A sentence gives
/// events when it refers to the Profile person (<see cref="RefersToPerson"/>), names no relative, associate or company
/// (<see cref="NotesEventWords.OtherPersons"/>) and no other party before its act word
/// (<see cref="NotesEventWords.OtherSubjects"/>), is no review, negation, hypothesis or birth, names no law or event by year,
/// and holds an act word (<see cref="NotesEventWords.Acts"/>): each date written in a date context is then an event
/// (<see cref="DateMention"/>). Birth dates and stated ages are read over the whole notes. When unsure, a sentence is
/// skipped. Silent (null) over <see cref="NotesEventWords.MaxDates"/> dates or on a pattern timeout.
/// </summary>
public static partial class NotesEvents
{
    /// <summary>An event: its date at the precision written, the act word read, and the sentence's place in the notes.</summary>
    public sealed record Event(PartialDate On, string Act, string Location);

    /// <summary>What the notes say of the person's time line: the events, the birth dates and whether an age is stated anywhere.</summary>
    public sealed record Reading(IReadOnlyList<Event> Events, IReadOnlyList<PartialDate> BirthDates, bool StatesAge);

    public static Reading? Read(ProfileNotes notes, IReadOnlyList<string> latinNames)
    {
        var events = new List<Event>();
        var births = new List<PartialDate>();
        var dates = 0;
        try
        {
            // The guards, over the whole notes whatever the part or subject: they can only silence the hint.
            var statesAge = StatedAge().IsMatch(notes.Raw);
            foreach (var sentence in notes.Parts.SelectMany(x => Sentences(x.Text)).Where(x => HasAny(x, NotesEventWords.Births)))
                foreach (Match m in AnyDate().Matches(sentence))
                {
                    if (++dates > NotesEventWords.MaxDates) return null;
                    births.AddRange(ToDates(m));
                }

            dates = 0;
            foreach (var part in notes.Parts.Where(x => NotesEventWords.ReadLabels.Contains(x.Label)))
            {
                var n = 0;
                foreach (var sentence in Sentences(part.Text))
                {
                    n++;
                    if (!RefersToPerson(sentence, latinNames) || HasAny(sentence, NotesEventWords.OtherPersons) || HasAny(sentence, NotesEventWords.Reviews)
                        || HasAny(sentence, NotesEventWords.Negations) || HasAny(sentence, NotesEventWords.Hypotheses) || HasAny(sentence, NotesEventWords.Births)
                        || NamedByYear().IsMatch(sentence)) continue;
                    if (FirstAt(sentence, NotesEventWords.Acts) is not { } act) continue;
                    // Another party standing before the act word may be its subject (a victim said he was defrauded).
                    if (FirstAt(sentence, NotesEventWords.OtherSubjects) is { } other && other.At < act.At) continue;
                    foreach (Match m in DateMention().Matches(sentence))
                    {
                        if (++dates > NotesEventWords.MaxDates) return null;
                        if (NotADate().IsMatch(sentence.AsSpan(m.Index + m.Length))) continue;
                        foreach (var on in ToDates(m)) events.Add(new(on, act.Word, $"{notes.Location} {part.Label ?? "附註"} 第 {n} 句"));
                    }
                }
            }
            return new(events, births, statesAge);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// The sentence refers to the Profile person: a whole Latin name form (any case), a token of one of at least three
    /// letters written capitalised, or he / she. A two-letter token (An, Li) alone never identifies the person.
    /// </summary>
    internal static bool RefersToPerson(string sentence, IReadOnlyList<string> latinNames)
    {
        if (HasAny(sentence, [.. latinNames]) || HasAny(sentence, NotesEventWords.Pronouns)) return true;
        var tokens = latinNames.SelectMany(x => x.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(t => t.Count(char.IsLetter) >= 3);
        return tokens.Any(t => Whole(sentence, t, StringComparison.OrdinalIgnoreCase).Any(at => char.IsUpper(sentence[at])));
    }

    // Sentence ends: ; and a line break always; . ! ? when whitespace and a capital, a quote or a bracket follow
    // (so "Sample Co. was" stays one sentence, "Co. Ltd" is split, which can only drop a sentence's person reference).
    private static IEnumerable<string> Sentences(string text)
    {
        var from = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var end = c is ';' or '\n'
                || (c is '.' or '!' or '?' && i + 2 < text.Length && char.IsWhiteSpace(text[i + 1]) && (char.IsUpper(text[i + 2]) || text[i + 2] is '"' or '('));
            if (!end) continue;
            if (i > from) yield return text[from..i];
            from = i + 1;
        }
        if (from < text.Length) yield return text[from..];
    }

    private static bool HasAny(string sentence, string[] words) => FirstAt(sentence, words) is not null;

    // The earliest place a listed word or phrase stands whole (no letter or digit on either side), ignoring case.
    private static (string Word, int At)? FirstAt(string sentence, string[] words)
    {
        (string Word, int At)? first = null;
        foreach (var w in words)
        {
            var at = Whole(sentence, w, StringComparison.OrdinalIgnoreCase).FirstOrDefault(-1);
            if (at >= 0 && (first is null || at < first.Value.At)) first = (w, at);
        }
        return first;
    }

    private static IEnumerable<int> Whole(string s, string w, StringComparison comparison)
    {
        for (var at = s.IndexOf(w, comparison); at >= 0; at = s.IndexOf(w, at + 1, comparison))
        {
            var end = at + w.Length;
            if ((at == 0 || !char.IsLetterOrDigit(s[at - 1])) && (end == s.Length || !char.IsLetterOrDigit(s[end]) || !char.IsLetterOrDigit(w[^1]))) yield return at;
        }
    }

    private static IEnumerable<PartialDate> ToDates(Match m)
    {
        int? month = m.Groups["m"].Success ? Array.IndexOf(Months, m.Groups["m"].Value[..3].ToUpperInvariant()) + 1
            : m.Groups["n"].Success ? int.Parse(m.Groups["n"].ValueSpan, provider: CultureInfo.InvariantCulture) : null;
        int? day = m.Groups["d"].Success ? int.Parse(m.Groups["d"].ValueSpan, provider: CultureInfo.InvariantCulture) : null;
        // The date parser checks the calendar and refuses a year after the current one (a future date is no event).
        foreach (var g in YearGroups.Where(g => m.Groups[g].Success))
        {
            var year = m.Groups[g].Value;
            var text = g == "y2" || month is null ? year : day is null ? $"{year}-{month}" : $"{year}-{month}-{day}";
            if (PartialDate.Parse(text) is { } on) yield return on;
        }
    }

    private static readonly string[] YearGroups = ["y", "y2"];

    private static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private const string Month = "(?<m>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\\.?";
    private const string Year = "(?<y>(?:19|20)\\d{2})";
    private const string Year2 = "(?<y2>(?:19|20)\\d{2})";
    private const string FullDates = "(?<d>\\d{1,2})(?:st|nd|rd|th)?[ -]" + Month + "[ ,-]{1,3}" + Year + "|" + Month + " (?<d>\\d{1,2})(?:st|nd|rd|th)?,? " + Year
        + "|" + Month + ",? " + Year + "|" + Year + "[-/](?<n>\\d{1,2})[-/](?<d>\\d{1,2})";

    /// <summary>
    /// A date in a date context, the only way a number becomes a year: a full date (12 March 2005, 12-Mar-2005, March 12,
    /// 2005, 2005-03-12), a month and year (March 2005), a span (between 2003 and 2006, from 2003 to 2006), or a year right
    /// after in, since, during, by, until, till, before, after, around, early, mid or late. A bare number (USD 2000, Case
    /// 2005-123, Docket 2005/123) is no date; <see cref="NotADate"/> then drops a context year a unit or a number follows.
    /// </summary>
    [GeneratedRegex("\\b(?:" + FullDates + "|between " + Year + " and " + Year2 + "|from " + Year + " (?:to|until|through|till) " + Year2
        + "|(?:(?:early|mid|late)[- ]|(?:in|since|during|by|until|till|before|after|around) (?:the )?(?:year )?)" + Year + ")\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, NotesEventWords.MatchTimeoutMs)]
    private static partial Regex DateMention();

    // Right after a context year: a separator and a digit (2005,000; 2005-123; 2005/123), a currency or a unit (in 2000 shares).
    [GeneratedRegex("^(?:[,./-]\\d|\\s*(?:%|\\$|USD|NTD|TWD|NT\\$|RMB|HKD|EUR|dollars?|yuan|euros?|people|persons|shares|units|kg|tons?|items|times|million|billion|thousand)\\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, NotesEventWords.MatchTimeoutMs)]
    private static partial Regex NotADate();

    // In a birth sentence any written date counts, a bare year too (DOB: 1980, born 1980).
    [GeneratedRegex("\\b(?:" + FullDates + "|" + Year + ")\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, NotesEventWords.MatchTimeoutMs)]
    private static partial Regex AnyDate();

    // aged 45, age 45, Age: 45, 45-year-old, 45 years old.
    [GeneratedRegex("\\baged?:? ?\\d{1,3}\\b|\\b\\d{1,3}[- ]years?[- ]old\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, NotesEventWords.MatchTimeoutMs)]
    private static partial Regex StatedAge();

    // A law or an event named by its year: the 2001 Securities and Exchange Act, the Money Laundering Control Act of 2016, the 2008 Olympics, the 2004 election.
    [GeneratedRegex("\\b(?:19|20)\\d{2} (?:(?:[A-Z][A-Za-z&-]*|and|of|on|the) ){0,5}(?:Act|Law|Ordinance|Code|Regulations?|Amendments?|Convention|Directive|Olympics|Games|Cup|Expo|[Ee]lections?|[Cc]ensus)\\b"
        + "|\\b(?:Act|Law|Ordinance|Code) of (?:19|20)\\d{2}\\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, NotesEventWords.MatchTimeoutMs)]
    private static partial Regex NamedByYear();
}
