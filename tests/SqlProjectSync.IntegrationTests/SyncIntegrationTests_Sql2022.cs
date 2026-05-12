using Xunit;

namespace SqlProjectSync.IntegrationTests;

/// <summary>
/// Same scenarios as <see cref="SyncIntegrationTests"/> but pointed at a SQL Server 2022
/// container provisioned via Testcontainers. Skips gracefully when Docker is not
/// reachable. See <see cref="SqlContainerFixture"/>.
/// </summary>
[Trait("Style", "Sdk")]
[Trait("Backend", "Sql2022Container")]
public sealed class SyncIntegrationTests_Sql2022 : SyncIntegrationTestsBase, IClassFixture<SqlContainerFixture>
{
    public SyncIntegrationTests_Sql2022(SqlContainerFixture fixture)
        : base(fixture)
    {
    }
}
