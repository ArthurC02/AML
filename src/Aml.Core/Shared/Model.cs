using System.Globalization;

namespace Aml.Core.Shared;

public enum ValueState { Present, Missing, Ambiguous }

public enum DatePrecision { Year, Month, Day }

public enum Gender { Male, Female }

public sealed record FieldValue(string? Value, ValueState State, string? Detail = null)
{
    public static FieldValue Missing(string detail) => new(null, ValueState.Missing, detail);
}

/// <summary>A date known to YEAR, MONTH or DAY precision.</summary>
public readonly record struct PartialDate(int Year, int? Month = null, int? Day = null)
{
    public DatePrecision Precision => Day is not null ? DatePrecision.Day : Month is not null ? DatePrecision.Month : DatePrecision.Year;

    /// <summary>Parses 1973, 1972/4, 1990-07, 1992/4/10.</summary>
    public static PartialDate? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Trim().Split('/', '-');
        var n = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return null;
        return n.Length switch
        {
            1 when n[0] > 1800 => new PartialDate(n[0]),
            2 => new PartialDate(n[0], n[1]),
            3 => new PartialDate(n[0], n[1], n[2]),
            _ => null,
        };
    }

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
