using Microsoft.SqlServer.Dac.Compare;

namespace SqlProjectSync;

/// <summary>The outcome of applying a <see cref="SchemaSyncResult"/> via <see cref="SchemaSync.Apply"/>.</summary>
public sealed class PublishResult
{
    private PublishResult(
        bool isPreview,
        bool success,
        string? errorMessage,
        IReadOnlyList<string> addedFiles,
        IReadOnlyList<string> deletedFiles,
        IReadOnlyList<string> changedFiles,
        IReadOnlyList<SchemaDifference> previewDifferences)
    {
        IsPreview = isPreview;
        Success = success;
        ErrorMessage = errorMessage;
        AddedFiles = addedFiles;
        DeletedFiles = deletedFiles;
        ChangedFiles = changedFiles;
        PreviewDifferences = previewDifferences;
    }

    /// <summary>True when this result represents a preview rather than an applied publish.</summary>
    public bool IsPreview { get; }

    /// <summary>Whether the publish reported success (always <c>true</c> in preview mode).</summary>
    public bool Success { get; }

    /// <summary>The error message reported by DacFx when <see cref="Success"/> is <c>false</c>.</summary>
    public string? ErrorMessage { get; }

    /// <summary>Files that were added to (or would be added to in preview mode) the project on disk.</summary>
    public IReadOnlyList<string> AddedFiles { get; }

    /// <summary>Files that were deleted from the project on disk.</summary>
    public IReadOnlyList<string> DeletedFiles { get; }

    /// <summary>Files in the project on disk that were changed.</summary>
    public IReadOnlyList<string> ChangedFiles { get; }

    /// <summary>The raw differences emitted by the comparison; populated only when <see cref="IsPreview"/> is <c>true</c>.</summary>
    public IReadOnlyList<SchemaDifference> PreviewDifferences { get; }

    /// <summary>
    /// Returns a copy with <paramref name="extraChangedFiles"/> merged into
    /// <see cref="ChangedFiles"/> (de-duplicated, case-insensitive). Used to fold
    /// in files written by the <see cref="ChangedTableRewriter"/> workaround,
    /// which bypasses DacFx's publish for crash-prone table changes.
    /// </summary>
    internal PublishResult WithAdditionalChangedFiles(IEnumerable<string> extraChangedFiles)
    {
        var merged = new List<string>(ChangedFiles);
        var seen = new HashSet<string>(ChangedFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var file in extraChangedFiles)
        {
            if (seen.Add(file))
            {
                merged.Add(file);
            }
        }

        if (merged.Count == ChangedFiles.Count)
        {
            return this;
        }

        return new PublishResult(
            isPreview: IsPreview,
            success: Success,
            errorMessage: ErrorMessage,
            addedFiles: AddedFiles,
            deletedFiles: DeletedFiles,
            changedFiles: merged,
            previewDifferences: PreviewDifferences);
    }

    internal static PublishResult FromDacFx(SchemaComparePublishProjectResult result)
    {
        return new PublishResult(
            isPreview: false,
            success: result.Success,
            errorMessage: result.ErrorMessage,
            addedFiles: result.AddedFiles?.ToArray() ?? Array.Empty<string>(),
            deletedFiles: result.DeletedFiles?.ToArray() ?? Array.Empty<string>(),
            changedFiles: result.ChangedFiles?.ToArray() ?? Array.Empty<string>(),
            previewDifferences: Array.Empty<SchemaDifference>());
    }

    internal static PublishResult Preview(IEnumerable<SchemaDifference> differences)
    {
        return new PublishResult(
            isPreview: true,
            success: true,
            errorMessage: null,
            addedFiles: Array.Empty<string>(),
            deletedFiles: Array.Empty<string>(),
            changedFiles: Array.Empty<string>(),
            previewDifferences: differences.ToArray());
    }
}
