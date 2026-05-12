using Microsoft.Data.SqlClient;

namespace SqlProjectSync.IntegrationTests;

/// <summary>
/// Per-test isolation: copies the requested fixture into a fresh temp directory and
/// creates a uniquely-named database on the shared server with the fixture schema
/// already published. Disposing drops the DB and deletes the temp dir.
/// </summary>
internal sealed class SyncTestContext : IAsyncDisposable
{
    private readonly IDatabaseFixture _fixture;
    private readonly string _tempDir;

    private SyncTestContext(IDatabaseFixture fixture, string tempDir, string projectPath, string scmpPath, string databaseName)
    {
        _fixture = fixture;
        _tempDir = tempDir;
        ProjectPath = projectPath;
        ScmpPath = scmpPath;
        DatabaseName = databaseName;
        ConnectionString = fixture.BuildConnectionString(databaseName);
    }

    public string ProjectPath { get; }

    public string ScmpPath { get; }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    public string ProjectDirectory => Path.GetDirectoryName(ProjectPath)!;

    public static async Task<SyncTestContext> CreateAsync(IDatabaseFixture fixture, string fixtureDirectory)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SqlProjectSync.IntegrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        CopyDirectory(fixtureDirectory, tempDir);

        var projectFile = Directory.EnumerateFiles(tempDir, "*.sqlproj").First();
        var scmpFile = Directory.EnumerateFiles(tempDir, "*.scmp").First();
        var dbName = $"SqlProjectSync_{Guid.NewGuid():N}";

        var context = new SyncTestContext(fixture, tempDir, projectFile, scmpFile, dbName);
        await fixture.DeployDacpacAsync(dbName, fixture.SdkDacpacPath);
        await context.RewriteScmpConnectionAsync();
        return context;
    }

    public async Task ExecuteSqlAsync(string sql)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _fixture.DropDatabaseAsync(DatabaseName);
        }
        catch
        {
            // Best-effort cleanup; leak rather than fail teardown.
        }

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private async Task RewriteScmpConnectionAsync()
    {
        var content = await File.ReadAllTextAsync(ScmpPath);
        var rewritten = System.Text.RegularExpressions.Regex.Replace(
            content,
            "<ConnectionString>[^<]*</ConnectionString>",
            $"<ConnectionString>{System.Security.SecurityElement.Escape(ConnectionString)}</ConnectionString>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        await File.WriteAllTextAsync(ScmpPath, rewritten);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
