using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.Dac.Projects;

namespace SqlProjectSync;

/// <summary>
/// Closes the gap DacFx leaves on legacy <c>.sqlproj</c> files: after
/// <see cref="Microsoft.SqlServer.Dac.Compare.SchemaComparisonResult.PublishChangesToProject(string, Microsoft.SqlServer.Dac.DacExtractTarget)"/>
/// writes/deletes the <c>.sql</c> files, this helper opens the project via the preview
/// <c>Microsoft.SqlServer.DacFx.Projects</c> package and replays those file operations
/// onto the <c>&lt;Build Include="..."/&gt;</c> items in the project XML.
///
/// SDK-style projects auto-glob <c>**/*.sql</c> and need no XML patching — they're skipped.
/// </summary>
internal static partial class LegacyProjectPatcher
{
    public static void PatchIfNeeded(string projectPath, PublishResult publish, ILogger logger)
    {
        if (SqlProjectStyleExtensions.Detect(projectPath) != SqlProjectStyle.Legacy)
        {
            return;
        }

        if (publish.AddedFiles.Count == 0 && publish.DeletedFiles.Count == 0)
        {
            return;
        }

        var projectDir = Path.GetDirectoryName(projectPath)
            ?? throw new SchemaSyncException($"Could not determine directory for '{projectPath}'.");

        var project = SqlProject.OpenProject(projectPath, false);

        foreach (var added in publish.AddedFiles)
        {
            var rel = ToProjectRelative(added, projectDir);
            if (!project.SqlObjectScripts.Contains(rel))
            {
                project.SqlObjectScripts.Add(new SqlObjectScript(rel));
                LogPatched(logger, "added", rel);
            }
        }

        foreach (var deleted in publish.DeletedFiles)
        {
            var rel = ToProjectRelative(deleted, projectDir);
            if (project.SqlObjectScripts.Contains(rel))
            {
                project.SqlObjectScripts.Delete(rel);
                LogPatched(logger, "removed", rel);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Legacy .sqlproj <Build> {Action}: {Path}")]
    private static partial void LogPatched(ILogger logger, string action, string path);

    private static string ToProjectRelative(string fileFromPublish, string projectDir)
    {
        var fullPath = Path.IsPathRooted(fileFromPublish)
            ? fileFromPublish
            : Path.Combine(projectDir, fileFromPublish);
        var rel = Path.GetRelativePath(projectDir, fullPath);
        return rel.Replace('/', '\\');
    }
}
