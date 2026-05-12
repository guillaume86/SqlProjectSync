using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.Dac.Compare;

namespace SqlProjectSync;

internal enum ScmpShape
{
    Legacy,
    Modern,
}

internal interface IModelProvider
{
}

internal sealed record ConnectionBasedModelProvider(string ConnectionString) : IModelProvider;

internal sealed record FileBasedModelProvider(string Name, string DatabaseFileName) : IModelProvider;

internal sealed record ProjectBasedModelProvider(Guid ProjectGuid, string Name, string? ProjectFilePath = null) : IModelProvider;

internal sealed class ScmpModel
{
    private readonly XDocument _doc;

    private ScmpModel(string scmpPath, XDocument doc, ScmpShape shape, IModelProvider source, IModelProvider target)
    {
        ScmpPath = scmpPath;
        _doc = doc;
        Shape = shape;
        Source = source;
        Target = target;
    }

    public string ScmpPath { get; }

    public ScmpShape Shape { get; }

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

        var shape = DetectShape(root);
        var fullPath = Path.GetFullPath(scmpPath);
        var scmpDir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();

        var source = ReadProvider(root, "SourceModelProvider", scmpPath, scmpDir);
        var target = ReadProvider(root, "TargetModelProvider", scmpPath, scmpDir);

        return new ScmpModel(fullPath, doc, shape, source, target);
    }

    public string GetTargetProjectPath()
    {
        if (Target is not ProjectBasedModelProvider p)
        {
            throw new SchemaSyncException(
                $"Target provider is {Target.GetType().Name}; cannot resolve a .sqlproj path.");
        }

        if (p.ProjectFilePath is { } resolvedPath)
        {
            if (!File.Exists(resolvedPath))
            {
                throw new SchemaSyncException(
                    $"Target .sqlproj '{resolvedPath}' referenced by '{ScmpPath}' does not exist.");
            }

            return resolvedPath;
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
        if (Shape == ScmpShape.Legacy)
        {
            LegacyScmpReader.Apply(_doc, ScmpPath, comparison, logger);
            return;
        }

        SchemaComparison parsed;
        try
        {
            parsed = new SchemaComparison(ScmpPath);
        }
        catch (Exception ex)
        {
            throw new SchemaSyncException(
                $"DacFx failed to load modern-shape .scmp '{ScmpPath}': {ex.Message}",
                ex);
        }

        CopyOptions(parsed.Options, comparison.Options);
        CopyCollection(parsed.ExcludedSourceObjects, comparison.ExcludedSourceObjects);
        CopyCollection(parsed.ExcludedTargetObjects, comparison.ExcludedTargetObjects);
    }

    private static ScmpShape DetectShape(XElement root)
    {
        // Look at the target provider only — sources are DB/dacpac in our flow.
        var targetProject = root
            .Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "TargetModelProvider", StringComparison.Ordinal))
            ?.Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "ProjectBasedModelProvider", StringComparison.Ordinal));

        if (targetProject is null)
        {
            return ScmpShape.Modern;
        }

        var hasProjectFilePath = targetProject.Elements()
            .Any(e => string.Equals(e.Name.LocalName, "ProjectFilePath", StringComparison.Ordinal));

        return hasProjectFilePath ? ScmpShape.Modern : ScmpShape.Legacy;
    }

    private static IModelProvider ReadProvider(XElement root, string parentName, string scmpPath, string scmpDir)
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
            "ProjectBasedModelProvider" => ReadProjectProvider(provider, scmpPath, scmpDir),
            var name => throw new SchemaSyncException(
                $"'{scmpPath}' has unknown provider element <{name}>."),
        };
    }

    private static ProjectBasedModelProvider ReadProjectProvider(XElement provider, string scmpPath, string scmpDir)
    {
        var projectFilePath = ChildValueOptional(provider, "ProjectFilePath");
        if (!string.IsNullOrWhiteSpace(projectFilePath))
        {
            var resolved = Path.IsPathRooted(projectFilePath)
                ? Path.GetFullPath(projectFilePath)
                : Path.GetFullPath(Path.Combine(scmpDir, projectFilePath));
            var name = Path.GetFileNameWithoutExtension(resolved);
            return new ProjectBasedModelProvider(Guid.Empty, name, resolved);
        }

        return new ProjectBasedModelProvider(
            ParseGuid(ChildValueOptional(provider, "ProjectGuid"), scmpPath),
            ChildValue(provider, "Name", scmpPath));
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
        if (raw is null)
        {
            return Guid.Empty;
        }

        if (!Guid.TryParse(raw, out var g))
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
}
