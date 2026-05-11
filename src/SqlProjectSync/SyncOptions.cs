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
}
