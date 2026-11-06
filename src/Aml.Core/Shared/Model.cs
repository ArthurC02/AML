using System.ComponentModel;
using System.Globalization;
using Aml.Core.Normalization;

namespace Aml.Core.Shared;

public enum ValueState { Present, Missing, Ambiguous }

public enum DatePrecision { Year, Month, Day }

public enum Gender { Male, Female }

public sealed record FieldValue(string? Value, ValueState State, string? Detail = null);

/// <summary>A date known to YEAR, MONTH or DAY precision.</summary>
public readonly record struct PartialDate(
    [property: Description("年")] int Year,
    [property: Description("月；只知道年時為 null")] int? Month = null,
    [property: Description("日；只知道年或年月時為 null")] int? Day = null)
{
    [property: Description("精度：YEAR、MONTH 或 DAY")]
    public DatePrecision Precision => Day is not null ? DatePrecision.Day : Month is not null ? DatePrecision.Month : DatePrecision.Year;

    /// <summary>
    /// Parses 1973, 1972/4, 1990-07, 1992/4/10 and 民國62年6月12日; a trailing time is dropped. A 00 month or day lowers the
    /// precision. Full-width digits and separators (１９７３／６／１２) read as their half-width forms. The year must lie
    /// between 1800 and the current year and the day within its month; anything else is null, so the caller records it as
    /// UNPARSEABLE. A two-digit year is never read as a ROC year unless 民國 says so.
    /// </summary>
    public static PartialDate? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = Normalize.HalfWidth(s.Trim()).Replace('／', '/').Replace('－', '-').Split([' ', '	', 'T'], 2)[0];
        var roc = t.StartsWith("民國", StringComparison.Ordinal);
        var parts = roc ? t[2..].TrimEnd('日').Split(['年', '月'], StringSplitOptions.RemoveEmptyEntries) : t.Split('/', '-');
        if (parts.Length is 0 or > 3) return null;
        var n = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return null;

        var year = roc ? n[0] + 1911 : n[0];
        if (year < 1800 || year > DateTime.UtcNow.Year) return null;
        int? month = n.Length > 1 ? n[1] : null;
        int? day = n.Length > 2 ? n[2] : null;
        if (month == 0) return day is null or 0 ? new PartialDate(year) : null;
        if (month > 12) return null;
        if (day is null or 0) return new PartialDate(year, month);
        return day <= DateTime.DaysInMonth(year, month!.Value) ? new PartialDate(year, month, day) : null;
    }

    /// <summary>The last day the date can stand for: 1973 is read as up to 1973-12-31, 1973/6 as up to 1973-06-30.</summary>
    [property: Description("此日期可能代表的最晚一天")]
    public DateOnly LatestDay => new(Year, Month ?? 12, Day ?? DateTime.DaysInMonth(Year, Month ?? 12));

    /// <summary>True when both dates agree at the coarser of the two precisions.</summary>
    public bool AgreesWith(PartialDate other) =>
        Year == other.Year
        && (Month is null || other.Month is null || Month == other.Month)
        && (Day is null || other.Day is null || Day == other.Day);

    public override string ToString() => Precision switch
    {
        DatePrecision.Year => Year.ToString(CultureInfo.InvariantCulture),
        DatePrecision.Month => string.Create(CultureInfo.InvariantCulture, $"{Year:D4}-{Month:D2}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{Year:D4}-{Month:D2}-{Day:D2}"),
    };
}
