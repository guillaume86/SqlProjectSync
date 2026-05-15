using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Compare;

namespace SqlProjectSync;

/// <summary>The result of a schema comparison, ready to be applied via <see cref="SchemaSync.Apply"/>.</summary>
public sealed class SchemaSyncResult
{
    internal SchemaSyncResult(
        SchemaComparisonResult inner,
        string projectPath,
        DacExtractTarget folderStructure,
        InlineConstraintsMode inlineConstraintsMode)
    {
        Inner = inner;
        ProjectPath = projectPath;
        FolderStructure = folderStructure;
        InlineConstraintsMode = inlineConstraintsMode;
    }

    internal SchemaComparisonResult Inner { get; }

    /// <summary>The full path of the target <c>.sqlproj</c> the comparison was run against.</summary>
    public string ProjectPath { get; }

    /// <summary>The folder layout DacFx will use when applying changes.</summary>
    public DacExtractTarget FolderStructure { get; }

    /// <summary>The inline-constraints reshape mode <see cref="SchemaSync.Apply"/> will use.</summary>
    public InlineConstraintsMode InlineConstraintsMode { get; }

    /// <summary>Whether the comparison result is valid (free of model errors).</summary>
    public bool IsValid => Inner.IsValid;

    /// <summary>Whether the source and target are equal — no changes will be applied.</summary>
    public bool IsEqual => Inner.IsEqual;

    /// <summary>The differences between the source and target schemas.</summary>
    public IEnumerable<SchemaDifference> Differences => Inner.Differences;
}
