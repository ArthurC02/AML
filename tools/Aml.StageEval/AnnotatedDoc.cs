using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aml.StageEval;

/// <summary>One line of a measured set: a document's text, its annotated people and events.</summary>
internal sealed record Doc([property: JsonPropertyName("doc")] string Name, string Text, List<Person> People, List<Ev> Events)
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}

internal sealed record Person(string Id, string Role, List<string> Mentions, string? Party = null);

internal sealed record Ev(string Mention, List<string> Labels);

internal static class ReferenceStage
{
    /// <summary>aml-eval-r4 <c>stage</c> mode (gold.py stage()): highest step of the person's non-prior labels, '-' labels ignored.</summary>
    public static string Of(IEnumerable<string> labels)
    {
        var l = labels.Where(x => !x.Contains('-', StringComparison.Ordinal) && x != "prior").ToHashSet();
        return l.Contains("Conviction") ? "Convicted" : l.Contains("Acquittal") ? "Acquitted" : l.Contains("NonIndictment") ? "Dismissed"
            : l.Contains("Indictment") ? "Indicted" : l.Contains("WantedNotice") ? "Wanted" : l.Count > 0 ? "Alleged" : "Unknown";
    }
}
