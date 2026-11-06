using System.ComponentModel;
using System.Globalization;
using Aml.Core.Normalization;

namespace Aml.Core.Shared;

public enum ValueState { Present, Missing, Ambiguous }

public enum DatePrecision { Year, Month, Day }

public enum Gender { Male, Female }

public sealed record FieldValue(string? Value, ValueState State, string? Detail = null);

public readonly record struct PartialDate(
    [property: Description("年")] int Year,
    [property: Description("月；只知道年時為 null")] int? Month = null,
    [property: Description("日；只知道年或年月時為 null")] int? Day = null)
{
    private const string RocEraMarker = "民國";
    private const int RocYearOffset = 1911;
    private const int EarliestYear = 1800;
    private const int LastMonth = 12;

    [property: Description("精度：YEAR、MONTH 或 DAY")]
    public DatePrecision Precision => Day is not null ? DatePrecision.Day : Month is not null ? DatePrecision.Month : DatePrecision.Year;

    /// <summary>
    /// Reads 1973, 1972/4, 1990-07, 1992/4/10, 民國62年6月12日 and full-width forms; a 00 month or day lowers the precision.
    /// Null means UNPARSEABLE. A two-digit year is never a ROC year unless 民國 says so.
    /// </summary>
    public static PartialDate? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var date = HalfWidthDateWithoutTime(s);
        var roc = date.StartsWith(RocEraMarker, StringComparison.Ordinal);
        if (!TryReadNumbers(roc ? RocDateParts(date) : date.Split('/', '-'), out var n)) return null;

        var year = roc ? n[0] + RocYearOffset : n[0];
        if (year < EarliestYear || year > DateTime.UtcNow.Year) return null;
        return FromParts(year, n.Length > 1 ? n[1] : null, n.Length > 2 ? n[2] : null);
    }

    private static string HalfWidthDateWithoutTime(string s) =>
        Normalize.HalfWidth(s.Trim()).Replace('／', '/').Replace('－', '-').Split([' ', '\t', 'T'], 2)[0];

    private static string[] RocDateParts(string date) =>
        date[RocEraMarker.Length..].TrimEnd('日').Split(['年', '月'], StringSplitOptions.RemoveEmptyEntries);

    private static bool TryReadNumbers(string[] parts, out int[] numbers)
    {
        numbers = new int[parts.Length];
        if (parts.Length is 0 or > 3) return false;
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        return true;
    }

    private static PartialDate? FromParts(int year, int? month, int? day)
    {
        if (month == 0) return day is null or 0 ? new PartialDate(year) : null;
        if (month > LastMonth) return null;
        if (day is null or 0) return new PartialDate(year, month);
        return day <= DateTime.DaysInMonth(year, month!.Value) ? new PartialDate(year, month, day) : null;
    }

    [property: Description("此日期可能代表的最晚一天")]
    public DateOnly LatestDay => new(Year, Month ?? LastMonth, Day ?? DateTime.DaysInMonth(Year, Month ?? LastMonth));

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
