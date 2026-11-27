using System.Globalization;
using System.Text.RegularExpressions;
using Aml.Core.Explanation;
using Aml.Core.Normalization;
using Aml.Core.Retrieval;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Identity;

/// <summary>
/// The birth date and the gender attached news writes right next to the Profile person's name
/// (identity-adjudication.md, birth date and gender stated in a source), set against the customer's. The news is not an
/// approved field and its value is the person's only because it stands next to the name, so an agreeing birth date is
/// weaker support than the Profile's own, and a difference keeps the pair in REVIEW, unless every stated value is more than
/// two years off and the Profile has no DOB of its own (<see cref="HardConflictGate"/>, STATED_DOB_CONFLICT).
/// </summary>
public static partial class NewsStatedCheck
{
    private const int FirstGregorianYear = 1000;

    public static Evidence? CheckBirthDate(ScreenedSubject s, SourceProfile p, NewsItem? news, Evidence? profileDateOfBirth) =>
        CheckBirthDate(s, p, Items(news), profileDateOfBirth);

    private static IReadOnlyList<NewsItem> Items(NewsItem? news) => news is null ? [] : [news];

    /// <summary>The birth dates every news item of the Profile states next to the name, taken together as one item's are.</summary>
    public static Evidence? CheckBirthDate(ScreenedSubject s, SourceProfile p, IReadOnlyList<NewsItem> news, Evidence? profileDateOfBirth)
    {
        const EvidenceGroup g = EvidenceGroup.StatedBirthDate;
        var stated = StatedBirthDates(p, news);
        if (stated.Count == 0) return null;
        var written = string.Join("、", stated);
        if (s.DateOfBirth is not { } dob) return new(g, Direction.Neutral, ComparisonStatus.SubjectMissing, Strength.None, null, $"新聞所載出生日期 {written}");

        var note = $"新聞所載出生日期 {written}；客戶出生日期 {dob}";
        // The Profile's own birth date and the news tell the same fact: when the Profile's agrees, the news adds no second
        // support, and its difference is a disagreement between two sources, not with the customer.
        var profileAgrees = profileDateOfBirth?.IsSupport == true;
        var agreeing = stated.Where(dob.AgreesWith).OrderByDescending(d => d.Precision).ToList();
        // A received page's date within two years of the customer's is neutral: neither agreement nor a difference (user,
        // 2026-11-27); attached news keeps its difference. Every date further off is the difference HardConflictGate closes
        // as STATED_DOB_CONFLICT.
        if (agreeing.Count == 0)
            return profileAgrees
                ? new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, ReasonCodes.NewsBirthDateDifference, note)
                : ProfileKeys.IsWeb(p.ProfileKey) && stated.Exists(d => !dob.IsMoreThanYearsApart(d, EvidenceBuilder.DobToleranceYears))
                ? new(g, Direction.Neutral, ComparisonStatus.Partial, Strength.None, ReasonCodes.PageBirthDateWithinTolerance, note)
                : new(g, Direction.Conflict, ComparisonStatus.Conflict, Strength.None, ReasonCodes.NewsBirthDateDifference, note);
        var precision = (DatePrecision)Math.Min((int)dob.Precision, (int)agreeing[0].Precision);
        var status = precision == DatePrecision.Day ? ComparisonStatus.Exact : ComparisonStatus.Partial;
        // Scattered dates next to one name are several people written the same way (masked names): agreeing with one of
        // them is not support, as with a Profile's scattered birth dates.
        if (EvidenceBuilder.AreScattered(stated))
            return new(g, Direction.Neutral, status, Strength.None, ReasonCodes.NewsBirthDateAmbiguous, note);
        return profileAgrees
            ? new(g, Direction.Neutral, status, Strength.None, ReasonCodes.NewsBirthDateAgrees, note)
            : new(g, Direction.Support, status, precision == DatePrecision.Day ? Strength.Medium : Strength.Low, ReasonCodes.NewsBirthDateAgrees, note);
    }

    /// <summary>
    /// The distinct birth dates written next to the Profile person's names, without a coarser one a more precise one covers;
    /// with <paramref name="fullFormsOnly"/>, next to a full name form only (<see cref="StatedAgeCheck.IsFullForm"/>).
    /// </summary>
    internal static List<PartialDate> StatedBirthDates(SourceProfile p, IReadOnlyList<NewsItem> news, bool fullFormsOnly = false)
    {
        var read = Read(p, news, NewsIdentifierKind.BirthDate, fullFormsOnly).Select(Parse).OfType<PartialDate>().Distinct().ToList();
        return read.Where(d => !read.Exists(x => x.Precision > d.Precision && d.AgreesWith(x))).ToList();
    }

    public static Evidence? CheckGender(ScreenedSubject s, SourceProfile p, NewsItem? news) => CheckGender(s, p, Items(news));

    /// <summary>The genders every news item of the Profile states next to the name, taken together: one gender or no reading.</summary>
    public static Evidence? CheckGender(ScreenedSubject s, SourceProfile p, IReadOnlyList<NewsItem> news)
    {
        const EvidenceGroup g = EvidenceGroup.StatedGender;
        if (s.Gender is not { } gender) return null;
        var stated = Read(p, news, NewsIdentifierKind.Gender).Select(t => t[0]).Where(c => c is '男' or '女').Distinct().ToList();
        if (stated.Count != 1) return null; // none, or both next to one written name: no reading
        var note = $"新聞所載性別 {stated[0]}";
        if ((stated[0] == '男' ? Gender.Male : Gender.Female) == gender)
            return new(g, Direction.Neutral, ComparisonStatus.Exact, Strength.None, ReasonCodes.NewsGenderAgrees, note);
        return p.Gender == gender // the Profile's own gender agrees: two sources disagree, not the customer
            ? new(g, Direction.Neutral, ComparisonStatus.Uncertain, Strength.None, ReasonCodes.NewsGenderDifference, note)
            : new(g, Direction.Conflict, ComparisonStatus.Conflict, Strength.None, ReasonCodes.NewsGenderDifference, note);
    }

    private static IEnumerable<string> Read(SourceProfile p, IReadOnlyList<NewsItem> news, NewsIdentifierKind kind, bool fullFormsOnly = false) =>
        news.SelectMany(n => NewsIdentifiers.Read(n.Text, fullFormsOnly ? p.Names.Where(StatedAgeCheck.IsFullForm) : p.Names, wholeWords: fullFormsOnly))
            .Where(i => i.Kind == kind && !string.IsNullOrEmpty(i.Text)).Select(i => i.Text!);

    /// <summary>民國70年1月1日生, 70年1月生, 1981年1月1日出生, 70年次: a year under 1000 is a ROC year, as news and judgments in Taiwan write it.</summary>
    public static PartialDate? Parse(string text)
    {
        var m = BirthText().Match(CjkText.HalfWidth(text));
        if (!m.Success) return null;
        var parts = new[] { m.Groups["y"], m.Groups["m"], m.Groups["d"] }.Where(x => x.Success).Select(x => x.Value).ToArray();
        return int.Parse(parts[0], CultureInfo.InvariantCulture) < FirstGregorianYear
            ? PartialDate.Parse($"民國{parts[0]}年{string.Join("月", parts[1..])}")
            : PartialDate.Parse(string.Join("/", parts));
    }

    // [0-9], not \d: \d also takes digits of other scripts, which int.Parse refuses.
    [GeneratedRegex(@"^(?:民國)?\s?(?<y>[0-9]{2,4})\s?年(?:次$|(?:\s?(?<m>[0-9]{1,2})\s?月(?:\s?(?<d>[0-9]{1,2})\s?日)?)?\s?出?生$)")]
    private static partial Regex BirthText();
}
