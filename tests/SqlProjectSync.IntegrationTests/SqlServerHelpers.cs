using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace SqlProjectSync.IntegrationTests;

/// <summary>
/// Operations every <see cref="IDatabaseFixture"/> needs: build the shared SDK dacpac,
/// deploy it onto a server, drop a database, and build a per-database connection string.
/// </summary>
internal static class SqlServerHelpers
{
    public static string BuildSdkFixtureDacpac()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{RepoLayout.SdkFixtureProject}\" -c Debug",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet build for the SDK fixture.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet build of SDK fixture failed (exit {process.ExitCode}).\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }

        var dacpacPath = Path.Combine(
            RepoLayout.SdkFixtureDirectory,
            "bin",
            "Debug",
            "SdkStyleTestProject.dacpac");

        if (!File.Exists(dacpacPath))
        {
            throw new FileNotFoundException(
                $"Expected SDK fixture dacpac at '{dacpacPath}' but it was not produced.");
        }

        return dacpacPath;
    }

    public static string BuildConnectionString(string serverConnectionString, string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(serverConnectionString)
        {
            InitialCatalog = databaseName,
        };
        return builder.ConnectionString;
    }

    public static async Task DeployDacpacAsync(string serverConnectionString, string databaseName, string dacpacPath)
    {
        await Task.Yield();
        using var package = DacPackage.Load(dacpacPath);
        var services = new DacServices(serverConnectionString);
        services.Deploy(
            package,
            databaseName,
            upgradeExisting: true,
            options: new DacDeployOptions
            {
                CreateNewDatabase = true,
                BlockOnPossibleDataLoss = false,
            });
    }

    public static async Task DropDatabaseAsync(string serverConnectionString, string databaseName)
    {
        await using var conn = new SqlConnection(serverConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            IF DB_ID(@dbName) IS NOT NULL
            BEGIN
                DECLARE @sql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@dbName) +
                    N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@dbName) + N';';
                EXEC sp_executesql @sql;
            END";
        cmd.Parameters.Add(new SqlParameter("@dbName", databaseName));
        await cmd.ExecuteNonQueryAsync();
    }
}
