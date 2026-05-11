using System.CommandLine;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.Dac;
using SqlProjectSync;

var scmpPathArg = new Argument<string>("scmp-path")
{
    Description = "Path to the .scmp file describing source endpoint + target project.",
};

var previewOption = new Option<bool>("--preview")
{
    Description = "Show projected changes without writing to disk.",
};

var verbosityOption = new Option<LogLevel>("--verbosity", "-v")
{
    Description = "Logging verbosity (Trace, Debug, Information, Warning, Error, Critical, None).",
    DefaultValueFactory = _ => LogLevel.Information,
};

var folderStructureOption = new Option<DacExtractTarget>("--folder-structure")
{
    Description = "Folder layout DacFx uses when writing project files.",
    DefaultValueFactory = _ => DacExtractTarget.SchemaObjectType,
};

var syncCommand = new Command("sync", "Sync a SQL Server database schema into a .sqlproj.")
{
    scmpPathArg,
    previewOption,
    verbosityOption,
    folderStructureOption,
};

syncCommand.SetAction(parseResult =>
{
    var scmpPath = parseResult.GetValue(scmpPathArg)!;
    var preview = parseResult.GetValue(previewOption);
    var verbosity = parseResult.GetValue(verbosityOption);
    var folderStructure = parseResult.GetValue(folderStructureOption);

    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder
            .AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            })
            .SetMinimumLevel(verbosity);
    });

    var logger = loggerFactory.CreateLogger("sqlproj-sync");

    try
    {
        var options = new SyncOptions { FolderStructure = folderStructure };
        var comparison = SchemaSync.Compare(scmpPath, options, logger);

        if (!comparison.IsValid)
        {
            logger.LogError("Comparison reported invalid state for '{ScmpPath}'.", scmpPath);
            return 2;
        }

        if (comparison.IsEqual)
        {
            Console.WriteLine("No differences. Source and target are equal.");
            return 0;
        }

        var publish = SchemaSync.Apply(comparison, preview, logger);

        if (publish.IsPreview)
        {
            Console.WriteLine($"Preview: {publish.PreviewDifferences.Count} difference(s).");
            foreach (var diff in publish.PreviewDifferences)
            {
                Console.WriteLine($"  [{diff.UpdateAction}] {DescribeDifference(diff)}");
            }
        }
        else
        {
            Console.WriteLine($"Added:   {publish.AddedFiles.Count}");
            Console.WriteLine($"Deleted: {publish.DeletedFiles.Count}");
            Console.WriteLine($"Changed: {publish.ChangedFiles.Count}");
            foreach (var f in publish.AddedFiles)
            {
                Console.WriteLine($"  + {f}");
            }
            foreach (var f in publish.DeletedFiles)
            {
                Console.WriteLine($"  - {f}");
            }
            foreach (var f in publish.ChangedFiles)
            {
                Console.WriteLine($"  ~ {f}");
            }
        }

        return 0;
    }
    catch (SchemaSyncException ex)
    {
        logger.LogError(ex, "Schema sync failed: {Message}", ex.Message);
        return 1;
    }
});

var rootCommand = new RootCommand("Pulls a SQL Server database schema into a .sqlproj via DacFx.")
{
    syncCommand,
};

return rootCommand.Parse(args).Invoke();

static string DescribeDifference(Microsoft.SqlServer.Dac.Compare.SchemaDifference diff)
{
    var source = diff.SourceObject?.Name?.ToString();
    var target = diff.TargetObject?.Name?.ToString();
    return source ?? target ?? "<unknown>";
}
