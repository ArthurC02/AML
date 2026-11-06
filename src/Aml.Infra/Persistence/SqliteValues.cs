using System.Globalization;

namespace Aml.Infra.Persistence;

/// <summary>Times as SQLite stores them: ISO 8601 text in UTC.</summary>
internal static class SqliteValues
{
    public static string Time(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTime(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
