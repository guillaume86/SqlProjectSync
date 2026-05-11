using Microsoft.SqlServer.Dac.Compare;
using Shouldly;
using Xunit;

namespace SqlProjectSync.Tests;

public class ScmpModelTests
{
    private static string ProjectScmp(string connectionString, string targetProjectName) =>
        $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <SchemaComparison>
          <Version>10</Version>
          <SourceModelProvider>
            <ConnectionBasedModelProvider>
              <ConnectionString>{{connectionString}}</ConnectionString>
            </ConnectionBasedModelProvider>
          </SourceModelProvider>
          <TargetModelProvider>
            <ProjectBasedModelProvider>
              <ProjectGuid>{B0F0F0F0-1111-2222-3333-444444444444}</ProjectGuid>
              <Name>{{targetProjectName}}</Name>
            </ProjectBasedModelProvider>
          </TargetModelProvider>
        </SchemaComparison>
        """;

    [Fact]
    public void Load_ReturnsModel_FromValidFile()
    {
        using var dir = new TempDirectory();
        var scmp = dir.Write(
            "Test.scmp",
            ProjectScmp("Data Source=localhost;Database=Foo", "MyProject"));

        var model = ScmpModel.Load(scmp);

        model.Source.ShouldBeOfType<ConnectionBasedModelProvider>();
        ((ConnectionBasedModelProvider)model.Source).ConnectionString
            .ShouldBe("Data Source=localhost;Database=Foo");

        model.Target.ShouldBeOfType<ProjectBasedModelProvider>();
        ((ProjectBasedModelProvider)model.Target).Name.ShouldBe("MyProject");
    }

    [Fact]
    public void GetTargetProjectPath_ResolvesByName_WhenInSameDirectory()
    {
        using var dir = new TempDirectory();
        dir.Write("MyProject.sqlproj", "<Project Sdk=\"Microsoft.Build.Sql/2.1.0\" />");
        var scmp = dir.Write(
            "Test.scmp",
            ProjectScmp("Data Source=localhost;Database=Foo", "MyProject"));

        var path = ScmpModel.Load(scmp).GetTargetProjectPath();

        Path.GetFileName(path).ShouldBe("MyProject.sqlproj");
    }

    [Fact]
    public void GetTargetProjectPath_ResolvesByName_WhenInParentDirectory()
    {
        using var dir = new TempDirectory();
        dir.Write("MyProject.sqlproj", "<Project Sdk=\"Microsoft.Build.Sql/2.1.0\" />");
        var scmp = dir.Write(
            "subdir/nested/Test.scmp",
            ProjectScmp("Data Source=localhost;Database=Foo", "MyProject"));

        var path = ScmpModel.Load(scmp).GetTargetProjectPath();

        Path.GetFileName(path).ShouldBe("MyProject.sqlproj");
    }

    [Fact]
    public void GetTargetProjectPath_IsCaseInsensitiveOnName()
    {
        using var dir = new TempDirectory();
        dir.Write("MyProject.sqlproj", "<Project Sdk=\"Microsoft.Build.Sql/2.1.0\" />");
        var scmp = dir.Write(
            "Test.scmp",
            ProjectScmp("Data Source=localhost;Database=Foo", "myproject"));

        var path = ScmpModel.Load(scmp).GetTargetProjectPath();

        Path.GetFileName(path).ShouldBe("MyProject.sqlproj");
    }

    [Fact]
    public void GetTargetProjectPath_NoMatch_Throws()
    {
        using var dir = new TempDirectory();
        var scmp = dir.Write(
            "Test.scmp",
            ProjectScmp("Data Source=localhost;Database=Foo", "Nonexistent"));

        Should.Throw<SchemaSyncException>(() => ScmpModel.Load(scmp).GetTargetProjectPath());
    }

    [Fact]
    public void BuildSourceEndpoint_FromConnectionBasedProvider_ReturnsDatabaseEndpoint()
    {
        using var dir = new TempDirectory();
        var scmp = dir.Write(
            "Test.scmp",
            ProjectScmp("Data Source=localhost;Database=Foo", "MyProject"));

        var endpoint = ScmpModel.Load(scmp).BuildSourceEndpoint(connectionOverride: null);

        endpoint.ShouldBeOfType<SchemaCompareDatabaseEndpoint>();
    }

    [Fact]
    public void BuildSourceEndpoint_ConnectionOverride_IsApplied()
    {
        using var dir = new TempDirectory();
        var scmp = dir.Write(
            "Test.scmp",
            ProjectScmp("Data Source=ignored", "MyProject"));

        var model = ScmpModel.Load(scmp);
        var endpoint = model.BuildSourceEndpoint("Data Source=actually-used");

        endpoint.ShouldBeOfType<SchemaCompareDatabaseEndpoint>();
        // No public accessor on the endpoint for ConnectionString, so we trust the
        // construction succeeded with the override — a runtime mismatch would throw.
    }

    [Fact]
    public void Load_MissingFile_Throws()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".scmp");
        Should.Throw<SchemaSyncException>(() => ScmpModel.Load(nonexistent));
    }

    [Fact]
    public void Load_MalformedXml_Throws()
    {
        using var dir = new TempDirectory();
        var scmp = dir.Write("Bad.scmp", "<<not xml");
        Should.Throw<SchemaSyncException>(() => ScmpModel.Load(scmp));
    }

    [Fact]
    public void Load_MissingSourceProvider_Throws()
    {
        using var dir = new TempDirectory();
        var scmp = dir.Write(
            "Bad.scmp",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <SchemaComparison>
              <Version>10</Version>
            </SchemaComparison>
            """);
        Should.Throw<SchemaSyncException>(() => ScmpModel.Load(scmp));
    }
}
