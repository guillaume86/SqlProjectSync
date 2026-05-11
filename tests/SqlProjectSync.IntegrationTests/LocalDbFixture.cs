using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.SqlServer.Dac;
using Xunit;

namespace SqlProjectSync.IntegrationTests;

/// <summary>
/// Builds the SDK fixture into a .dacpac once per test session and exposes a connection
/// string for LocalDB. Individual tests create their own database off this server so they
/// stay isolated; the fixture only owns the cached .dacpac.
/// </summary>
public sealed class LocalDbFixture : IAsyncLifetime
{
    public string ServerConnectionString { get; private set; } = string.Empty;

    public string SdkDacpacPath { get; private set; } = string.Empty;

    public bool LocalDbAvailable { get; private set; }

    public string? UnavailabilityReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables(prefix: "SQLPROJECTSYNC_")
            .Build();

        ServerConnectionString = config["CONNECTION"]
            ?? config["ConnectionString"]
            ?? @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Encrypt=false;TrustServerCertificate=true";

        try
        {
            await using var conn = new SqlConnection(ServerConnectionString);
            await conn.OpenAsync();
            LocalDbAvailable = true;
        }
        catch (Exception ex)
        {
            LocalDbAvailable = false;
            UnavailabilityReason = $"LocalDB not reachable: {ex.Message}";
            return;
        }

        try
        {
            SdkDacpacPath = BuildSdkFixtureDacpac();
        }
        catch (Exception ex)
        {
            LocalDbAvailable = false;
            UnavailabilityReason = $"Failed to build SDK fixture .dacpac: {ex.Message}";
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public string BuildConnectionString(string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(ServerConnectionString)
        {
            InitialCatalog = databaseName,
        };
        return builder.ConnectionString;
    }

    public async Task DeployDacpacAsync(string databaseName, string dacpacPath)
    {
        await Task.Yield();
        using var package = DacPackage.Load(dacpacPath);
        var services = new DacServices(ServerConnectionString);
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

    public async Task DropDatabaseAsync(string databaseName)
    {
        await using var conn = new SqlConnection(ServerConnectionString);
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

    private static string BuildSdkFixtureDacpac()
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
}
