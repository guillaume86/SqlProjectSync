namespace SqlProjectSync.IntegrationTests;

/// <summary>
/// Common surface for any SQL Server instance the integration tests can target.
/// Implementations own the lifecycle (start, build .dacpac, dispose).
/// </summary>
public interface IDatabaseFixture
{
    string ServerConnectionString { get; }

    string SdkDacpacPath { get; }

    bool IsAvailable { get; }

    string? UnavailabilityReason { get; }

    string BuildConnectionString(string databaseName);

    Task DeployDacpacAsync(string databaseName, string dacpacPath);

    Task DropDatabaseAsync(string databaseName);
}
