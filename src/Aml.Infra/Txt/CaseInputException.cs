namespace Aml.Infra.Txt;

public sealed class CaseInputException : Exception
{
    public const string Missing = "MISSING";
    public const string Duplicate = "DUPLICATE";
    public const string MissingSection = "MISSING_SECTION";
    public const string MissingColumn = "MISSING_COLUMN";
    public const string Malformed = "MALFORMED";

    public CaseInputException(string file, string? table, string problem, string message, Exception? inner = null) : base(message, inner) =>
        (File, Table, Problem) = (file, table, problem);

    public CaseInputException() : this("", null, Malformed, "Unreadable input") { }

    public CaseInputException(string message) : this("", null, Malformed, message) { }

    public CaseInputException(string message, Exception inner) : this("", null, Malformed, message, inner) { }

    public string File { get; }
    public string? Table { get; }

    public string Problem { get; }

    /// <summary>Runs a read of <paramref name="file"/>, turning the failures a layout mismatch causes into a MALFORMED input problem.</summary>
    internal static T ReadOrMalformed<T>(string file, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is IndexOutOfRangeException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or IOException)
        {
            throw new CaseInputException(file, null, Malformed, $"{file} is not in the export layout.", e);
        }
    }
}
