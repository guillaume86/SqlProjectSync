using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace SqlProjectSync.IntegrationTests;

/// <summary>
/// Same sync scenarios as <see cref="SyncIntegrationTests"/> but pointed at the legacy
/// SSDT-style fixture. The DB schema is published from the SDK-built .dacpac (both
/// fixtures share the same object set), only the target <c>.sqlproj</c> differs.
///
/// Best-effort — gated on the <c>SQLPROJECTSYNC_RUN_LEGACY=1</c> environment variable.
/// CI does not block on these passing.
/// </summary>
[Trait("Style", "Legacy")]
public class LegacyCompatTests : IClassFixture<LocalDbFixture>
{
    private const string GateEnvVar = "SQLPROJECTSYNC_RUN_LEGACY";

    private readonly LocalDbFixture _fixture;

    public LegacyCompatTests(LocalDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Compare_NoFileLevelChanges_WhenDbMatchesProject()
    {
        SkipIfLegacyGateClosed();
        SkipIfLocalDbUnavailable();

        await using var ctx = await SyncTestContext.CreateAsync(_fixture, RepoLayout.LegacyFixtureDirectory);

        var comparison = SchemaSync.Compare(ctx.ScmpPath);
        comparison.IsValid.ShouldBeTrue();

        // Same loose assertion as the SDK suite: a freshly-published DB exposes
        // database-level diffs that don't translate to file-level changes.
        var publish = SchemaSync.Apply(comparison);
        publish.Success.ShouldBeTrue();
        publish.AddedFiles.ShouldBeEmpty();
        publish.DeletedFiles.ShouldBeEmpty();
        publish.ChangedFiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task Apply_RemovesSqlFile_AndBuildItem_WhenTableDroppedInDb()
    {
        SkipIfLegacyGateClosed();
        SkipIfLocalDbUnavailable();

        await using var ctx = await SyncTestContext.CreateAsync(_fixture, RepoLayout.LegacyFixtureDirectory);
        BuildItemsOf(ctx.ProjectPath).ShouldContain("dbo\\Tables\\Table2.sql");

        await ctx.ExecuteSqlAsync("DROP TABLE [dbo].[Table2];");

        var comparison = SchemaSync.Compare(ctx.ScmpPath);
        var publish = SchemaSync.Apply(comparison);

        publish.Success.ShouldBeTrue();
        publish.DeletedFiles.ShouldContain(f => Path.GetFileName(f).Equals("Table2.sql", StringComparison.OrdinalIgnoreCase));

        File.Exists(Path.Combine(ctx.ProjectDirectory, "dbo", "Tables", "Table2.sql")).ShouldBeFalse();
        BuildItemsOf(ctx.ProjectPath).ShouldNotContain("dbo\\Tables\\Table2.sql");
    }

    [Fact]
    public async Task Apply_AddsSqlFile_AndBuildItem_WhenTableAddedInDb()
    {
        SkipIfLegacyGateClosed();
        SkipIfLocalDbUnavailable();

        await using var ctx = await SyncTestContext.CreateAsync(_fixture, RepoLayout.LegacyFixtureDirectory);
        BuildItemsOf(ctx.ProjectPath).ShouldNotContain("dbo\\Tables\\Table3.sql");

        await ctx.ExecuteSqlAsync("""
            CREATE TABLE [dbo].[Table3]
            (
                [Id]   INT          NOT NULL PRIMARY KEY,
                [Note] NVARCHAR(50) NULL
            );
            """);

        var comparison = SchemaSync.Compare(ctx.ScmpPath);
        var publish = SchemaSync.Apply(comparison);

        publish.Success.ShouldBeTrue();
        publish.AddedFiles.ShouldContain(f => Path.GetFileName(f).Equals("Table3.sql", StringComparison.OrdinalIgnoreCase));

        Directory.EnumerateFiles(ctx.ProjectDirectory, "Table3.sql", SearchOption.AllDirectories)
            .ShouldNotBeEmpty();
        BuildItemsOf(ctx.ProjectPath).ShouldContain(b => b.EndsWith("Table3.sql", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> BuildItemsOf(string sqlprojPath)
    {
        var doc = XDocument.Load(sqlprojPath);
        return doc.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, "Build", StringComparison.Ordinal))
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList();
    }

    private static void SkipIfLegacyGateClosed()
    {
        if (Environment.GetEnvironmentVariable(GateEnvVar) != "1")
        {
            Assert.Skip($"Legacy compat suite gated on {GateEnvVar}=1.");
        }
    }

    private void SkipIfLocalDbUnavailable()
    {
        if (!_fixture.IsAvailable)
        {
            Assert.Skip(_fixture.UnavailabilityReason ?? "LocalDB not available.");
        }
    }
}
