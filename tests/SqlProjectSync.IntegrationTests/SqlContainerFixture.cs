using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace SqlProjectSync.IntegrationTests;

/// <summary>
/// Spins up a SQL Server 2022 container via Testcontainers for the duration of the test
/// run. Skips gracefully when Docker is not reachable. Builds the SDK .dacpac once per
/// session via <see cref="SqlServerHelpers.BuildSdkFixtureDacpac"/>.
/// </summary>
public sealed class SqlContainerFixture : IAsyncLifetime, IDatabaseFixture
{
    private MsSqlContainer? _container;

    public string ServerConnectionString { get; private set; } = string.Empty;

    public string SdkDacpacPath { get; private set; } = string.Empty;

    public bool IsAvailable { get; private set; }

    public string? UnavailabilityReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                .Build();
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailabilityReason = $"SQL Server container failed to start: {ex.Message}";
            return;
        }

        // Testcontainers' connection string defaults to Encrypt=False already, but be explicit.
        var raw = _container.GetConnectionString();
        var builder = new SqlConnectionStringBuilder(raw)
        {
            TrustServerCertificate = true,
            Encrypt = false,
        };
        ServerConnectionString = builder.ConnectionString;

        try
        {
            await using var conn = new SqlConnection(ServerConnectionString);
            await conn.OpenAsync();
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailabilityReason = $"SQL Server container not reachable after start: {ex.Message}";
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

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            try
            {
                await _container.DisposeAsync();
            }
            catch
            {
                // Best-effort container teardown.
            }
        }
    }

    public string BuildConnectionString(string databaseName) =>
        SqlServerHelpers.BuildConnectionString(ServerConnectionString, databaseName);

    public Task DeployDacpacAsync(string databaseName, string dacpacPath) =>
        SqlServerHelpers.DeployDacpacAsync(ServerConnectionString, databaseName, dacpacPath);

    public Task DropDatabaseAsync(string databaseName) =>
        SqlServerHelpers.DropDatabaseAsync(ServerConnectionString, databaseName);
}
