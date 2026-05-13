using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace SqlProjectSync;

/// <summary>
/// Resolves the set of <c>.sql</c> scripts that DacFx should treat as the build
/// inputs of a <c>.sqlproj</c>. Replaces a naive
/// <c>Directory.GetFiles(projectDir, "*.sql", AllDirectories)</c> walk, which
/// over-includes generated outputs under <c>bin\</c>/<c>obj\</c>, pre/post-deploy
/// scripts, and <c>&lt;None&gt;</c>-tagged data files. Including those causes
/// <see cref="Microsoft.SqlServer.Dac.Compare.SchemaComparisonResult.PublishChangesToProject(string, Microsoft.SqlServer.Dac.DacExtractTarget)"/>
/// to either spend minutes parsing duplicates or fail outright with an internal
/// <see cref="ArgumentNullException"/>.
/// </summary>
internal static partial class ProjectScriptCollector
{
    private static readonly HashSet<string> SdkNonBuildItemNames = new(StringComparer.Ordinal)
    {
        "None",
        "Content",
        "PreDeploy",
        "PostDeploy",
        "NotInBuild",
        "RefactorLog",
    };

    public static string[] Collect(string sqlprojPath, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(sqlprojPath))
        {
            throw new ArgumentException("Project path is required.", nameof(sqlprojPath));
        }

        var projectDir = Path.GetDirectoryName(Path.GetFullPath(sqlprojPath))
            ?? throw new SchemaSyncException($"Could not determine directory for '{sqlprojPath}'.");

        var doc = LoadProject(sqlprojPath);
        var style = SqlProjectStyleExtensions.Detect(sqlprojPath);

        return style == SqlProjectStyle.Legacy
            ? CollectLegacy(doc, projectDir, sqlprojPath, logger)
            : CollectSdk(doc, projectDir, logger);
    }

    private static string[] CollectLegacy(
        XDocument doc, string projectDir, string sqlprojPath, ILogger logger)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var include in EnumerateItemIncludes(doc, "Build"))
        {
            var full = ResolveToProject(projectDir, include);
            if (!File.Exists(full))
            {
                LogMissingBuildItem(logger, include, sqlprojPath);
                continue;
            }

            if (seen.Add(NormaliseForCompare(full)))
            {
                result.Add(full);
            }
        }

        return [.. result];
    }

    private static string[] CollectSdk(
        XDocument doc, string projectDir, ILogger logger)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in doc.Descendants())
        {
            var localName = item.Name.LocalName;
            var include = item.Attribute("Include")?.Value;

            if (!string.IsNullOrWhiteSpace(include) && SdkNonBuildItemNames.Contains(localName))
            {
                excluded.Add(NormaliseForCompare(ResolveToProject(projectDir, include)));
            }

            if (string.Equals(localName, "Build", StringComparison.Ordinal))
            {
                var remove = item.Attribute("Remove")?.Value;
                if (!string.IsNullOrWhiteSpace(remove))
                {
                    excluded.Add(NormaliseForCompare(ResolveToProject(projectDir, remove)));
                }
            }
        }

        var result = new List<string>();
        var skipped = 0;
        foreach (var file in Directory.EnumerateFiles(projectDir, "*.sql", SearchOption.AllDirectories))
        {
            if (IsUnderBinOrObj(projectDir, file))
            {
                skipped++;
                continue;
            }

            if (excluded.Contains(NormaliseForCompare(file)))
            {
                skipped++;
                continue;
            }

            result.Add(file);
        }

        if (skipped > 0)
        {
            LogSdkSkipped(logger, skipped);
        }

        return [.. result];
    }

    private static IEnumerable<string> EnumerateItemIncludes(XDocument doc, string itemName)
    {
        foreach (var item in doc.Descendants())
        {
            if (!string.Equals(item.Name.LocalName, itemName, StringComparison.Ordinal))
            {
                continue;
            }

            var include = item.Attribute("Include")?.Value;
            if (!string.IsNullOrWhiteSpace(include))
            {
                yield return include;
            }
        }
    }

    private static XDocument LoadProject(string sqlprojPath)
    {
        try
        {
            return XDocument.Load(sqlprojPath);
        }
        catch (XmlException ex)
        {
            throw new SchemaSyncException($"Failed to parse '{sqlprojPath}' as XML.", ex);
        }
        catch (IOException ex)
        {
            throw new SchemaSyncException($"Failed to read '{sqlprojPath}'.", ex);
        }
    }

    private static string ResolveToProject(string projectDir, string include)
    {
        // .sqlproj Include attributes are backslash-separated, but tolerate forward
        // slashes anyway — MSBuild does, and so will we.
        var normalised = include.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.IsPathRooted(normalised)
            ? normalised
            : Path.Combine(projectDir, normalised);
        return Path.GetFullPath(full);
    }

    private static string NormaliseForCompare(string path)
    {
        return Path.GetFullPath(path).Replace('/', '\\');
    }

    private static bool IsUnderBinOrObj(string projectDir, string filePath)
    {
        var rel = Path.GetRelativePath(projectDir, filePath).Replace('/', '\\');
        return rel.StartsWith("bin\\", StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith("obj\\", StringComparison.OrdinalIgnoreCase)
            || rel.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
            || rel.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Project '{Project}' declares <Build Include=\"{Include}\"/> but the file does not exist on disk; skipping.")]
    private static partial void LogMissingBuildItem(ILogger logger, string include, string project);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Skipped {Count} non-build .sql file(s) (bin/obj or non-Build items).")]
    private static partial void LogSdkSkipped(ILogger logger, int count);
}
