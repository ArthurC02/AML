using System.Text;

namespace Aml.Core.Normalization;

internal static class EmbeddedTables
{
    internal static IEnumerable<string> Lines(string file)
    {
        using var stream = typeof(EmbeddedTables).Assembly.GetManifestResourceStream(file) ?? throw new InvalidOperationException(file);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0 && line[0] != '#') yield return line;
    }
}
