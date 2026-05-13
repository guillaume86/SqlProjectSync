using System.CommandLine;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SqlProjectSync;

var scmpPathArg = new Argument<string>("scmp-path")
{
    Description = "Path to the .scmp describing the source DB and target .sqlproj.",
};

var applyOption = new Option<bool>("--apply")
{
    Description = "Actually write changes. Default is --preview (no writes).",
};

var verbosityOption = new Option<LogLevel>("--verbosity", "-v")
{
    Description = "Logging verbosity. Default Debug.",
    DefaultValueFactory = _ => LogLevel.Debug,
};

var root = new RootCommand("Reproduce SqlProjectSync against a real-world .sqlproj.")
{
    scmpPathArg,
    applyOption,
    verbosityOption,
};

root.SetAction(parse =>
{
    var scmpPath = parse.GetValue(scmpPathArg)!;
    var apply = parse.GetValue(applyOption);
    var verbosity = parse.GetValue(verbosityOption);

    using var loggerFactory = LoggerFactory.Create(b => b
        .AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        })
        .SetMinimumLevel(verbosity));

    var logger = loggerFactory.CreateLogger("HrWebDatabase.Repro");

    var sw = Stopwatch.StartNew();
    try
    {
        var result = SchemaSync.Compare(scmpPath, logger: logger);
        if (!result.IsValid)
        {
            logger.LogError("Comparison reported invalid state.");
            return 2;
        }

        if (result.IsEqual)
        {
            logger.LogInformation("No differences. Source and target are equal.");
            return 0;
        }

        var publish = SchemaSync.Apply(result, preview: !apply, logger: logger);
        if (publish.IsPreview)
        {
            logger.LogInformation("Preview: {Count} difference(s). Rerun with --apply to write.", publish.PreviewDifferences.Count);
        }

        return 0;
    }
    catch (SchemaSyncException ex)
    {
        logger.LogError(ex, "Schema sync failed.");
        return 1;
    }
    finally
    {
        sw.Stop();
        logger.LogInformation("Done in {Elapsed}.", sw.Elapsed);
    }
});

return root.Parse(args).Invoke();
