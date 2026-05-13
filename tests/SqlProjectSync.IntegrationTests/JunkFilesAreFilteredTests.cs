using Shouldly;
using Xunit;

namespace SqlProjectSync.IntegrationTests;

/// <summary>
/// Regression coverage for the failure reported against the Mpleo HrWeb.Database
/// project: <c>SchemaCompareProjectEndpoint</c> was being fed every <c>.sql</c>
/// file under the project directory — including <c>bin\</c>/<c>obj\</c> build
/// outputs, <c>Script.PostDeployment.sql</c>, and data files — which made
/// <c>PublishChangesToProject</c> throw <c>ArgumentNullException: source</c>
/// after several minutes of parsing duplicates. <c>ProjectScriptCollector</c>
/// now filters these out; the assertions here would fail without it.
/// </summary>
[Trait("Style", "Legacy")]
public class JunkFilesAreFilteredTests : IClassFixture<SqlContainerFixture>
{
    private readonly SqlContainerFixture _fixture;

    public JunkFilesAreFilteredTests(SqlContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Apply_Succeeds_When_Project_Has_Bin_Obj_PostDeploy_Junk()
    {
        SkipIfBackendUnavailable();

        await using var ctx = await SyncTestContext.CreateAsync(_fixture, RepoLayout.LegacyFixtureDirectory);

        SeedJunkSqlFiles(ctx.ProjectDirectory);

        await ctx.ExecuteSqlAsync("DROP TABLE [dbo].[Table2];");

        var comparison = SchemaSync.Compare(ctx.ScmpPath);
        comparison.Differences.ShouldNotBeEmpty();

        var publish = SchemaSync.Apply(comparison);

        publish.Success.ShouldBeTrue();
        publish.DeletedFiles.ShouldContain(f => Path.GetFileName(f).Equals("Table2.sql", StringComparison.OrdinalIgnoreCase));
        File.Exists(Path.Combine(ctx.ProjectDirectory, "dbo", "Tables", "Table2.sql")).ShouldBeFalse();

        // Junk files must survive untouched — the collector ignored them so DacFx never
        // wrote against them, and our patcher never staged them as project items.
        File.Exists(Path.Combine(ctx.ProjectDirectory, "bin", "Debug", "schema-creation.sql")).ShouldBeTrue();
        File.Exists(Path.Combine(ctx.ProjectDirectory, "obj", "Release", "postdeploy.sql")).ShouldBeTrue();
        File.Exists(Path.Combine(ctx.ProjectDirectory, "Script.PostDeployment.sql")).ShouldBeTrue();
    }

    private static void SeedJunkSqlFiles(string projectDir)
    {
        // A condensed but realistic stand-in for the 3.2 MB schema dump that
        // tripped DacFx in the user's repo: redeclares every fixture object so
        // parsing it as a model contributor would create duplicate identifiers.
        var binDir = Path.Combine(projectDir, "bin", "Debug");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "schema-creation.sql"), """
            CREATE TABLE [dbo].[Table1] (Id INT);
            GO
            CREATE TABLE [dbo].[Table2] (Id INT);
            GO
            CREATE VIEW [dbo].[View1] AS SELECT 1 AS X;
            GO
            """);

        var objDir = Path.Combine(projectDir, "obj", "Release");
        Directory.CreateDirectory(objDir);
        File.WriteAllText(Path.Combine(objDir, "postdeploy.sql"), "-- build intermediate\n");

        File.WriteAllText(Path.Combine(projectDir, "Script.PostDeployment.sql"), """
            PRINT N'Hello from $(DatabaseName)';
            GO
            """);
    }

    private void SkipIfBackendUnavailable()
    {
        if (!_fixture.IsAvailable)
        {
            Assert.Skip(_fixture.UnavailabilityReason ?? "Database backend not available.");
        }
    }
}
