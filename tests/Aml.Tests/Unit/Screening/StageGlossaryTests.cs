using System.Reflection;
using Aml.Core.Screening.Stage;

namespace Aml.Tests.Unit.Screening;

/// <summary>The stage glossary stays a glossary: its word lists are plain words, so they can be kept outside the code.</summary>
public class StageGlossaryTests
{
    [Fact]
    public void Every_word_list_is_plain_words_each_once()
    {
        var lists = typeof(StageGlossary).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name is not (nameof(StageGlossary.Version) or nameof(StageGlossary.CommonSurnames)))
            .Select(f => (f.Name, Words: (string)f.GetRawConstantValue()!)).ToList();
        Assert.NotEmpty(lists);
        Assert.All(lists, l =>
        {
            Assert.Matches(@"^[^|()\[\]{}?*+^$\\.]+(\|[^|()\[\]{}?*+^$\\.]+)*$", l.Words);
            var words = l.Words.Split('|');
            Assert.True(words.Distinct(StringComparer.Ordinal).Count() == words.Length, l.Name + " holds a word twice");
        });
    }
}
