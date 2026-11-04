namespace Aml.Core.Shared;

/// <summary>Data classification of a field, published as x-sensitivity in the JSON Schemas. An unmarked field is public.</summary>
public enum Sensitivity { Public, Personal, Identifier }

/// <summary>
/// Personal: names, dates of birth, addresses and text quoting them. Identifier: document, registration and reference
/// numbers, masked or not, and any field that can hold one. Summary views leave Identifier fields out.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SensitivityAttribute(Sensitivity level) : Attribute
{
    public Sensitivity Level { get; } = level;
}
