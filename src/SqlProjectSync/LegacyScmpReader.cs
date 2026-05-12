using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Compare;
using Microsoft.SqlServer.Dac.Model;

namespace SqlProjectSync;

internal static class LegacyScmpReader
{
    // Ported from DacFx's internal `AddObjectTypeMapEntry` calls (Microsoft.SqlServer.Dac.dll).
    // Legacy SSDT writes type-level exclusions as <PropertyElementName><Name>{CLR FullName}</Name><Value>ExcludedType</Value></PropertyElementName>
    // inside <ConfigurationOptionsElement>. Those don't set DacDeployOptions properties — they
    // contribute entries to DacDeployOptions.ExcludeObjectTypes.
    private static readonly Dictionary<string, ObjectType> LegacyTypeNameToObjectType = new(StringComparer.Ordinal)
    {
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlAggregate"] = ObjectType.Aggregates,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlApplicationRole"] = ObjectType.ApplicationRoles,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlAssembly"] = ObjectType.Assemblies,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlAssemblyFile"] = ObjectType.AssemblyFiles,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlAsymmetricKey"] = ObjectType.AsymmetricKeys,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlServerAudit"] = ObjectType.Audits,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlBrokerPriority"] = ObjectType.BrokerPriorities,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlCertificate"] = ObjectType.Certificates,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlUserDefinedType"] = ObjectType.ClrUserDefinedTypes,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlColumnEncryptionKey"] = ObjectType.ColumnEncryptionKeys,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlColumnMasterKey"] = ObjectType.ColumnMasterKeys,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlContract"] = ObjectType.Contracts,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlCredential"] = ObjectType.Credentials,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlCryptographicProvider"] = ObjectType.CryptographicProviders,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlDatabaseAuditSpecification"] = ObjectType.DatabaseAuditSpecifications,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlDatabaseCredential"] = ObjectType.DatabaseScopedCredentials,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlDatabaseEncryptionKey"] = ObjectType.DatabaseEncryptionKeys,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlDatabaseOptions"] = ObjectType.DatabaseOptions,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlRole"] = ObjectType.DatabaseRoles,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlDatabaseDdlTrigger"] = ObjectType.DatabaseTriggers,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlDefault"] = ObjectType.Defaults,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlEndpoint"] = ObjectType.Endpoints,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlErrorMessage"] = ObjectType.ErrorMessages,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlServerEventNotification"] = ObjectType.EventNotifications,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlDatabaseEventNotification"] = ObjectType.EventNotifications,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlQueueEventNotification"] = ObjectType.EventNotifications,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlEventNotification"] = ObjectType.EventNotifications,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlEventSession"] = ObjectType.EventSessions,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlDatabaseEventSession"] = ObjectType.EventSessions,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlExtendedProperty"] = ObjectType.ExtendedProperties,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlExternalDataSource"] = ObjectType.ExternalDataSources,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlExternalFileFormat"] = ObjectType.ExternalFileFormats,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlExternalLanguage"] = ObjectType.ExternalLanguages,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlExternalLibrary"] = ObjectType.ExternalLibraries,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlExternalModel"] = ObjectType.ExternalModels,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlExternalTable"] = ObjectType.ExternalTables,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlExternalStream"] = ObjectType.ExternalStreams,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlExternalStreamingJob"] = ObjectType.ExternalStreamingJobs,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlFilegroup"] = ObjectType.Filegroups,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlFile"] = ObjectType.Files,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlFileTable"] = ObjectType.FileTables,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlFullTextCatalog"] = ObjectType.FullTextCatalogs,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlFullTextStopList"] = ObjectType.FullTextStoplists,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlInlineTableValuedFunction"] = ObjectType.TableValuedFunctions,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlMultiStatementTableValuedFunction"] = ObjectType.TableValuedFunctions,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlLinkedServerLogin"] = ObjectType.LinkedServerLogins,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlLinkedServer"] = ObjectType.LinkedServers,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlLogin"] = ObjectType.Logins,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlMasterKey"] = ObjectType.MasterKeys,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlMessageType"] = ObjectType.MessageTypes,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlPartitionFunction"] = ObjectType.PartitionFunctions,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlPartitionScheme"] = ObjectType.PartitionSchemes,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlPermissionStatement"] = ObjectType.Permissions,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlQueue"] = ObjectType.Queues,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlRemoteServiceBinding"] = ObjectType.RemoteServiceBindings,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlRoleMembership"] = ObjectType.RoleMembership,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlRoute"] = ObjectType.Routes,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlRule"] = ObjectType.Rules,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlScalarFunction"] = ObjectType.ScalarValuedFunctions,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlSearchPropertyList"] = ObjectType.SearchPropertyLists,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlSecurityPolicy"] = ObjectType.SecurityPolicies,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlSequence"] = ObjectType.Sequences,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlServerAuditSpecification"] = ObjectType.ServerAuditSpecifications,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlServerRoleMembership"] = ObjectType.ServerRoleMembership,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlUserDefinedServerRole"] = ObjectType.ServerRoles,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlServerDdlTrigger"] = ObjectType.ServerTriggers,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlService"] = ObjectType.Services,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlSignature"] = ObjectType.Signatures,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlProcedure"] = ObjectType.StoredProcedures,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlSymmetricKey"] = ObjectType.SymmetricKeys,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlSynonym"] = ObjectType.Synonyms,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlTable"] = ObjectType.Tables,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlUserDefinedDataType"] = ObjectType.UserDefinedDataTypes,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlTableType"] = ObjectType.UserDefinedTableTypes,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlUser"] = ObjectType.Users,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlView"] = ObjectType.Views,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlDatabaseWorkloadGroup"] = ObjectType.DatabaseWorkloadGroups,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlWorkloadClassifier"] = ObjectType.WorkloadClassifiers,
        ["Microsoft.Data.Tools.Schema.Sql.SchemaModel.SqlXmlSchemaCollection"] = ObjectType.XmlSchemaCollections,
    };

    // Names written by legacy SSDT into <ConfigurationOptionsElement> that don't map to a
    // schema-compare option in the modern API and should not produce a warning:
    //  - PlanGenerationType: a type-tag marker emitted by SqlDeploymentOptions.Serialize.
    //  - TargetConnectionString / TargetDatabaseName: belong to publish-profile / target-endpoint,
    //    not DacDeployOptions. Our flow targets a .sqlproj — these are irrelevant.
    //  - AllowExistingModelErrors: obsolete option, removed from DacDeployOptions in 170.x.
    private static readonly HashSet<string> KnownIgnoredLegacyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "PlanGenerationType",
        "TargetConnectionString",
        "TargetDatabaseName",
        "AllowExistingModelErrors",
    };

    public static void Apply(XDocument doc, string scmpPath, SchemaComparison comparison, ILogger logger)
    {
        var root = doc.Root
            ?? throw new SchemaSyncException($"'{scmpPath}' has no root element.");

        ReadOptions(root, scmpPath, comparison.Options, logger);
        ReadExclusions(root, scmpPath, "ExcludedSourceElements", comparison.ExcludedSourceObjects, logger);
        ReadExclusions(root, scmpPath, "ExcludedTargetElements", comparison.ExcludedTargetObjects, logger);
    }

    private static void ReadOptions(XElement root, string scmpPath, DacDeployOptions options, ILogger logger)
    {
        var config = root.Descendants()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "ConfigurationOptionsElement", StringComparison.Ordinal));
        if (config is null)
        {
            return;
        }

        // Case-insensitive: legacy SSDT casing has drifted across VS versions (e.g. CLRTypes vs ClrTypes).
        var properties = options.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in config.Elements()
                     .Where(e => string.Equals(e.Name.LocalName, "PropertyElementName", StringComparison.Ordinal)))
        {
            var name = ChildText(entry, "Name");
            var value = ChildText(entry, "Value");

            if (string.IsNullOrEmpty(name))
            {
                throw new SchemaSyncException(
                    $"'{scmpPath}' <ConfigurationOptionsElement> has a <PropertyElementName> with no <Name>.");
            }

            // Legacy SSDT type-level exclusions: <Value>ExcludedType</Value> means the entry is not an
            // option setter but a contribution to DacDeployOptions.ExcludeObjectTypes.
            if (string.Equals(value, "ExcludedType", StringComparison.Ordinal))
            {
                if (LegacyTypeNameToObjectType.TryGetValue(name, out var objectType))
                {
                    AddObjectType(options, objectType, isExclude: true);
                }
                else
                {
                    logger.LogWarning(
                        "Skipping unknown legacy type-exclusion '{Name}' in '{Scmp}'.",
                        name,
                        scmpPath);
                }

                continue;
            }

            // Legacy SSDT consolidated semicolon list: <Name>DoNotDropTypes</Name><Value>X;Y;Z</Value>
            // → adds each CLR FullName to DacDeployOptions.DoNotDropObjectTypes.
            if (string.Equals(name, "DoNotDropTypes", StringComparison.Ordinal))
            {
                foreach (var typeName in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (LegacyTypeNameToObjectType.TryGetValue(typeName, out var objectType))
                    {
                        AddObjectType(options, objectType, isExclude: false);
                    }
                    else
                    {
                        logger.LogWarning(
                            "Skipping unknown legacy DoNotDropTypes entry '{TypeName}' in '{Scmp}'.",
                            typeName,
                            scmpPath);
                    }
                }

                continue;
            }

            // Legacy SSDT per-type flags: <Name>DoNotDropXxx</Name> or <Name>ExcludeXxx</Name> where Xxx is
            // an ObjectType enum member. Only true flags contribute; false is recognised but no-op.
            if (TryHandleLegacyTypeFlag(options, name, value, prefix: "DoNotDrop", isExclude: false))
            {
                continue;
            }

            if (TryHandleLegacyTypeFlag(options, name, value, prefix: "Exclude", isExclude: true))
            {
                continue;
            }

            if (properties.TryGetValue(name, out var prop))
            {
                ApplyOption(options, prop, name, value, scmpPath);
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace("Applied legacy-scmp option {Name}={Value}", name, value);
                }
                continue;
            }

            if (KnownIgnoredLegacyNames.Contains(name))
            {
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace("Ignoring known non-option legacy-scmp entry {Name}", name);
                }
                continue;
            }

            logger.LogWarning(
                "Skipping unknown DacDeployOptions property '{Name}' in '{Scmp}' (UI-only or renamed).",
                name,
                scmpPath);
        }
    }

    private static void ApplyOption(DacDeployOptions options, PropertyInfo prop, string name, string value, string scmpPath)
    {
        object? typedValue;
        try
        {
            typedValue = Coerce(prop.PropertyType, value);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new SchemaSyncException(
                $"'{scmpPath}' cannot coerce DacDeployOptions.{name} value '{value}' to {prop.PropertyType.Name}: {ex.Message}",
                ex);
        }

        prop.SetValue(options, typedValue);
    }

    private static bool TryHandleLegacyTypeFlag(
        DacDeployOptions options,
        string name,
        string value,
        string prefix,
        bool isExclude)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = name[prefix.Length..];
        if (suffix.Length == 0 || !Enum.TryParse(suffix, ignoreCase: false, out ObjectType candidate))
        {
            return false;
        }

        if (!Enum.IsDefined(candidate))
        {
            return false;
        }

        // Flag is recognised. Only `True` contributes; `False` is the implicit default and a no-op.
        if (bool.TryParse(value, out var flag) && flag)
        {
            AddObjectType(options, candidate, isExclude);
        }

        return true;
    }

    private static void ReadExclusions(
        XElement root,
        string scmpPath,
        string containerLocalName,
        IList<SchemaComparisonExcludedObjectId> target,
        ILogger logger)
    {
        var container = root.Descendants()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, containerLocalName, StringComparison.Ordinal));
        if (container is null)
        {
            return;
        }

        foreach (var item in container.Elements()
                     .Where(e => string.Equals(e.Name.LocalName, "SelectedItem", StringComparison.Ordinal)))
        {
            var typeName = ReadTypeAttribute(item, scmpPath, containerLocalName);
            var parts = ReadNameParts(item);

            var parent = item.Elements()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, "ParentItem", StringComparison.Ordinal));

            SchemaComparisonExcludedObjectId exclusion;
            if (parent is not null)
            {
                var parentTypeName = ReadTypeAttribute(parent, scmpPath, containerLocalName);
                var parentParts = ReadNameParts(parent);
                exclusion = new SchemaComparisonExcludedObjectId(
                    typeName,
                    new ObjectIdentifier(parts),
                    parentTypeName,
                    new ObjectIdentifier(parentParts));
            }
            else
            {
                exclusion = new SchemaComparisonExcludedObjectId(typeName, new ObjectIdentifier(parts));
            }

            target.Add(exclusion);

            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace(
                    "Added legacy-scmp exclusion {Container} {Type}({Parts})",
                    containerLocalName,
                    typeName,
                    string.Join(".", parts));
            }
        }
    }

    private static string ReadTypeAttribute(XElement element, string scmpPath, string containerLocalName)
    {
        var raw = element.Attribute("Type")?.Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new SchemaSyncException(
                $"'{scmpPath}' <{containerLocalName}> has a <{element.Name.LocalName}> with no Type attribute.");
        }

        // DacFx strips everything after the first comma (assembly-qualified suffix).
        var comma = raw.IndexOf(',');
        return comma < 0 ? raw.Trim() : raw[..comma].Trim();
    }

    private static List<string> ReadNameParts(XElement element)
    {
        // Match DacFx's SchemaCompareElementId.ReadTypeAndNamePartsFromXmlDoc: skip empty <Name/>
        // children. They serialise as a placeholder when the object is identified only by its parent.
        return element.Elements()
            .Where(e => string.Equals(e.Name.LocalName, "Name", StringComparison.Ordinal))
            .Select(e => e.Value)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }

    private static string ChildText(XElement parent, string localName)
    {
        return parent.Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal))
            ?.Value
            ?? string.Empty;
    }

    private static void AddObjectType(DacDeployOptions options, ObjectType objectType, bool isExclude)
    {
        var current = (isExclude ? options.ExcludeObjectTypes : options.DoNotDropObjectTypes) ?? [];
        if (Array.IndexOf(current, objectType) >= 0)
        {
            return;
        }

        var combined = new ObjectType[current.Length + 1];
        Array.Copy(current, combined, current.Length);
        combined[^1] = objectType;

        if (isExclude)
        {
            options.ExcludeObjectTypes = combined;
        }
        else
        {
            options.DoNotDropObjectTypes = combined;
        }
    }

    private static object? Coerce(Type targetType, string raw)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlying == typeof(string))
        {
            return raw;
        }

        if (underlying == typeof(bool))
        {
            return bool.Parse(raw);
        }

        if (underlying == typeof(int))
        {
            return int.Parse(raw, CultureInfo.InvariantCulture);
        }

        if (underlying == typeof(long))
        {
            return long.Parse(raw, CultureInfo.InvariantCulture);
        }

        if (underlying.IsEnum)
        {
            return Enum.Parse(underlying, raw, ignoreCase: true);
        }

        return Convert.ChangeType(raw, underlying, CultureInfo.InvariantCulture);
    }
}
