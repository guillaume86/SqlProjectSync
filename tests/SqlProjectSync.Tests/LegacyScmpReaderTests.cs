using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Compare;
using Shouldly;
using Xunit;

namespace SqlProjectSync.Tests;

public class LegacyScmpReaderTests
{
    private static SchemaComparison NewComparison()
    {
        var source = new SchemaCompareDatabaseEndpoint("Data Source=ignored;Initial Catalog=src;Integrated Security=True");
        var target = new SchemaCompareDatabaseEndpoint("Data Source=ignored;Initial Catalog=tgt;Integrated Security=True");
        return new SchemaComparison(source, target);
    }

    private static void Apply(string xml, SchemaComparison comparison, ILogger? logger = null)
    {
        var doc = XDocument.Parse(xml);
        LegacyScmpReader.Apply(doc, scmpPath: "test.scmp", comparison, logger ?? NullLogger.Instance);
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void Apply_SetsBoolOption_FromConfigurationOptionsElement()
    {
        var xml =
            """
            <SchemaComparison>
              <SchemaCompareSettingsService>
                <ConfigurationOptionsElement>
                  <PropertyElementName><Name>BlockOnPossibleDataLoss</Name><Value>False</Value></PropertyElementName>
                </ConfigurationOptionsElement>
              </SchemaCompareSettingsService>
            </SchemaComparison>
            """;

        var comparison = NewComparison();
        comparison.Options.BlockOnPossibleDataLoss.ShouldBeTrue(customMessage: "DacFx default for BlockOnPossibleDataLoss is true.");

        Apply(xml, comparison);

        comparison.Options.BlockOnPossibleDataLoss.ShouldBeFalse();
    }

    [Fact]
    public void Apply_CoercesIntOption()
    {
        var xml =
            """
            <SchemaComparison>
              <SchemaCompareSettingsService>
                <ConfigurationOptionsElement>
                  <PropertyElementName><Name>CommandTimeout</Name><Value>120</Value></PropertyElementName>
                </ConfigurationOptionsElement>
              </SchemaCompareSettingsService>
            </SchemaComparison>
            """;

        var comparison = NewComparison();
        Apply(xml, comparison);

        comparison.Options.CommandTimeout.ShouldBe(120);
    }

    [Fact]
    public void Apply_SetsStringOption()
    {
        var xml =
            """
            <SchemaComparison>
              <SchemaCompareSettingsService>
                <ConfigurationOptionsElement>
                  <PropertyElementName><Name>AdditionalDeploymentContributors</Name><Value>Foo.Bar.Contributor</Value></PropertyElementName>
                </ConfigurationOptionsElement>
              </SchemaCompareSettingsService>
            </SchemaComparison>
            """;

        var comparison = NewComparison();
        Apply(xml, comparison);

        comparison.Options.AdditionalDeploymentContributors.ShouldBe("Foo.Bar.Contributor");
    }

    [Fact]
    public void Apply_UnknownOptionName_LogsAndSkips()
    {
        // Two entries: one UI-only key, one real key. The real key still applies; the UI-only one is logged + skipped.
        var xml =
            """
            <SchemaComparison>
              <SchemaCompareSettingsService>
                <ConfigurationOptionsElement>
                  <PropertyElementName><Name>SchemaCompareIncludeCompositeObjects</Name><Value>False</Value></PropertyElementName>
                  <PropertyElementName><Name>CommandTimeout</Name><Value>200</Value></PropertyElementName>
                </ConfigurationOptionsElement>
              </SchemaCompareSettingsService>
            </SchemaComparison>
            """;

        var comparison = NewComparison();
        var logger = new ListLogger();

        Apply(xml, comparison, logger);

        comparison.Options.CommandTimeout.ShouldBe(200);
        logger.Entries.ShouldContain(
            e => e.Level == LogLevel.Warning && e.Message.Contains("SchemaCompareIncludeCompositeObjects"));
    }

    [Fact]
    public void Apply_UnparsableOptionValue_Throws()
    {
        var xml =
            """
            <SchemaComparison>
              <SchemaCompareSettingsService>
                <ConfigurationOptionsElement>
                  <PropertyElementName><Name>CommandTimeout</Name><Value>not-a-number</Value></PropertyElementName>
                </ConfigurationOptionsElement>
              </SchemaCompareSettingsService>
            </SchemaComparison>
            """;

        var comparison = NewComparison();
        var ex = Should.Throw<SchemaSyncException>(() => Apply(xml, comparison));
        ex.Message.ShouldContain("CommandTimeout");
        ex.Message.ShouldContain("not-a-number");
    }

    [Fact]
    public void Apply_AddsExcludedSourceObject_FromSelectedItem()
    {
        var xml =
            """
            <SchemaComparison>
              <ExcludedSourceElements>
                <SelectedItem Type="Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlUser">
                  <Name>dbo</Name>
                  <Name>app_user</Name>
                </SelectedItem>
              </ExcludedSourceElements>
            </SchemaComparison>
            """;

        var comparison = NewComparison();
        Apply(xml, comparison);

        comparison.ExcludedSourceObjects.Count.ShouldBe(1);
        var exclusion = comparison.ExcludedSourceObjects[0];
        exclusion.TypeName.ShouldBe("Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlUser");
        exclusion.Identifier.Parts.ShouldBe(["dbo", "app_user"]);
        comparison.ExcludedTargetObjects.Count.ShouldBe(0);
    }

    [Fact]
    public void Apply_AddsExcludedTargetObject_Independent()
    {
        var xml =
            """
            <SchemaComparison>
              <ExcludedSourceElements>
                <SelectedItem Type="Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlUser">
                  <Name>dbo</Name>
                  <Name>src_only</Name>
                </SelectedItem>
              </ExcludedSourceElements>
              <ExcludedTargetElements>
                <SelectedItem Type="Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlRole">
                  <Name>tgt_only</Name>
                </SelectedItem>
              </ExcludedTargetElements>
            </SchemaComparison>
            """;

        var comparison = NewComparison();
        Apply(xml, comparison);

        comparison.ExcludedSourceObjects.Count.ShouldBe(1);
        comparison.ExcludedSourceObjects[0].TypeName.ShouldBe("Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlUser");
        comparison.ExcludedSourceObjects[0].Identifier.Parts.ShouldBe(["dbo", "src_only"]);

        comparison.ExcludedTargetObjects.Count.ShouldBe(1);
        comparison.ExcludedTargetObjects[0].TypeName.ShouldBe("Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlRole");
        comparison.ExcludedTargetObjects[0].Identifier.Parts.ShouldBe(["tgt_only"]);
    }

    [Fact]
    public void Apply_FiltersEmptyNameElements_OnExclusion()
    {
        // DacFx's SchemaCompareElementId serialiser emits an empty <Name/> when the object is
        // identified only by its parent. The reader must drop the empty entry so the ObjectIdentifier
        // gets a parts-less identity rather than a list containing [""].
        var xml =
            """
            <SchemaComparison>
              <ExcludedSourceElements>
                <SelectedItem Type="Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlColumn">
                  <Name></Name>
                  <ParentItem Type="Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlTable">
                    <Name>dbo</Name>
                    <Name>Orders</Name>
                  </ParentItem>
                </SelectedItem>
              </ExcludedSourceElements>
            </SchemaComparison>
            """;

        var comparison = NewComparison();
        Apply(xml, comparison);

        comparison.ExcludedSourceObjects.Count.ShouldBe(1);
        comparison.ExcludedSourceObjects[0].Identifier.Parts.Count.ShouldBe(0);
        comparison.ExcludedSourceObjects[0].ParentIdentifier.Parts.ShouldBe(["dbo", "Orders"]);
    }

    [Fact]
    public void Apply_IgnoresAssemblyQualifiedSuffix_OnType()
    {
        var xml =
            """
            <SchemaComparison>
              <ExcludedSourceElements>
                <SelectedItem Type="Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlTable, Microsoft.Data.Tools.Schema.Sql, Version=17.0.0.0, Culture=neutral, PublicKeyToken=89845dcd8080cc91">
                  <Name>dbo</Name>
                  <Name>Orders</Name>
                </SelectedItem>
              </ExcludedSourceElements>
            </SchemaComparison>
            """;

        var comparison = NewComparison();
        Apply(xml, comparison);

        comparison.ExcludedSourceObjects[0].TypeName.ShouldBe("Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlTable");
    }

    [Fact]
    public void Apply_LegacyTypeExclusion_AddsToExcludeObjectTypes()
    {
        // Real legacy SSDT shape for type-level exclusion: <Value>ExcludedType</Value>.
        // SqlUser → ObjectType.Users, SqlRole → ObjectType.DatabaseRoles.
        var xml =
            """
            <SchemaComparison>
              <SchemaCompareSettingsService>
                <ConfigurationOptionsElement>
                  <PropertyElementName><Name>Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlUser</Name><Value>ExcludedType</Value></PropertyElementName>
                  <PropertyElementName><Name>Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlRole</Name><Value>ExcludedType</Value></PropertyElementName>
                </ConfigurationOptionsElement>
              </SchemaCompareSettingsService>
            </SchemaComparison>
            """;

        var comparison = NewComparison();
        Apply(xml, comparison);

        comparison.Options.ExcludeObjectTypes.ShouldContain(ObjectType.Users);
        comparison.Options.ExcludeObjectTypes.ShouldContain(ObjectType.DatabaseRoles);
    }

    [Fact]
    public void Apply_LegacyTypeExclusion_UnknownTypeName_LogsAndSkips()
    {
        var xml =
            """
            <SchemaComparison>
              <SchemaCompareSettingsService>
                <ConfigurationOptionsElement>
                  <PropertyElementName><Name>Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlBogusType</Name><Value>ExcludedType</Value></PropertyElementName>
                </ConfigurationOptionsElement>
              </SchemaCompareSettingsService>
            </SchemaComparison>
            """;

        var comparison = NewComparison();
        var beforeCount = comparison.Options.ExcludeObjectTypes?.Length ?? 0;
        var logger = new ListLogger();

        Apply(xml, comparison, logger);

        (comparison.Options.ExcludeObjectTypes?.Length ?? 0).ShouldBe(beforeCount);
        logger.Entries.ShouldContain(
            e => e.Level == LogLevel.Warning && e.Message.Contains("SqlBogusType"));
    }

    [Fact]
    public void Apply_LegacyTypeExclusion_DoesNotDuplicate()
    {
        var xml =
            """
            <SchemaComparison>
              <SchemaCompareSettingsService>
                <ConfigurationOptionsElement>
                  <PropertyElementName><Name>Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlUser</Name><Value>ExcludedType</Value></PropertyElementName>
                  <PropertyElementName><Name>Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlUser</Name><Value>ExcludedType</Value></PropertyElementName>
                </ConfigurationOptionsElement>
              </SchemaCompareSettingsService>
            </SchemaComparison>
            """;

        var comparison = NewComparison();
        Apply(xml, comparison);

        comparison.Options.ExcludeObjectTypes
            .Count(t => t == ObjectType.Users)
            .ShouldBe(1);
    }

    [Fact]
    public void Apply_LegacyDoNotDropFlag_True_AddsToDoNotDropObjectTypes()
    {
        // Older VS-SSDT shape: one DoNotDropXxx=True flag per type, instead of the consolidated
        // DoNotDropTypes semicolon string.
        var xml =
            """
            <SchemaComparison>
              <SchemaCompareSettingsService>
                <ConfigurationOptionsElement>
                  <PropertyElementName><Name>DoNotDropUsers</Name><Value>True</Value></PropertyElementName>
                  <PropertyElementName><Name>DoNotDropDatabaseRoles</Name><Value>True</Value></PropertyElementName>
                </ConfigurationOptionsElement>
              </SchemaCompareSettingsService>
            </SchemaComparison>
            """;

        var comparison = NewComparison();
        Apply(xml, comparison);

        comparison.Options.DoNotDropObjectTypes.ShouldContain(ObjectType.Users);
        comparison.Options.DoNotDropObjectTypes.ShouldContain(ObjectType.DatabaseRoles);
    }

    [Fact]
    public void Apply_LegacyDoNotDropFlag_False_IsRecognisedButNoOp()
    {
        // False = implicit default. Must NOT add to DoNotDropObjectTypes and must NOT emit a warning.
        var xml =
            """
            <SchemaComparison>
              <SchemaCompareSettingsService>
                <ConfigurationOptionsElement>
                  <PropertyElementName><Name>DoNotDropUsers</Name><Value>False</Value></PropertyElementName>
                </ConfigurationOptionsElement>
              </SchemaCompareSettingsService>
            </SchemaComparison>
            """;

        var comparison = NewComparison();
        var logger = new ListLogger();
        var beforeCount = comparison.Options.DoNotDropObjectTypes?.Length ?? 0;

        Apply(xml, comparison, logger);

        (comparison.Options.DoNotDropObjectTypes?.Length ?? 0).ShouldBe(beforeCount);
        logger.Entries.ShouldNotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Apply_LegacyExcludeFlag_True_AddsToExcludeObjectTypes()
    {
        var xml =
            """
            <SchemaComparison>
              <SchemaCompareSettingsService>
                <ConfigurationOptionsElement>
                  <PropertyElementName><Name>ExcludeUsers</Name><Value>True</Value></PropertyElementName>
                </ConfigurationOptionsElement>
              </SchemaCompareSettingsService>
            </SchemaComparison>
            """;

        var comparison = NewComparison();
        Apply(xml, comparison);

        comparison.Options.ExcludeObjectTypes.ShouldContain(ObjectType.Users);
    }

    [Fact]
    public void Apply_LegacyDoNotDropTypes_SemicolonList_AddsEachToDoNotDropObjectTypes()
    {
        // Newer VS-SSDT shape: consolidated semicolon-delimited CLR FullName list.
        var xml =
            """
            <SchemaComparison>
              <SchemaCompareSettingsService>
                <ConfigurationOptionsElement>
                  <PropertyElementName><Name>DoNotDropTypes</Name><Value>Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlUser;Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlRole</Value></PropertyElementName>
                </ConfigurationOptionsElement>
              </SchemaCompareSettingsService>
            </SchemaComparison>
            """;

        var comparison = NewComparison();
        Apply(xml, comparison);

        comparison.Options.DoNotDropObjectTypes.ShouldContain(ObjectType.Users);
        comparison.Options.DoNotDropObjectTypes.ShouldContain(ObjectType.DatabaseRoles);
    }

    [Fact]
    public void Apply_KnownIgnoredLegacyName_SilentlySkipped()
    {
        // PlanGenerationType, TargetConnectionString, TargetDatabaseName, AllowExistingModelErrors:
        // none are DacDeployOptions properties — but they appear in real legacy .scmp files. Must not warn.
        var xml =
            """
            <SchemaComparison>
              <SchemaCompareSettingsService>
                <ConfigurationOptionsElement>
                  <PropertyElementName><Name>PlanGenerationType</Name><Value>Default</Value></PropertyElementName>
                  <PropertyElementName><Name>TargetConnectionString</Name><Value>Data Source=foo</Value></PropertyElementName>
                  <PropertyElementName><Name>TargetDatabaseName</Name><Value>Bar</Value></PropertyElementName>
                  <PropertyElementName><Name>AllowExistingModelErrors</Name><Value>True</Value></PropertyElementName>
                </ConfigurationOptionsElement>
              </SchemaCompareSettingsService>
            </SchemaComparison>
            """;

        var comparison = NewComparison();
        var logger = new ListLogger();

        Apply(xml, comparison, logger);

        logger.Entries.ShouldNotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Apply_OptionNameLookupIsCaseInsensitive()
    {
        // Legacy SSDT casing has drifted: CLRTypes vs ClrTypes. The reflection lookup must match either way.
        // DacDeployOptions exposes `NoAlterStatementsToChangeClrTypes` (lowercase lr); real .scmp uses CLR.
        var xml =
            """
            <SchemaComparison>
              <SchemaCompareSettingsService>
                <ConfigurationOptionsElement>
                  <PropertyElementName><Name>NoAlterStatementsToChangeCLRTypes</Name><Value>True</Value></PropertyElementName>
                </ConfigurationOptionsElement>
              </SchemaCompareSettingsService>
            </SchemaComparison>
            """;

        var comparison = NewComparison();
        var logger = new ListLogger();

        Apply(xml, comparison, logger);

        comparison.Options.NoAlterStatementsToChangeClrTypes.ShouldBeTrue();
        logger.Entries.ShouldNotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Apply_NoConfigurationOptionsElement_LeavesOptionsUntouched()
    {
        var xml =
            """
            <SchemaComparison>
              <Version>10</Version>
            </SchemaComparison>
            """;

        var comparison = NewComparison();
        var originalTimeout = comparison.Options.CommandTimeout;
        var originalBlock = comparison.Options.BlockOnPossibleDataLoss;

        Apply(xml, comparison);

        comparison.Options.CommandTimeout.ShouldBe(originalTimeout);
        comparison.Options.BlockOnPossibleDataLoss.ShouldBe(originalBlock);
        comparison.ExcludedSourceObjects.Count.ShouldBe(0);
        comparison.ExcludedTargetObjects.Count.ShouldBe(0);
    }
}
