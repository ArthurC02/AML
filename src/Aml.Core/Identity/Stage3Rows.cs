namespace Aml.Core.Identity;

/// <summary>Row numbers of the stage 3 decision table (identity-adjudication.md); the first matching row wins.</summary>
public static class Stage3Rows
{
    public const int Conflict = 1;
    public const int UniqueIdentifier = 2;
    public const int StrongSupport = 3;
    public const int ConsistentAtPrecision = 4;
    public const int PartialSupport = 5;
    public const int NoSupport = 6;

    public static bool IsDecidedBySupport(int? row) => row is >= UniqueIdentifier and <= PartialSupport;
}
