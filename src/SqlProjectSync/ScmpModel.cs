using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.Dac.Compare;

namespace SqlProjectSync;

internal interface IModelProvider
{
}

internal sealed record ConnectionBasedModelProvider(string ConnectionString) : IModelProvider;

internal sealed record FileBasedModelProvider(string Name, string DatabaseFileName) : IModelProvider;

internal sealed record ProjectBasedModelProvider(Guid ProjectGuid, string Name) : IModelProvider;

internal sealed class ScmpModel
{
    private ScmpModel(string scmpPath, IModelProvider source, IModelProvider target)
    {
        ScmpPath = scmpPath;
        Source = source;
        Target = target;
    }

    public string ScmpPath { get; }

    public IModelProvider Source { get; }

    public IModelProvider Target { get; }

    public static ScmpModel Load(string scmpPath)
    {
        if (string.IsNullOrWhiteSpace(scmpPath))
        {
            throw new ArgumentException("Scmp path is required.", nameof(scmpPath));
        }

        XDocument doc;
        try
        {
            doc = XDocument.Load(scmpPath);
        }
        catch (XmlException ex)
        {
            throw new SchemaSyncException($"Failed to parse '{scmpPath}' as XML.", ex);
        }
        catch (IOException ex)
        {
            throw new SchemaSyncException($"Failed to read '{scmpPath}'.", ex);
        }

        var root = doc.Root
            ?? throw new SchemaSyncException($"'{scmpPath}' has no root element.");

        var source = ReadProvider(root, "SourceModelProvider", scmpPath);
        var target = ReadProvider(root, "TargetModelProvider", scmpPath);

        return new ScmpModel(Path.GetFullPath(scmpPath), source, target);
    }

    public string GetTargetProjectPath()
    {
        if (Target is not ProjectBasedModelProvider p)
        {
            throw new SchemaSyncException(
                $"Target provider is {Target.GetType().Name}; cannot resolve a .sqlproj path.");
        }

        var dir = Path.GetDirectoryName(ScmpPath);
        while (!string.IsNullOrEmpty(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.sqlproj"))
            {
                if (string.Equals(
                        Path.GetFileNameWithoutExtension(file),
                        p.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFullPath(file);
                }
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new SchemaSyncException(
            $"No .sqlproj matching '{p.Name}' found walking up from '{ScmpPath}'.");
    }

    public SchemaCompareEndpoint BuildSourceEndpoint(string? connectionOverride)
    {
        return Source switch
        {
            ConnectionBasedModelProvider c => new SchemaCompareDatabaseEndpoint(
                connectionOverride ?? c.ConnectionString),
            FileBasedModelProvider f => new SchemaCompareDacpacEndpoint(f.DatabaseFileName),
            ProjectBasedModelProvider _ => throw new SchemaSyncException(
                "Project-as-source is out of scope; this tool syncs DB → project."),
            _ => throw new SchemaSyncException(
                $"Unsupported source provider type: {Source.GetType().Name}."),
        };
    }

    public void ApplyOptionsAndExclusions(SchemaComparison comparison, ILogger logger)
    {
        SchemaComparison? parsed;
        try
        {
            parsed = new SchemaComparison(ScmpPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "DacFx could not load '{Scmp}' directly; attempting reflection fallback.",
                ScmpPath);
            parsed = LoadOptionsViaReflection(ScmpPath, logger);
        }

        if (parsed is null)
        {
            logger.LogWarning(
                "Proceeding with DacFx default options; .scmp options not applied for '{Scmp}'.",
                ScmpPath);
            return;
        }

        CopyOptions(parsed.Options, comparison.Options);
        CopyCollection(parsed.ExcludedSourceObjects, comparison.ExcludedSourceObjects);
        CopyCollection(parsed.ExcludedTargetObjects, comparison.ExcludedTargetObjects);
    }

    private static IModelProvider ReadProvider(XElement root, string parentName, string scmpPath)
    {
        var parent = root.Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, parentName, StringComparison.Ordinal))
            ?? throw new SchemaSyncException($"'{scmpPath}' is missing <{parentName}>.");

        var provider = parent.Elements().FirstOrDefault()
            ?? throw new SchemaSyncException($"'{scmpPath}' <{parentName}> has no provider child.");

        return provider.Name.LocalName switch
        {
            "ConnectionBasedModelProvider" => new ConnectionBasedModelProvider(
                ChildValue(provider, "ConnectionString", scmpPath)),
            "FileBasedModelProvider" => new FileBasedModelProvider(
                ChildValueOptional(provider, "Name") ?? string.Empty,
                ChildValue(provider, "DatabaseFileName", scmpPath)),
            "ProjectBasedModelProvider" => new ProjectBasedModelProvider(
                ParseGuid(ChildValueOptional(provider, "ProjectGuid"), scmpPath),
                ChildValue(provider, "Name", scmpPath)),
            var name => throw new SchemaSyncException(
                $"'{scmpPath}' has unknown provider element <{name}>."),
        };
    }

    private static string ChildValue(XElement parent, string localName, string scmpPath)
    {
        return ChildValueOptional(parent, localName)
            ?? throw new SchemaSyncException(
                $"'{scmpPath}' <{parent.Name.LocalName}> missing <{localName}>.");
    }

    private static string? ChildValueOptional(XElement parent, string localName)
    {
        return parent.Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal))
            ?.Value;
    }

    private static Guid ParseGuid(string? raw, string scmpPath)
    {
        if (raw is null || !Guid.TryParse(raw, out var g))
        {
            throw new SchemaSyncException(
                $"'{scmpPath}' ProjectGuid '{raw}' is not a valid GUID.");
        }

        return g;
    }

    private static void CopyOptions(object from, object to)
    {
        var properties = from.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in properties)
        {
            if (!prop.CanRead || !prop.CanWrite)
            {
                continue;
            }

            if (prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var value = prop.GetValue(from);
            prop.SetValue(to, value);
        }
    }

    private static void CopyCollection<T>(IList<T> from, IList<T> to)
    {
        foreach (var item in from)
        {
            to.Add(item);
        }
    }

    // Fallback when DacFx's public SchemaComparison(scmpPath) constructor rejects a
    // project-target .scmp. The exact internal symbol used by DacFx to deserialize a
    // .scmp without endpoint validation is version-dependent; if not present, we log
    // and return null so the compare runs with DacFx default options.
    private static SchemaComparison? LoadOptionsViaReflection(string scmpPath, ILogger logger)
    {
        try
        {
            var loadMethod = typeof(SchemaComparison).GetMethod(
                "LoadFromXml",
                BindingFlags.Static | BindingFlags.NonPublic);

            if (loadMethod is not null)
            {
                var xml = XDocument.Load(scmpPath);
                return loadMethod.Invoke(null, [xml]) as SchemaComparison;
            }

            logger.LogWarning(
                "No internal SCMP loader found on {Type}; cannot apply .scmp options.",
                typeof(SchemaComparison).FullName);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reflection fallback failed for '{Scmp}'.", scmpPath);
            return null;
        }
    }
}
