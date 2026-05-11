using Microsoft.SqlServer.Dac;
using Shouldly;
using Xunit;

namespace SqlProjectSync.Tests;

public class SyncOptionsTests
{
    [Fact]
    public void Defaults_FolderStructure_Is_SchemaObjectType()
    {
        var options = new SyncOptions();
        options.FolderStructure.ShouldBe(DacExtractTarget.SchemaObjectType);
    }

    [Fact]
    public void Defaults_AllOverrides_AreNull()
    {
        var options = new SyncOptions();
        options.OverrideTargetProjectPath.ShouldBeNull();
        options.OverrideSourceConnectionString.ShouldBeNull();
        options.DataSchemaProvider.ShouldBeNull();
    }

    [Fact]
    public void WithStatement_ProducesNewInstance_WithOverride()
    {
        var original = new SyncOptions();
        var modified = original with { DataSchemaProvider = "custom-dsp" };

        modified.DataSchemaProvider.ShouldBe("custom-dsp");
        original.DataSchemaProvider.ShouldBeNull();
    }
}
