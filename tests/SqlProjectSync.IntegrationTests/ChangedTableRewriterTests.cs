using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Compare;
using Shouldly;
using Xunit;

namespace SqlProjectSync.IntegrationTests;

/// <summary>
/// Regression coverage for the <see cref="SqlProjectSync.ChangedTableRewriter"/>
/// workaround. A table-level <c>Change</c> whose inline column/constraint children
/// edit the same <c>.sql</c> file makes DacFx's
/// <see cref="SchemaComparisonResult.PublishChangesToProject(string, DacExtractTarget)"/>
/// throw <c>startIndex ('-1') must be a non-negative value</c> — the failure Bruno
/// and Olivier hit on Mpleo (a column rename with a referencing FK, and a primary-key
/// column change). These tests reproduce both shapes and assert the rewriter applies
/// them cleanly. <see cref="RawPublishChangesToProject_StillCrashesOnColumnRename"/> is
/// the tripwire that flips when DacFx fixes the bug upstream.
/// </summary>
[Trait("Style", "Sdk")]
public class ChangedTableRewriterTests : IClassFixture<SqlContainerFixture>
{
    private const string Dsp = "Microsoft.Data.Tools.Schema.Sql.Sql160DatabaseSchemaProvider";

    private readonly SqlContainerFixture _fixture;

    public ChangedTableRewriterTests(SqlContainerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task RenameColumnWithForeignKey_IsAppliedWithoutCrash()
    {
        await using var scenario = await Scenario.CreateAsync(_fixture);
        if (scenario is null)
        {
            return;
        }

        // Project: Demo.Status with a FK on it. DB: Status renamed to StatusId.
        scenario.WriteTable("Demo.sql", """
            CREATE TABLE [dbo].[Demo] (
                [Id]     INT NOT NULL,
                [Status] INT NOT NULL,
                CONSTRAINT [PK_Demo] PRIMARY KEY CLUSTERED ([Id] ASC),
                CONSTRAINT [FK_Demo_Ref] FOREIGN KEY ([Status]) REFERENCES [dbo].[Ref] ([Id])
            );
            """);
        await scenario.ExecAsync("""
            CREATE TABLE [dbo].[Ref] ([Id] INT NOT NULL CONSTRAINT [PK_Ref] PRIMARY KEY);
            CREATE TABLE [dbo].[Demo] (
                [Id] INT NOT NULL CONSTRAINT [PK_Demo] PRIMARY KEY,
                [Status] INT NOT NULL,
                CONSTRAINT [FK_Demo_Ref] FOREIGN KEY ([Status]) REFERENCES [dbo].[Ref] ([Id])
            );
            """);
        scenario.WriteTable("Ref.sql", "CREATE TABLE [dbo].[Ref] ([Id] INT NOT NULL CONSTRAINT [PK_Ref] PRIMARY KEY);");
        await scenario.ExecAsync("""
            ALTER TABLE [dbo].[Demo] DROP CONSTRAINT [FK_Demo_Ref];
            EXEC sp_rename 'dbo.Demo.Status', 'StatusId', 'COLUMN';
            ALTER TABLE [dbo].[Demo] ADD CONSTRAINT [FK_Demo_Ref] FOREIGN KEY ([StatusId]) REFERENCES [dbo].[Ref] ([Id]);
            """);

        var (success, error) = scenario.ApplyWithRewriter();

        success.ShouldBeTrue(customMessage: error);
        var demo = scenario.ReadTable("Demo.sql");
        demo.ShouldContain("[StatusId]");
        demo.ShouldNotContain("[Status] ");
        demo.ShouldContain("FOREIGN KEY ([StatusId])");
    }

    [Fact]
    public async Task PrimaryKeyColumnChange_IsAppliedWithoutCrash()
    {
        await using var scenario = await Scenario.CreateAsync(_fixture);
        if (scenario is null)
        {
            return;
        }

        // Project: PK column WidgetId is a plain INT. DB: widened to BIGINT.
        // A type change on a PK column surfaces as a child column Change that
        // DacFx edits in place before the whole-table replace — the second
        // crash shape Olivier reported.
        scenario.WriteTable("Widget.sql", """
            CREATE TABLE [dbo].[Widget] (
                [WidgetId] INT          NOT NULL,
                [Name]     NVARCHAR (50) NOT NULL,
                CONSTRAINT [PK_Widget] PRIMARY KEY CLUSTERED ([WidgetId] ASC)
            );
            """);
        await scenario.ExecAsync("""
            CREATE TABLE [dbo].[Widget] (
                [WidgetId] INT NOT NULL CONSTRAINT [PK_Widget] PRIMARY KEY CLUSTERED,
                [Name]     NVARCHAR (50) NOT NULL
            );
            """);
        await scenario.ExecAsync("""
            ALTER TABLE [dbo].[Widget] DROP CONSTRAINT [PK_Widget];
            ALTER TABLE [dbo].[Widget] ALTER COLUMN [WidgetId] BIGINT NOT NULL;
            ALTER TABLE [dbo].[Widget] ADD CONSTRAINT [PK_Widget] PRIMARY KEY CLUSTERED ([WidgetId] ASC);
            """);

        var (success, error) = scenario.ApplyWithRewriter();

        success.ShouldBeTrue(customMessage: error);
        var widget = scenario.ReadTable("Widget.sql");
        widget.ShouldContain("[WidgetId] BIGINT");
    }

    [Fact]
    public async Task ComprehensiveChange_PreservesUnchangedIndex_AndAddsNewOne()
    {
        await using var scenario = await Scenario.CreateAsync(_fixture);
        if (scenario is null)
        {
            return;
        }

        // The full Mpleo webhook shape: identity PK, a renamed column, a new
        // unique constraint, a new standalone index — plus an unchanged index
        // already in the file that must survive the rewrite untouched.
        scenario.WriteTable("Whk.sql", """
            CREATE TABLE [dbo].[Whk] (
                [WhkId]   INT CONSTRAINT [DF_Whk_WhkId] DEFAULT ((0)) NOT NULL,
                [EventId] INT NOT NULL,
                [Status]  INT NOT NULL,
                CONSTRAINT [PK_Whk] PRIMARY KEY CLUSTERED ([WhkId] ASC)
            );
            GO

            CREATE NONCLUSTERED INDEX [Idx_Whk_Keep] ON [dbo].[Whk]([EventId] ASC);
            GO
            """);
        await scenario.ExecAsync("""
            CREATE TABLE [dbo].[Whk] (
                [WhkId]    INT IDENTITY (1, 1) NOT NULL,
                [EventId]  INT NOT NULL,
                [StatusId] INT NOT NULL,
                CONSTRAINT [PK_Whk] PRIMARY KEY CLUSTERED ([WhkId] ASC),
                CONSTRAINT [UQ_Whk_Event] UNIQUE NONCLUSTERED ([EventId] ASC)
            );
            CREATE NONCLUSTERED INDEX [Idx_Whk_Status] ON [dbo].[Whk]([StatusId] ASC);
            CREATE NONCLUSTERED INDEX [Idx_Whk_Keep] ON [dbo].[Whk]([EventId] ASC);
            """);

        var (success, error) = scenario.ApplyWithRewriter();

        success.ShouldBeTrue(customMessage: error);
        var whk = scenario.ReadTable("Whk.sql");
        whk.ShouldContain("IDENTITY (1, 1)");
        whk.ShouldContain("[StatusId]");
        whk.ShouldContain("CONSTRAINT [UQ_Whk_Event] UNIQUE");
        whk.ShouldContain("[Idx_Whk_Status]", customMessage: "the new standalone index should be added");
        whk.ShouldContain("[Idx_Whk_Keep]", customMessage: "the unchanged index must be preserved");
        // The renamed column is gone and there is no leftover DEFAULT for the identity column.
        whk.ShouldNotContain("[Status] ");
        whk.ShouldNotContain("DF_Whk_WhkId");
        // No duplicate CREATE TABLE.
        CountOccurrences(whk, "CREATE TABLE").ShouldBe(1);
    }

    [Fact]
    public async Task RawPublishChangesToProject_StillCrashesOnColumnRename()
    {
        await using var scenario = await Scenario.CreateAsync(_fixture);
        if (scenario is null)
        {
            return;
        }

        scenario.WriteTable("Demo.sql", """
            CREATE TABLE [dbo].[Demo] (
                [Id]     INT NOT NULL,
                [Status] INT NOT NULL,
                CONSTRAINT [PK_Demo] PRIMARY KEY CLUSTERED ([Id] ASC),
                CONSTRAINT [FK_Demo_Ref] FOREIGN KEY ([Status]) REFERENCES [dbo].[Ref] ([Id])
            );
            """);
        scenario.WriteTable("Ref.sql", "CREATE TABLE [dbo].[Ref] ([Id] INT NOT NULL CONSTRAINT [PK_Ref] PRIMARY KEY);");
        await scenario.ExecAsync("""
            CREATE TABLE [dbo].[Ref] ([Id] INT NOT NULL CONSTRAINT [PK_Ref] PRIMARY KEY);
            CREATE TABLE [dbo].[Demo] (
                [Id] INT NOT NULL CONSTRAINT [PK_Demo] PRIMARY KEY,
                [Status] INT NOT NULL,
                CONSTRAINT [FK_Demo_Ref] FOREIGN KEY ([Status]) REFERENCES [dbo].[Ref] ([Id])
            );
            """);
        await scenario.ExecAsync("""
            ALTER TABLE [dbo].[Demo] DROP CONSTRAINT [FK_Demo_Ref];
            EXEC sp_rename 'dbo.Demo.Status', 'StatusId', 'COLUMN';
            ALTER TABLE [dbo].[Demo] ADD CONSTRAINT [FK_Demo_Ref] FOREIGN KEY ([StatusId]) REFERENCES [dbo].[Ref] ([Id]);
            """);

        // No rewriter: DacFx alone still fails with the startIndex bug.
        var result = scenario.Compare();
        var publish = result.PublishChangesToProject(scenario.WorkDir, DacExtractTarget.SchemaObjectType);

        publish.Success.ShouldBeFalse(
            customMessage: "DacFx PublishChangesToProject appears fixed for in-place table changes — "
                + "the ChangedTableRewriter workaround and this tripwire can be revisited.");
        publish.ErrorMessage.ShouldContain("startIndex");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>Self-contained DB + SDK project on the shared container; disposes both.</summary>
    private sealed class Scenario : IAsyncDisposable
    {
        private readonly SqlContainerFixture _fixture;
        private readonly string _dbName;
        private readonly List<string> _scripts = [];

        private Scenario(SqlContainerFixture fixture, string dbName, string workDir, string sqlprojPath, string dbConn)
        {
            _fixture = fixture;
            _dbName = dbName;
            WorkDir = workDir;
            SqlprojPath = sqlprojPath;
            DbConnectionString = dbConn;
        }

        public string WorkDir { get; }

        public string SqlprojPath { get; }

        public string DbConnectionString { get; }

        public static async Task<Scenario?> CreateAsync(SqlContainerFixture fixture)
        {
            if (!fixture.IsAvailable)
            {
                Assert.Skip(fixture.UnavailabilityReason ?? "Database backend not available.");
                return null;
            }

            var dbName = "RewriterTest_" + Guid.NewGuid().ToString("N")[..8];
            await Exec(fixture.ServerConnectionString, $"CREATE DATABASE [{dbName}];");

            var workDir = Path.Combine(Path.GetTempPath(), "SqlProjectSync.Rewriter." + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(workDir, "dbo", "Tables"));

            var sqlprojPath = Path.Combine(workDir, "Repro.sqlproj");
            File.WriteAllText(sqlprojPath, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <Project DefaultTargets="Build">
                  <Sdk Name="Microsoft.Build.Sql" Version="2.1.0" />
                  <PropertyGroup>
                    <Name>Repro</Name>
                    <DSP>{Dsp}</DSP>
                    <ModelCollation>1033, CI</ModelCollation>
                    <DefaultCollation>SQL_Latin1_General_CP1_CI_AS</DefaultCollation>
                  </PropertyGroup>
                </Project>
                """);

            return new Scenario(fixture, dbName, workDir, sqlprojPath, fixture.BuildConnectionString(dbName));
        }

        public void WriteTable(string fileName, string sql)
        {
            var path = Path.Combine(WorkDir, "dbo", "Tables", fileName);
            // CRLF + BOM, matching SSDT-formatted files in the wild.
            File.WriteAllText(path, sql.ReplaceLineEndings("\r\n"), new System.Text.UTF8Encoding(true));
            _scripts.Add(path);
        }

        public string ReadTable(string fileName) =>
            File.ReadAllText(Path.Combine(WorkDir, "dbo", "Tables", fileName));

        public Task ExecAsync(string sql) => Exec(DbConnectionString, sql);

        public SchemaComparisonResult Compare()
        {
            var source = new SchemaCompareDatabaseEndpoint(DbConnectionString);
            var target = new SchemaCompareProjectEndpoint(SqlprojPath, _scripts.ToArray(), Dsp, DacExtractTarget.SchemaObjectType);
            return new SchemaComparison(source, target).Compare(TestContext.Current.CancellationToken);
        }

        /// <summary>Runs the production fix path: plan rewrites, publish, execute rewrites.</summary>
        public (bool Success, string? Error) ApplyWithRewriter()
        {
            var result = Compare();
            var plans = ChangedTableRewriter.Plan(result, NullLogger.Instance);
            var publish = result.PublishChangesToProject(WorkDir, DacExtractTarget.SchemaObjectType);
            if (!publish.Success)
            {
                return (false, $"PublishChangesToProject failed: {publish.ErrorMessage}");
            }

            ChangedTableRewriter.Execute(plans, NullLogger.Instance);
            return (true, null);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await SqlServerHelpers.DropDatabaseAsync(_fixture.ServerConnectionString, _dbName);
            }
            catch
            {
                // Best-effort.
            }

            try
            {
                Directory.Delete(WorkDir, recursive: true);
            }
            catch
            {
                // Best-effort.
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
}
