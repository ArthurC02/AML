namespace Aml.Core.Shared;

// An unmarked field is Public. Personal: names, dates of birth, addresses and text quoting them.
// Identifier: any field that can hold a document, registration or reference number, masked or not.
public enum Sensitivity { Public, Personal, Identifier }

[AttributeUsage(AttributeTargets.Property)]
public sealed class SensitivityAttribute(Sensitivity level) : Attribute
{
    public Sensitivity Level { get; } = level;
}
