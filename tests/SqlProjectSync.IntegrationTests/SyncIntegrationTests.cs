using Xunit;

namespace SqlProjectSync.IntegrationTests;

[Trait("Style", "Sdk")]
[Trait("Backend", "LocalDb")]
public sealed class SyncIntegrationTests : SyncIntegrationTestsBase, IClassFixture<LocalDbFixture>
{
    public SyncIntegrationTests(LocalDbFixture fixture)
        : base(fixture)
    {
    }
}
