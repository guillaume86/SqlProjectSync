using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SqlServer.Dac.Compare;

namespace SqlProjectSync;

/// <summary>
/// Pulls a SQL Server database schema into a <c>.sqlproj</c> using DacFx's public
/// <see cref="SchemaCompareProjectEndpoint"/> and <see cref="SchemaComparisonResult.PublishChangesToProject(string, Microsoft.SqlServer.Dac.DacExtractTarget)"/>.
/// </summary>
public static partial class SchemaSync
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

        LogRunningComparison(logger, scmpPath);

        var scmp = ScmpModel.Load(scmpPath);
        var projectPath = options.OverrideTargetProjectPath ?? scmp.GetTargetProjectPath();

        var dsp = options.DataSchemaProvider ?? DspReader.ReadFromProject(projectPath);
        var scripts = ProjectScriptCollector.Collect(projectPath, logger);

        LogResolvedTarget(logger, projectPath, dsp, scripts.Length);

        var source = scmp.BuildSourceEndpoint(options.OverrideSourceConnectionString);
        var target = new SchemaCompareProjectEndpoint(projectPath, scripts, dsp, options.FolderStructure);

        var comparison = new SchemaComparison(source, target);
        scmp.ApplyOptionsAndExclusions(comparison, logger);

        var result = comparison.Compare();
        var differences = result.Differences?.ToList() ?? [];
        LogComparisonComplete(logger, differences.Count);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            foreach (var diff in differences)
            {
                var name = diff.Name ?? "<unknown>";
                LogDifference(logger, diff.UpdateAction, name);
            }
        }

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
            LogPreview(logger);
            return PublishResult.Preview(comparison.Differences);
        }

        var projectDir = Path.GetDirectoryName(comparison.ProjectPath)
            ?? throw new SchemaSyncException(
                $"Could not determine directory for '{comparison.ProjectPath}'.");

        LogUpdatingProject(logger, comparison.ProjectPath);

        var publish = comparison.Inner.PublishChangesToProject(projectDir, comparison.FolderStructure);
        if (!publish.Success)
        {
            throw new SchemaSyncException(
                $"PublishChangesToProject failed for '{comparison.ProjectPath}': {publish.ErrorMessage}");
        }

        var result = PublishResult.FromDacFx(publish);

        foreach (var added in result.AddedFiles)
        {
            var rel = ToProjectRelative(projectDir, added);
            LogAdded(logger, rel);
        }

        foreach (var deleted in result.DeletedFiles)
        {
            var rel = ToProjectRelative(projectDir, deleted);
            LogDeleted(logger, rel);
        }

        foreach (var changed in result.ChangedFiles)
        {
            var rel = ToProjectRelative(projectDir, changed);
            LogChanged(logger, rel);
        }

        LegacyProjectPatcher.PatchIfNeeded(comparison.ProjectPath, result, logger);
        LogSaveComplete(logger);
        return result;
    }

    private static string ToProjectRelative(string projectDir, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return filePath;
        }

        var full = Path.IsPathRooted(filePath)
            ? filePath
            : Path.Combine(projectDir, filePath);
        return Path.GetRelativePath(projectDir, full).Replace('/', '\\');
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Running schema comparison using \"{ScmpPath}\"...")]
    private static partial void LogRunningComparison(ILogger logger, string scmpPath);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Resolved target project \"{ProjectPath}\" (dsp={Dsp}, {ScriptCount} build script(s)).")]
    private static partial void LogResolvedTarget(ILogger logger, string projectPath, string dsp, int scriptCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Schema comparison complete. Found {DifferenceCount} difference(s).")]
    private static partial void LogComparisonComplete(ILogger logger, int differenceCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "-> {Action} {Name}")]
    private static partial void LogDifference(ILogger logger, SchemaUpdateAction action, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Preview requested; not writing to disk.")]
    private static partial void LogPreview(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updating project \"{ProjectPath}\"...")]
    private static partial void LogUpdatingProject(ILogger logger, string projectPath);

    [LoggerMessage(Level = LogLevel.Information, Message = "-> Add \"{Path}\"")]
    private static partial void LogAdded(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "-> Remove \"{Path}\"")]
    private static partial void LogDeleted(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "-> Change \"{Path}\"")]
    private static partial void LogChanged(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Save complete.")]
    private static partial void LogSaveComplete(ILogger logger);
}
