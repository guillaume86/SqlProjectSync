using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace SqlProjectSync.IntegrationTests;

/// <summary>
/// Builds the SDK fixture into a .dacpac once per test session and exposes a connection
/// string for LocalDB. Individual tests create their own database off this server so they
/// stay isolated.
/// </summary>
public sealed class LocalDbFixture : IAsyncLifetime, IDatabaseFixture
{
    public string ServerConnectionString { get; private set; } = string.Empty;

    public string SdkDacpacPath { get; private set; } = string.Empty;

    public bool IsAvailable { get; private set; }

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
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailabilityReason = $"LocalDB not reachable: {ex.Message}";
            return;
        }

        try
        {
            SdkDacpacPath = SqlServerHelpers.BuildSdkFixtureDacpac();
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailabilityReason = $"Failed to build SDK fixture .dacpac: {ex.Message}";
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public string BuildConnectionString(string databaseName) =>
        SqlServerHelpers.BuildConnectionString(ServerConnectionString, databaseName);

    public Task DeployDacpacAsync(string databaseName, string dacpacPath) =>
        SqlServerHelpers.DeployDacpacAsync(ServerConnectionString, databaseName, dacpacPath);

    public Task DropDatabaseAsync(string databaseName) =>
        SqlServerHelpers.DropDatabaseAsync(ServerConnectionString, databaseName);
}
