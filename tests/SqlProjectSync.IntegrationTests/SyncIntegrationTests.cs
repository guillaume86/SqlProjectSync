using Xunit;

namespace SqlProjectSync.IntegrationTests;

/// <summary>
/// Sync scenarios pointed at a SQL Server 2022 container provisioned via Testcontainers.
/// Skips gracefully when Docker is not reachable. See <see cref="SqlContainerFixture"/>.
/// </summary>
[Trait("Style", "Sdk")]
public sealed class SyncIntegrationTests : SyncIntegrationTestsBase, IClassFixture<SqlContainerFixture>
{
    public SyncIntegrationTests(SqlContainerFixture fixture)
        : base(fixture)
    {
    }
}
