using Shouldly;
using Xunit;

namespace SqlProjectSync.IntegrationTests;

/// <summary>
/// Scenarios that exercise <see cref="SchemaSync.Compare"/> and <see cref="SchemaSync.Apply"/>
/// against a real SQL Server. Concrete subclasses bind an <see cref="IDatabaseFixture"/>
/// (LocalDB, SQL Server 2022 container, etc.) via <see cref="IClassFixture{T}"/>.
/// </summary>
public abstract class SyncIntegrationTestsBase
{
    protected SyncIntegrationTestsBase(IDatabaseFixture fixture)
    {
        Fixture = fixture;
    }

    protected IDatabaseFixture Fixture { get; }

    [Fact]
    public async Task Compare_NoFileLevelChanges_WhenDbMatchesProject()
    {
        SkipIfUnavailable();

        await using var ctx = await SyncTestContext.CreateAsync(Fixture, RepoLayout.SdkFixtureDirectory);

        var comparison = SchemaSync.Compare(ctx.ScmpPath);
        comparison.IsValid.ShouldBeTrue();

        // Some database-level properties (collation, filegroup, compat level) commonly differ
        // between a freshly-published DB and its source project even with default DacFx options.
        // What matters operationally is that no .sql files in the project would actually change.
        var publish = SchemaSync.Apply(comparison);
        publish.Success.ShouldBeTrue();
        publish.AddedFiles.ShouldBeEmpty();
        publish.DeletedFiles.ShouldBeEmpty();
        publish.ChangedFiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task Apply_RemovesSqlFile_WhenTableDroppedInDb()
    {
        SkipIfUnavailable();

        await using var ctx = await SyncTestContext.CreateAsync(Fixture, RepoLayout.SdkFixtureDirectory);

        await ctx.ExecuteSqlAsync("DROP TABLE [dbo].[Table2];");

        var comparison = SchemaSync.Compare(ctx.ScmpPath);
        comparison.Differences.ShouldNotBeEmpty();

        var publish = SchemaSync.Apply(comparison);

        publish.Success.ShouldBeTrue();
        publish.DeletedFiles.ShouldContain(f => Path.GetFileName(f).Equals("Table2.sql", StringComparison.OrdinalIgnoreCase));

        var table2OnDisk = Path.Combine(ctx.ProjectDirectory, "dbo", "Tables", "Table2.sql");
        File.Exists(table2OnDisk).ShouldBeFalse();
    }

    [Fact]
    public async Task Apply_AddsSqlFile_WhenTableAddedInDb()
    {
        SkipIfUnavailable();

        await using var ctx = await SyncTestContext.CreateAsync(Fixture, RepoLayout.SdkFixtureDirectory);

        await ctx.ExecuteSqlAsync("""
            CREATE TABLE [dbo].[Table3]
            (
                [Id]   INT          NOT NULL PRIMARY KEY,
                [Note] NVARCHAR(50) NULL
            );
            """);

        var comparison = SchemaSync.Compare(ctx.ScmpPath);
        comparison.Differences.ShouldNotBeEmpty();

        var publish = SchemaSync.Apply(comparison);

        publish.Success.ShouldBeTrue();
        publish.AddedFiles.ShouldContain(f => Path.GetFileName(f).Equals("Table3.sql", StringComparison.OrdinalIgnoreCase));
        Directory.EnumerateFiles(ctx.ProjectDirectory, "Table3.sql", SearchOption.AllDirectories)
            .ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Apply_ModifiesSqlFile_WhenColumnAdded()
    {
        SkipIfUnavailable();

        await using var ctx = await SyncTestContext.CreateAsync(Fixture, RepoLayout.SdkFixtureDirectory);

        await ctx.ExecuteSqlAsync("ALTER TABLE [dbo].[Table1] ADD [Description] NVARCHAR(200) NULL;");

        var comparison = SchemaSync.Compare(ctx.ScmpPath);
        comparison.Differences.ShouldNotBeEmpty();

        var publish = SchemaSync.Apply(comparison);

        publish.Success.ShouldBeTrue();
        publish.ChangedFiles.ShouldContain(f => Path.GetFileName(f).Equals("Table1.sql", StringComparison.OrdinalIgnoreCase));

        var table1OnDisk = Path.Combine(ctx.ProjectDirectory, "dbo", "Tables", "Table1.sql");
        File.ReadAllText(table1OnDisk).ShouldContain("[Description]");
    }

    [Fact]
    public async Task Apply_Preview_DoesNotTouchDisk()
    {
        SkipIfUnavailable();

        await using var ctx = await SyncTestContext.CreateAsync(Fixture, RepoLayout.SdkFixtureDirectory);

        await ctx.ExecuteSqlAsync("DROP TABLE [dbo].[Table2];");

        var table2OnDisk = Path.Combine(ctx.ProjectDirectory, "dbo", "Tables", "Table2.sql");
        var before = File.ReadAllText(table2OnDisk);

        var comparison = SchemaSync.Compare(ctx.ScmpPath);
        var publish = SchemaSync.Apply(comparison, preview: true);

        publish.IsPreview.ShouldBeTrue();
        publish.PreviewDifferences.ShouldNotBeEmpty();
        publish.DeletedFiles.ShouldBeEmpty();
        publish.AddedFiles.ShouldBeEmpty();
        publish.ChangedFiles.ShouldBeEmpty();

        File.Exists(table2OnDisk).ShouldBeTrue();
        File.ReadAllText(table2OnDisk).ShouldBe(before);
    }

    private void SkipIfUnavailable()
    {
        if (!Fixture.IsAvailable)
        {
            Assert.Skip(Fixture.UnavailabilityReason ?? "Database backend not available.");
        }
    }
}
