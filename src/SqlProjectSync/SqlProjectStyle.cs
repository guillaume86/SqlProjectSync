using System.Xml;
using System.Xml.Linq;

namespace SqlProjectSync;

/// <summary>The build style of a <c>.sqlproj</c> file.</summary>
public enum SqlProjectStyle
{
    /// <summary>Modern <see href="https://github.com/microsoft/DacFx/tree/main/src/Microsoft.Build.Sql">Microsoft.Build.Sql</see> SDK-style project.</summary>
    Sdk,

    /// <summary>Legacy SSDT <c>.sqlproj</c> with imports of <c>Microsoft.Data.Tools.Schema.SqlTasks.targets</c>.</summary>
    Legacy,
}

/// <summary>Helpers for working with <see cref="SqlProjectStyle"/>.</summary>
public static class SqlProjectStyleExtensions
{
    private const string MicrosoftBuildSql = "Microsoft.Build.Sql";

    /// <summary>Detects whether <paramref name="sqlprojPath"/> is an SDK-style or legacy <c>.sqlproj</c>.</summary>
    public static SqlProjectStyle Detect(string sqlprojPath)
    {
        if (string.IsNullOrWhiteSpace(sqlprojPath))
        {
            throw new ArgumentException("Project path is required.", nameof(sqlprojPath));
        }

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

        if (!string.Equals(root.Name.LocalName, "Project", StringComparison.Ordinal))
        {
            throw new SchemaSyncException(
                $"'{sqlprojPath}' has root element '{root.Name.LocalName}', expected 'Project'.");
        }

        var sdkAttr = root.Attribute("Sdk")?.Value;
        if (sdkAttr is not null && sdkAttr.StartsWith(MicrosoftBuildSql, StringComparison.OrdinalIgnoreCase))
        {
            return SqlProjectStyle.Sdk;
        }

        foreach (var sdkChild in root.Elements().Where(e => string.Equals(e.Name.LocalName, "Sdk", StringComparison.Ordinal)))
        {
            var name = sdkChild.Attribute("Name")?.Value;
            if (name is not null && name.StartsWith(MicrosoftBuildSql, StringComparison.OrdinalIgnoreCase))
            {
                return SqlProjectStyle.Sdk;
            }
        }

        return SqlProjectStyle.Legacy;
    }
}
