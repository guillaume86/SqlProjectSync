using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Compare;
using Shouldly;
using Xunit;

namespace SqlProjectSync.IntegrationTests;

/// <summary>
/// Regression guard for <see href="https://github.com/microsoft/DacFx/issues/792">DacFx #792</see>,
/// fixed upstream in DacFx 170.5. Drives
/// <see cref="SchemaComparisonResult.PublishChangesToProject(string, DacExtractTarget)"/>
/// directly — bypassing <see cref="SchemaSync"/> and the post-publish
/// inline-constraint folder — and asserts that a constraint already declared
/// inline is not emitted a second time as a trailing <c>ALTER TABLE ADD CONSTRAINT</c>.
///
/// SqlProjectSync used to strip that duplicate itself; the dedup pass was
/// removed once DacFx shipped the fix. If this test starts failing after a
/// DacFx bump, the bug is back and the dedup pass needs to return.
///
/// Matches the minimal repro filed with the issue:
/// https://gist.github.com/guillaume86/d5a4ce3c6ba1b21f2fd35433ce560561
/// </summary>
public class DacFxIssue792Tests : IClassFixture<SqlContainerFixture>
{
    private readonly SqlContainerFixture _fixture;

    public DacFxIssue792Tests(SqlContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PublishChangesToProject_Does_Not_Duplicate_Inline_Constraint()
    {
        if (!_fixture.IsAvailable)
        {
            Assert.Skip(_fixture.UnavailabilityReason ?? "Database backend not available.");
        }

        var dbName = "DacFx792_" + Guid.NewGuid().ToString("N")[..8];
        var dbConn = _fixture.BuildConnectionString(dbName);

        await Exec(_fixture.ServerConnectionString, $"CREATE DATABASE [{dbName}];");

        var workDir = Path.Combine(Path.GetTempPath(), "SqlProjectSync.DacFx792." + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(workDir, "dbo", "Tables"));

        try
        {
            await Exec(dbConn, """
                CREATE TABLE [dbo].[Demo] (
                    [Id]   INT NOT NULL PRIMARY KEY,
                    [Flag] BIT NOT NULL
                );
                """);

            // Minimal SDK-style .sqlproj — neither legacy nor the SqlProjectSync
            // helpers are needed to trigger the bug. DacFx alone is enough.
            var sqlprojPath = Path.Combine(workDir, "Repro.sqlproj");
            File.WriteAllText(sqlprojPath, """
                <?xml version="1.0" encoding="utf-8"?>
                <Project DefaultTargets="Build">
                  <Sdk Name="Microsoft.Build.Sql" Version="2.1.0" />
                  <PropertyGroup>
                    <Name>Repro</Name>
                    <ProjectGuid>{22222222-2222-2222-2222-222222222222}</ProjectGuid>
                    <DSP>Microsoft.Data.Tools.Schema.Sql.Sql160DatabaseSchemaProvider</DSP>
                    <ModelCollation>1033, CI</ModelCollation>
                    <DefaultCollation>SQL_Latin1_General_CP1_CI_AS</DefaultCollation>
                  </PropertyGroup>
                </Project>
                """);

            var tablePath = Path.Combine(workDir, "dbo", "Tables", "Demo.sql");
            File.WriteAllText(tablePath, """
                CREATE TABLE [dbo].[Demo] (
                    [Id]   INT NOT NULL PRIMARY KEY,
                    [Flag] BIT NOT NULL
                );
                """);

            // Mutate the DB: add a column with an inline DEFAULT that the
            // project doesn't have. PublishChangesToProject should propagate
            // exactly that. Before DacFx 170.5 it emitted the constraint both
            // inline AND as a trailing ALTER TABLE in the same file.
            await Exec(dbConn, """
                ALTER TABLE [dbo].[Demo]
                    ADD [Note] NVARCHAR(50) NULL CONSTRAINT [DF_Demo_Note] DEFAULT ('hello');
                """);

            var source = new SchemaCompareDatabaseEndpoint(dbConn);
            var target = new SchemaCompareProjectEndpoint(
                sqlprojPath,
                new[] { tablePath },
                "Microsoft.Data.Tools.Schema.Sql.Sql160DatabaseSchemaProvider",
                DacExtractTarget.SchemaObjectType);

            var comparison = new SchemaComparison(source, target);
            var result = comparison.Compare(TestContext.Current.CancellationToken);
            result.IsValid.ShouldBeTrue();

            var publish = result.PublishChangesToProject(workDir, DacExtractTarget.SchemaObjectType);
            publish.Success.ShouldBeTrue(customMessage: $"PublishChangesToProject failed: {publish.ErrorMessage}");

            var after = File.ReadAllText(tablePath);
            after.ShouldContain(
                "CONSTRAINT [DF_Demo_Note] DEFAULT",
                customMessage: "Sanity check — the inline DEFAULT should always be present after publish.");

            after.ShouldNotContain(
                "ADD CONSTRAINT [DF_Demo_Note]",
                customMessage: "DacFx #792 has regressed: PublishChangesToProject emitted a redundant " +
                    "ALTER TABLE ADD CONSTRAINT for a constraint already defined inline. Restore the dedup " +
                    "pass in InlineConstraintFolder. See https://github.com/microsoft/DacFx/issues/792.");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort */ }
            try { await SqlServerHelpers.DropDatabaseAsync(_fixture.ServerConnectionString, dbName); } catch { /* best-effort */ }
        }
    }

    private static async Task Exec(string connStr, string sql)
    {
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        await cmd.ExecuteNonQueryAsync();
    }
}
