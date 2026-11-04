namespace Aml.Infra.Txt;

/// <summary>Input files the case reader cannot use: which file (and table), what is wrong, and the reader's message.</summary>
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

    /// <summary>MISSING, DUPLICATE, MISSING_SECTION, MISSING_COLUMN or MALFORMED.</summary>
    public string Problem { get; }
}
