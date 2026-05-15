using Microsoft.SqlServer.Dac;

namespace SqlProjectSync;

/// <summary>Options that control how <see cref="SchemaSync"/> runs a comparison and publishes changes.</summary>
public sealed record SyncOptions
{
    /// <summary>Folder layout DacFx applies when writing files into the target project. Defaults to <see cref="DacExtractTarget.SchemaObjectType"/>.</summary>
    public DacExtractTarget FolderStructure { get; init; } = DacExtractTarget.SchemaObjectType;

    /// <summary>Overrides the target <c>.sqlproj</c> path that would otherwise be resolved from the <c>.scmp</c>.</summary>
    public string? OverrideTargetProjectPath { get; init; }

    /// <summary>Overrides the source connection string embedded in the <c>.scmp</c>.</summary>
    public string? OverrideSourceConnectionString { get; init; }

    /// <summary>Overrides the data schema provider (DSP) string normally read from the target <c>.sqlproj</c>.</summary>
    public string? DataSchemaProvider { get; init; }

    /// <summary>
    /// Controls whether <see cref="SchemaSync.Apply"/> rewrites trailing
    /// <c>ALTER TABLE ADD CONSTRAINT</c> statements as inline constraints
    /// inside <c>CREATE TABLE</c>. Defaults to <see cref="InlineConstraintsMode.None"/>
    /// — only the mandatory dedup pass runs.
    /// </summary>
    public InlineConstraintsMode InlineConstraintsMode { get; init; } = InlineConstraintsMode.None;

    /// <summary>
    /// When <c>true</c>, <see cref="SchemaSync.Apply"/> strips leading
    /// whitespace-only lines from every touched <c>.sql</c> file. DacFx
    /// occasionally emits a blank line before the first statement (especially
    /// before a leading comment that precedes a <c>CREATE TABLE</c>); enabling
    /// this option produces a quieter first-sync diff. Defaults to <c>false</c>.
    /// </summary>
    public bool TrimLeadingBlankLines { get; init; }
}
