using Shouldly;
using Xunit;

namespace SqlProjectSync.Tests;

public class SqlProjectStyleTests
{
    [Fact]
    public void Detect_SdkStyle_AttributeForm_Returns_Sdk()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "Test.sqlproj",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project Sdk="Microsoft.Build.Sql/2.1.0">
              <PropertyGroup>
                <Name>Test</Name>
              </PropertyGroup>
            </Project>
            """);

        SqlProjectStyleExtensions.Detect(path).ShouldBe(SqlProjectStyle.Sdk);
    }

    [Fact]
    public void Detect_SdkStyle_ChildElementForm_Returns_Sdk()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "Test.sqlproj",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project>
              <Sdk Name="Microsoft.Build.Sql" Version="2.1.0" />
              <PropertyGroup>
                <Name>Test</Name>
              </PropertyGroup>
            </Project>
            """);

        SqlProjectStyleExtensions.Detect(path).ShouldBe(SqlProjectStyle.Sdk);
    }

    [Fact]
    public void Detect_LegacyStyle_Returns_Legacy()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "Test.sqlproj",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003" ToolsVersion="4.0">
              <PropertyGroup>
                <Name>Test</Name>
                <DSP>Microsoft.Data.Tools.Schema.Sql.Sql160DatabaseSchemaProvider</DSP>
              </PropertyGroup>
            </Project>
            """);

        SqlProjectStyleExtensions.Detect(path).ShouldBe(SqlProjectStyle.Legacy);
    }

    [Fact]
    public void Detect_MalformedXml_Throws()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("Test.sqlproj", "this is not xml <<<");

        Should.Throw<SchemaSyncException>(() => SqlProjectStyleExtensions.Detect(path));
    }

    [Fact]
    public void Detect_NonProjectRoot_Throws()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "Test.sqlproj",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <NotAProject>
              <PropertyGroup />
            </NotAProject>
            """);

        Should.Throw<SchemaSyncException>(() => SqlProjectStyleExtensions.Detect(path));
    }

    [Fact]
    public void Detect_EmptyPath_Throws()
    {
        Should.Throw<ArgumentException>(() => SqlProjectStyleExtensions.Detect(""));
    }
}
