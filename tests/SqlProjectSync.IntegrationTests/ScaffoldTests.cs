using Shouldly;
using Xunit;

namespace SqlProjectSync.IntegrationTests;

public class ScaffoldTests
{
    [Fact]
    public void Test_Infrastructure_Is_Wired()
    {
        true.ShouldBeTrue();
    }
}
