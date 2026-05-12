using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SqlServer.Dac.Compare;

namespace SqlProjectSync;

/// <summary>
/// Pulls a SQL Server database schema into a <c>.sqlproj</c> using DacFx's public
/// <see cref="SchemaCompareProjectEndpoint"/> and <see cref="SchemaComparisonResult.PublishChangesToProject(string, Microsoft.SqlServer.Dac.DacExtractTarget)"/>.
/// </summary>
public static class SchemaSync
{
    /// <summary>Runs a schema comparison described by a <c>.scmp</c> file against the target project.</summary>
    /// <param name="scmpPath">Path to the <c>.scmp</c> file.</param>
    /// <param name="options">Optional sync options.</param>
    /// <param name="logger">Optional logger; defaults to <see cref="NullLogger.Instance"/>.</param>
    public static SchemaSyncResult Compare(
        string scmpPath,
        SyncOptions? options = null,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(scmpPath))
        {
            throw new ArgumentException("Scmp path is required.", nameof(scmpPath));
        }

        options ??= new SyncOptions();
        logger ??= NullLogger.Instance;

        var scmp = ScmpModel.Load(scmpPath);
        var projectPath = options.OverrideTargetProjectPath ?? scmp.GetTargetProjectPath();
        var projectDir = Path.GetDirectoryName(projectPath)
            ?? throw new SchemaSyncException($"Could not determine directory for '{projectPath}'.");

        var dsp = options.DataSchemaProvider ?? DspReader.ReadFromProject(projectPath);
        var scripts = Directory.GetFiles(projectDir, "*.sql", SearchOption.AllDirectories);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                "Comparing scmp='{Scmp}' project='{Project}' dsp='{Dsp}' scripts={ScriptCount}",
                scmpPath,
                projectPath,
                dsp,
                scripts.Length);
        }

        var source = scmp.BuildSourceEndpoint(options.OverrideSourceConnectionString);
        var target = new SchemaCompareProjectEndpoint(projectPath, scripts, dsp, options.FolderStructure);

        var comparison = new SchemaComparison(source, target);
        scmp.ApplyOptionsAndExclusions(comparison, logger);

        var result = comparison.Compare();
        return new SchemaSyncResult(result, projectPath, options.FolderStructure);
    }

    /// <summary>Applies a comparison result to the target project on disk, or returns a preview when <paramref name="preview"/> is <c>true</c>.</summary>
    /// <param name="comparison">The comparison result returned by <see cref="Compare"/>.</param>
    /// <param name="preview">When <c>true</c>, returns the projected differences without writing to disk.</param>
    /// <param name="logger">Optional logger; defaults to <see cref="NullLogger.Instance"/>.</param>
    public static PublishResult Apply(
        SchemaSyncResult comparison,
        bool preview = false,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        logger ??= NullLogger.Instance;

        if (preview)
        {
            logger.LogDebug("Preview requested; not writing to disk.");
            return PublishResult.Preview(comparison.Differences);
        }

        var projectDir = Path.GetDirectoryName(comparison.ProjectPath)
            ?? throw new SchemaSyncException(
                $"Could not determine directory for '{comparison.ProjectPath}'.");

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Publishing schema changes into '{ProjectDir}' with folder structure {FolderStructure}.",
                projectDir,
                comparison.FolderStructure);
        }

        var publish = comparison.Inner.PublishChangesToProject(projectDir, comparison.FolderStructure);
        if (!publish.Success)
        {
            throw new SchemaSyncException(
                $"PublishChangesToProject failed for '{comparison.ProjectPath}': {publish.ErrorMessage}");
        }

        var result = PublishResult.FromDacFx(publish);
        LegacyProjectPatcher.PatchIfNeeded(comparison.ProjectPath, result, logger);
        return result;
    }
}
