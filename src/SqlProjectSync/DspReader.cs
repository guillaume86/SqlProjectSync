using System.Xml;
using System.Xml.Linq;

namespace SqlProjectSync;

internal static class DspReader
{
    private const string DefaultDsp = "Microsoft.Data.Tools.Schema.Sql.Sql160DatabaseSchemaProvider";

    public static string ReadFromProject(string sqlprojPath)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(sqlprojPath);
        }
        catch (XmlException ex)
        {
            throw new SchemaSyncException($"Failed to parse '{sqlprojPath}' as XML.", ex);
        }
        catch (IOException ex)
        {
            throw new SchemaSyncException($"Failed to read '{sqlprojPath}'.", ex);
        }

        var root = doc.Root
            ?? throw new SchemaSyncException($"'{sqlprojPath}' has no root element.");

        var dsp = FindElement(root, "DSP")?.Value.Trim();
        if (!string.IsNullOrEmpty(dsp))
        {
            return dsp;
        }

        var version = FindElement(root, "SqlServerVersion")?.Value.Trim();
        if (!string.IsNullOrEmpty(version))
        {
            return $"Microsoft.Data.Tools.Schema.Sql.{version}DatabaseSchemaProvider";
        }

        return DefaultDsp;
    }

    private static XElement? FindElement(XElement root, string localName)
    {
        return root.Descendants()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal));
    }
}
