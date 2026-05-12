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
    public async Task Compare_NoDifferences_WhenDbMatchesProject()
    {
        SkipIfLegacyGateClosed();
        SkipIfLocalDbUnavailable();

        await using var ctx = await SyncTestContext.CreateAsync(_fixture, RepoLayout.LegacyFixtureDirectory);

        var result = SchemaSync.Compare(ctx.ScmpPath);

        result.IsValid.ShouldBeTrue();
        result.IsEqual.ShouldBeTrue();
    }

    [Fact]
    public async Task Apply_RemovesSqlFile_WhenTableDroppedInDb()
    {
        SkipIfLegacyGateClosed();
        SkipIfLocalDbUnavailable();

        await using var ctx = await SyncTestContext.CreateAsync(_fixture, RepoLayout.LegacyFixtureDirectory);
        await ctx.ExecuteSqlAsync("DROP TABLE [dbo].[Table2];");

        var comparison = SchemaSync.Compare(ctx.ScmpPath);
        var publish = SchemaSync.Apply(comparison);

        publish.Success.ShouldBeTrue();
        publish.DeletedFiles.ShouldContain(f => Path.GetFileName(f).Equals("Table2.sql", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Apply_AddsSqlFile_WhenTableAddedInDb()
    {
        SkipIfLegacyGateClosed();
        SkipIfLocalDbUnavailable();

        await using var ctx = await SyncTestContext.CreateAsync(_fixture, RepoLayout.LegacyFixtureDirectory);
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
