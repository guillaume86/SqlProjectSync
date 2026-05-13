using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace SqlProjectSync.Tests;

public class ProjectScriptCollectorTests
{
    [Fact]
    public void Legacy_Returns_Only_Build_Items()
    {
        using var dir = new TempDirectory();
        var project = dir.Write(
            "Legacy.sqlproj",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup><Name>Legacy</Name></PropertyGroup>
              <ItemGroup>
                <Build Include="dbo\Tables\Table1.sql" />
                <Build Include="dbo\Tables\Table2.sql" />
              </ItemGroup>
            </Project>
            """);
        dir.Write("dbo/Tables/Table1.sql", "CREATE TABLE [dbo].[Table1] (Id INT);");
        dir.Write("dbo/Tables/Table2.sql", "CREATE TABLE [dbo].[Table2] (Id INT);");

        var scripts = ProjectScriptCollector.Collect(project, NullLogger.Instance);

        scripts.Length.ShouldBe(2);
        scripts.ShouldContain(s => s.EndsWith("Table1.sql", StringComparison.OrdinalIgnoreCase));
        scripts.ShouldContain(s => s.EndsWith("Table2.sql", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Legacy_Skips_Junk_Under_Bin_And_Obj_And_PostDeploy()
    {
        using var dir = new TempDirectory();
        var project = dir.Write(
            "Legacy.sqlproj",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup><Name>Legacy</Name></PropertyGroup>
              <ItemGroup>
                <Build Include="dbo\Tables\Table1.sql" />
              </ItemGroup>
              <ItemGroup>
                <PostDeploy Include="Script.PostDeployment.sql" />
              </ItemGroup>
            </Project>
            """);
        dir.Write("dbo/Tables/Table1.sql", "CREATE TABLE [dbo].[Table1] (Id INT);");
        dir.Write("bin/Debug/schema-creation.sql", "-- generated full schema");
        dir.Write("obj/Release/postdeploy.sql", "-- intermediate");
        dir.Write("Script.PostDeployment.sql", "PRINT '$(DatabaseName)';");

        var scripts = ProjectScriptCollector.Collect(project, NullLogger.Instance);

        scripts.Length.ShouldBe(1);
        scripts[0].ShouldEndWith("Table1.sql", Case.Insensitive);
    }

    [Fact]
    public void Legacy_Missing_Build_Item_Is_Logged_And_Skipped()
    {
        using var dir = new TempDirectory();
        var project = dir.Write(
            "Legacy.sqlproj",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup><Name>Legacy</Name></PropertyGroup>
              <ItemGroup>
                <Build Include="dbo\Tables\Real.sql" />
                <Build Include="dbo\Tables\Missing.sql" />
              </ItemGroup>
            </Project>
            """);
        dir.Write("dbo/Tables/Real.sql", "CREATE TABLE [dbo].[Real] (Id INT);");

        var scripts = ProjectScriptCollector.Collect(project, NullLogger.Instance);

        scripts.Length.ShouldBe(1);
        scripts[0].ShouldEndWith("Real.sql", Case.Insensitive);
    }

    [Fact]
    public void Sdk_Globs_Every_Real_Sql_File()
    {
        using var dir = new TempDirectory();
        var project = dir.Write(
            "Sdk.sqlproj",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project Sdk="Microsoft.Build.Sql/2.1.0">
              <PropertyGroup><Name>Sdk</Name></PropertyGroup>
            </Project>
            """);
        dir.Write("dbo/Tables/Table1.sql", "CREATE TABLE [dbo].[Table1] (Id INT);");
        dir.Write("dbo/Views/View1.sql", "CREATE VIEW [dbo].[View1] AS SELECT 1;");

        var scripts = ProjectScriptCollector.Collect(project, NullLogger.Instance);

        scripts.Length.ShouldBe(2);
    }

    [Fact]
    public void Sdk_Skips_Bin_Obj_And_NonBuild_Items()
    {
        using var dir = new TempDirectory();
        var project = dir.Write(
            "Sdk.sqlproj",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project Sdk="Microsoft.Build.Sql/2.1.0">
              <PropertyGroup><Name>Sdk</Name></PropertyGroup>
              <ItemGroup>
                <None Include="Version\Data\import.sql" />
                <PostDeploy Include="Script.PostDeployment.sql" />
              </ItemGroup>
            </Project>
            """);
        dir.Write("dbo/Tables/Table1.sql", "CREATE TABLE [dbo].[Table1] (Id INT);");
        dir.Write("Version/Data/import.sql", "INSERT INTO dbo.Table1 VALUES (1);");
        dir.Write("Script.PostDeployment.sql", "PRINT '$(DatabaseName)';");
        dir.Write("bin/Debug/schema-creation.sql", "-- generated");
        dir.Write("obj/Debug/postdeploy.sql", "-- intermediate");

        var scripts = ProjectScriptCollector.Collect(project, NullLogger.Instance);

        scripts.Length.ShouldBe(1);
        scripts[0].ShouldEndWith("Table1.sql", Case.Insensitive);
    }

    [Fact]
    public void Sdk_Honors_Build_Remove_Directive()
    {
        using var dir = new TempDirectory();
        var project = dir.Write(
            "Sdk.sqlproj",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project Sdk="Microsoft.Build.Sql/2.1.0">
              <PropertyGroup><Name>Sdk</Name></PropertyGroup>
              <ItemGroup>
                <Build Remove="dbo\Tables\Excluded.sql" />
              </ItemGroup>
            </Project>
            """);
        dir.Write("dbo/Tables/Table1.sql", "CREATE TABLE [dbo].[Table1] (Id INT);");
        dir.Write("dbo/Tables/Excluded.sql", "CREATE TABLE [dbo].[Excluded] (Id INT);");

        var scripts = ProjectScriptCollector.Collect(project, NullLogger.Instance);

        scripts.Length.ShouldBe(1);
        scripts[0].ShouldEndWith("Table1.sql", Case.Insensitive);
    }

    [Fact]
    public void Empty_Path_Throws()
    {
        Should.Throw<ArgumentException>(
            () => ProjectScriptCollector.Collect("", NullLogger.Instance));
    }
}
