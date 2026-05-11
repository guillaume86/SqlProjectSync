using Shouldly;
using Xunit;

namespace SqlProjectSync.Tests;

public class DspReaderTests
{
    [Fact]
    public void ReadFromProject_LegacyDsp_ReturnsExplicitValue()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "Legacy.sqlproj",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003" ToolsVersion="4.0">
              <PropertyGroup>
                <Name>Legacy</Name>
                <DSP>Microsoft.Data.Tools.Schema.Sql.Sql140DatabaseSchemaProvider</DSP>
              </PropertyGroup>
            </Project>
            """);

        DspReader.ReadFromProject(path)
            .ShouldBe("Microsoft.Data.Tools.Schema.Sql.Sql140DatabaseSchemaProvider");
    }

    [Fact]
    public void ReadFromProject_SqlServerVersion_MapsTo_DspString()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "Sdk.sqlproj",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project Sdk="Microsoft.Build.Sql/2.1.0">
              <PropertyGroup>
                <Name>Sdk</Name>
                <SqlServerVersion>Sql150</SqlServerVersion>
              </PropertyGroup>
            </Project>
            """);

        DspReader.ReadFromProject(path)
            .ShouldBe("Microsoft.Data.Tools.Schema.Sql.Sql150DatabaseSchemaProvider");
    }

    [Fact]
    public void ReadFromProject_NoDspOrVersion_FallsBackToSql160()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "Sdk.sqlproj",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project Sdk="Microsoft.Build.Sql/2.1.0">
              <PropertyGroup>
                <Name>Sdk</Name>
              </PropertyGroup>
            </Project>
            """);

        DspReader.ReadFromProject(path)
            .ShouldBe("Microsoft.Data.Tools.Schema.Sql.Sql160DatabaseSchemaProvider");
    }

    [Fact]
    public void ReadFromProject_DspWinsOverSqlServerVersion()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "Both.sqlproj",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project Sdk="Microsoft.Build.Sql/2.1.0">
              <PropertyGroup>
                <DSP>Microsoft.Data.Tools.Schema.Sql.Sql140DatabaseSchemaProvider</DSP>
                <SqlServerVersion>Sql160</SqlServerVersion>
              </PropertyGroup>
            </Project>
            """);

        DspReader.ReadFromProject(path)
            .ShouldBe("Microsoft.Data.Tools.Schema.Sql.Sql140DatabaseSchemaProvider");
    }

    [Fact]
    public void ReadFromProject_MalformedXml_Throws()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("Broken.sqlproj", "<<this is broken");

        Should.Throw<SchemaSyncException>(() => DspReader.ReadFromProject(path));
    }
}
