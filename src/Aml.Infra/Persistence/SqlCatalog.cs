using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Aml.Infra.Persistence;

public sealed class SqlCatalog
{
    private static readonly XNamespace Ns = "urn:aml:sql:v1";
    private readonly Dictionary<string, string> _sql;

    private SqlCatalog(Dictionary<string, string> sql) => _sql = sql;

    public string this[string id] => _sql.TryGetValue(id, out var s) ? s : throw new KeyNotFoundException($"SQL statement '{id}' is not in the catalog");

    public static SqlCatalog Load(string provider)
    {
        var asm = typeof(SqlCatalog).Assembly;
        var schemas = new XmlSchemaSet();
        using (var xsd = asm.GetManifestResourceStream("Sql.Sql.xsd") ?? throw new InvalidOperationException("Sql.xsd not embedded"))
        using (var reader = XmlReader.Create(xsd))
            schemas.Add(null, reader);

        var catalogs = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith("Sql.Sql.", StringComparison.Ordinal) && n.EndsWith(".xml", StringComparison.Ordinal))
            .ToDictionary(n => n["Sql.Sql.".Length..^".xml".Length], n => Parse(asm.GetManifestResourceStream(n)!, schemas, n));

        var reference = catalogs.Values.First().Keys.ToHashSet();
        foreach (var (name, statements) in catalogs)
            if (!reference.SetEquals(statements.Keys))
                throw new InvalidOperationException($"Sql.{name}.xml does not define the same statement ids as the other catalogs");

        return catalogs.TryGetValue(provider.ToLowerInvariant(), out var chosen)
            ? new SqlCatalog(chosen)
            : throw new InvalidOperationException($"No SQL catalog for provider '{provider}'");
    }

    private static Dictionary<string, string> Parse(Stream stream, XmlSchemaSet schemas, string name)
    {
        using (stream)
        {
            var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = schemas, DtdProcessing = DtdProcessing.Prohibit };
            settings.ValidationEventHandler += (_, e) => throw new InvalidOperationException($"{name}: {e.Message}", e.Exception);
            using var reader = XmlReader.Create(stream, settings);
            var doc = XDocument.Load(reader);

            var result = new Dictionary<string, string>();
            foreach (var module in doc.Root!.Elements(Ns + "module"))
                foreach (var st in module.Elements(Ns + "statement"))
                {
                    var id = (string)st.Attribute("id")!;
                    if (!id.StartsWith((string)module.Attribute("name")! + ".", StringComparison.Ordinal))
                        throw new InvalidOperationException($"{name}: statement '{id}' is not prefixed by its module");
                    result[id] = ((string)st.Element(Ns + "sql")!).Trim();
                }
            return result;
        }
    }
}
