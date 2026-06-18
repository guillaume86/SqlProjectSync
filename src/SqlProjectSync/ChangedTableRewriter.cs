using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.Dac.Compare;
using Microsoft.SqlServer.Dac.Model;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlProjectSync;

/// <summary>
/// Workaround for a <see cref="SchemaComparisonResult.PublishChangesToProject(string, Microsoft.SqlServer.Dac.DacExtractTarget)"/>
/// crash (<c>startIndex ('-1') must be a non-negative value</c>) on table changes.
///
/// <para>
/// When a table-level <c>Change</c> difference has object children that live in
/// the same <c>.sql</c> file (a renamed/retyped column, a dropped column, an
/// added/removed constraint, a new index), DacFx processes the children first —
/// rewriting the file in place — and then tries to locate the table's
/// <em>original</em> script in the now-modified file to splice in the new
/// definition. The lookup (<c>File.ReadAllText(...).IndexOf(originalScript)</c>)
/// returns <c>-1</c> because the children already changed those bytes, and the
/// subsequent <c>Substring(-1, …)</c> throws. The whole publish then fails with
/// no files written, so the change can't be applied at all.
/// </para>
///
/// <para>
/// We take those table diffs over ourselves: exclude them from the DacFx publish
/// and rewrite the file directly. The table's <c>CREATE TABLE</c> statement is
/// spliced by offset with the freshly-scripted source definition (which already
/// carries every inline column and constraint change), while standalone children
/// that aren't part of <c>CREATE TABLE</c> — indexes, triggers — are added,
/// removed, or replaced separately. Statements the comparison didn't touch (an
/// unchanged index or trigger in the same file) are preserved byte-for-byte.
/// </para>
/// </summary>
internal static partial class ChangedTableRewriter
{
    internal enum StandaloneAction
    {
        Add,
        Delete,
        Change,
    }

    /// <summary>A standalone (non-inline) child object of a changed table: an index or trigger.</summary>
    internal sealed record StandaloneOp(StandaloneAction Action, string ObjectName, string? Script);

    /// <summary>A planned rewrite of a single table's <c>.sql</c> file, captured before the DacFx publish runs.</summary>
    internal sealed record PlannedRewrite(
        string FilePath,
        string Schema,
        string Table,
        string TableScript,
        IReadOnlyList<StandaloneOp> Standalones);

    /// <summary>
    /// Scans the comparison for crash-prone table-<c>Change</c> diffs, captures
    /// a rewrite plan for each (table source script + standalone child ops), and
    /// excludes them from the comparison so the subsequent DacFx publish skips
    /// them. Diffs that can't be excluded, or whose file can't be resolved, are
    /// left to DacFx untouched.
    /// </summary>
    public static IReadOnlyList<PlannedRewrite> Plan(SchemaComparisonResult result, ILogger logger)
    {
        var plans = new List<PlannedRewrite>();

        foreach (var diff in result.Differences.ToList())
        {
            if (!diff.Included
                || diff.UpdateAction != SchemaUpdateAction.Change
                || diff.TargetObject is null
                || diff.TargetObject.ObjectType != Table.TypeClass)
            {
                continue;
            }

            var childObjects = diff.Children
                .Where(c => c.DifferenceType == SchemaDifferenceType.Object)
                .ToList();

            // The crash needs a child that edits text *inside* the CREATE TABLE
            // span before the parent's whole-table replace — i.e. an inline
            // column/constraint being dropped or changed. A table whose only
            // object children are pure adds (DacFx appends those) doesn't crash,
            // so leave it on DacFx's path to avoid needlessly re-emitting it.
            if (!childObjects.Any(c => c.UpdateAction is SchemaUpdateAction.Delete or SchemaUpdateAction.Change))
            {
                continue;
            }

            var (schema, table) = SplitName(diff.TargetObject.Name);
            var tableName = $"{schema}.{table}";

            var file = diff.TargetObject.GetSourceInformation()?.SourceName;
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
            {
                LogSkipUnresolved(logger, tableName);
                continue;
            }

            var tableScript = result.GetDiffEntrySourceScript(diff);
            if (string.IsNullOrWhiteSpace(tableScript))
            {
                continue;
            }

            var standalones = new List<StandaloneOp>();
            foreach (var child in childObjects)
            {
                var op = ClassifyChild(result, child);
                if (op is not null)
                {
                    standalones.Add(op);
                }
            }

            if (!result.Exclude(diff))
            {
                // Not excludable — we can't take it over; leave it to DacFx.
                LogCannotExclude(logger, tableName);
                continue;
            }

            plans.Add(new PlannedRewrite(file, schema, table, tableScript, standalones));
            LogPlanned(logger, tableName, file, standalones.Count);
        }

        return plans;
    }

    /// <summary>
    /// Applies the planned rewrites to disk after the DacFx publish has run.
    /// Returns the set of files written. Each file is parsed once; the table's
    /// <c>CREATE TABLE</c> statement is replaced by offset and standalone child
    /// ops are layered on top, so untouched statements keep their bytes.
    /// </summary>
    public static IReadOnlyList<string> Execute(IReadOnlyList<PlannedRewrite> plans, ILogger logger)
    {
        var changed = new List<string>();

        foreach (var plan in plans)
        {
            try
            {
                if (RewriteFile(plan, logger))
                {
                    changed.Add(plan.FilePath);
                }
            }
            catch (Exception ex)
            {
                throw new SchemaSyncException(
                    $"Failed to rewrite changed table '{plan.Schema}.{plan.Table}' in '{plan.FilePath}': {ex.Message}",
                    ex);
            }
        }

        return changed;
    }

    /// <summary>
    /// Classifies a table-change child. Inline elements (columns, table
    /// constraints) come back from DacFx as an <c>ALTER TABLE … ADD …</c>
    /// script and are already folded into the table's source definition, so
    /// they're skipped. Standalone statements (<c>CREATE INDEX</c>,
    /// <c>CREATE TRIGGER</c>) are returned as ops to apply separately.
    /// </summary>
    private static StandaloneOp? ClassifyChild(SchemaComparisonResult result, SchemaDifference child)
    {
        var script = child.UpdateAction == SchemaUpdateAction.Delete
            ? result.GetDiffEntryTargetScript(child)
            : result.GetDiffEntrySourceScript(child);

        if (string.IsNullOrWhiteSpace(script))
        {
            // Inline element handled by the whole-table replace (empty script,
            // or a deleted inline column/constraint).
            return null;
        }

        var statement = ParseSingleStatement(script);
        if (statement is null or AlterTableStatement)
        {
            // ALTER TABLE … ADD column/constraint → already inline in the table
            // source script. Anything unparseable is left to the table replace.
            return null;
        }

        var name = (child.SourceObject ?? child.TargetObject)?.Name is { } id
            ? LastPart(id)
            : StandaloneName(statement);
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var action = child.UpdateAction switch
        {
            SchemaUpdateAction.Add => StandaloneAction.Add,
            SchemaUpdateAction.Delete => StandaloneAction.Delete,
            _ => StandaloneAction.Change,
        };
        var payload = child.UpdateAction == SchemaUpdateAction.Delete ? null : script;
        return new StandaloneOp(action, name, payload);
    }

    private static bool RewriteFile(PlannedRewrite plan, ILogger logger)
    {
        var original = File.ReadAllText(plan.FilePath);
        if (string.IsNullOrWhiteSpace(original))
        {
            return false;
        }

        var lineEnding = DetectLineEnding(original);
        var script = ParseScript(original);
        if (script is null)
        {
            throw new InvalidOperationException("file did not parse as T-SQL");
        }

        var create = FindCreateTable(script, plan.Schema, plan.Table)
            ?? throw new InvalidOperationException(
                $"CREATE TABLE for [{plan.Schema}].[{plan.Table}] not found in file");

        // Span replacements (highest offset first so earlier offsets stay valid).
        var edits = new List<(int Start, int Length, string Replacement)>
        {
            (create.StartOffset, create.FragmentLength, Normalize(plan.TableScript, lineEnding)),
        };

        var appends = new StringBuilder();
        foreach (var op in plan.Standalones)
        {
            switch (op.Action)
            {
                case StandaloneAction.Add:
                    AppendStatement(appends, op.Script!, lineEnding);
                    break;

                case StandaloneAction.Delete:
                {
                    var stmt = FindStandalone(script, op.ObjectName);
                    if (stmt is not null)
                    {
                        var (start, length) = RemovalSpan(original, stmt);
                        edits.Add((start, length, string.Empty));
                    }

                    break;
                }

                case StandaloneAction.Change:
                {
                    var stmt = FindStandalone(script, op.ObjectName);
                    if (stmt is not null)
                    {
                        edits.Add((stmt.StartOffset, stmt.FragmentLength, Normalize(op.Script!, lineEnding)));
                    }
                    else
                    {
                        AppendStatement(appends, op.Script!, lineEnding);
                    }

                    break;
                }
            }
        }

        var rewritten = original;
        foreach (var (start, length, replacement) in edits.OrderByDescending(e => e.Start))
        {
            rewritten = rewritten.Remove(start, length).Insert(start, replacement);
        }

        if (appends.Length > 0)
        {
            rewritten = EnsureTrailingGo(rewritten, lineEnding) + appends;
        }

        if (string.Equals(rewritten, original, StringComparison.Ordinal))
        {
            return false;
        }

        File.WriteAllText(plan.FilePath, rewritten);
        var tableName = $"{plan.Schema}.{plan.Table}";
        LogRewrote(logger, tableName, plan.FilePath);
        return true;
    }

    /// <summary>Normalizes a generated script: trims trailing whitespace and re-applies the file's line ending.</summary>
    private static string Normalize(string script, string lineEnding)
    {
        var text = script.Replace("\r\n", "\n").TrimEnd();
        return lineEnding == "\r\n" ? text.Replace("\n", "\r\n") : text;
    }

    /// <summary>Appends a standalone statement as its own batch, separated by a blank line and terminated with <c>GO</c>.</summary>
    private static void AppendStatement(StringBuilder appends, string script, string lineEnding)
    {
        appends.Append(lineEnding)
            .Append(lineEnding)
            .Append(Normalize(script, lineEnding))
            .Append(lineEnding)
            .Append("GO")
            .Append(lineEnding);
    }

    private static string EnsureTrailingGo(string text, string lineEnding)
    {
        var trimmed = text.TrimEnd();
        if (trimmed.EndsWith("GO", StringComparison.OrdinalIgnoreCase)
            && (trimmed.Length == 2 || char.IsWhiteSpace(trimmed[^3])))
        {
            return trimmed;
        }

        return trimmed + lineEnding + "GO";
    }

    private static TSqlScript? ParseScript(string text)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: false);
        using var reader = new StringReader(text);
        var fragment = parser.Parse(reader, out var errors);
        if (errors is { Count: > 0 })
        {
            return null;
        }

        return fragment as TSqlScript;
    }

    private static TSqlStatement? ParseSingleStatement(string text)
    {
        var script = ParseScript(text);
        return script?.Batches.SelectMany(b => b.Statements).FirstOrDefault();
    }

    private static CreateTableStatement? FindCreateTable(TSqlScript script, string schema, string table)
    {
        foreach (var statement in script.Batches.SelectMany(b => b.Statements))
        {
            if (statement is CreateTableStatement create
                && create.SchemaObjectName is { } name
                && SchemaMatches(name, schema)
                && string.Equals(name.BaseIdentifier?.Value, table, StringComparison.OrdinalIgnoreCase))
            {
                return create;
            }
        }

        return null;
    }

    /// <summary>Finds a standalone index or trigger statement by its simple (last-identifier) name.</summary>
    private static TSqlStatement? FindStandalone(TSqlScript script, string objectName)
    {
        foreach (var statement in script.Batches.SelectMany(b => b.Statements))
        {
            if (string.Equals(StandaloneName(statement), objectName, StringComparison.OrdinalIgnoreCase))
            {
                return statement;
            }
        }

        return null;
    }

    private static string? StandaloneName(TSqlStatement statement) => statement switch
    {
        CreateIndexStatement index => index.Name?.Value,
        IndexStatement index => index.Name?.Value,
        CreateTriggerStatement trigger => trigger.Name?.BaseIdentifier?.Value,
        _ => null,
    };

    private static bool SchemaMatches(SchemaObjectName name, string schema)
    {
        var actual = name.SchemaIdentifier?.Value ?? "dbo";
        return string.Equals(actual, schema, StringComparison.OrdinalIgnoreCase);
    }

    private static (string Schema, string Table) SplitName(ObjectIdentifier id)
    {
        var parts = id.Parts;
        return parts.Count switch
        {
            >= 2 => (parts[^2], parts[^1]),
            1 => ("dbo", parts[0]),
            _ => ("dbo", id.ToString() ?? string.Empty),
        };
    }

    private static string LastPart(ObjectIdentifier id) =>
        id.Parts.Count > 0 ? id.Parts[^1] : id.ToString() ?? string.Empty;

    /// <summary>
    /// Computes a removal span covering the statement, its trailing <c>GO</c>,
    /// and any following whitespace-only lines — mirroring
    /// <see cref="InlineConstraintFolder"/>'s span logic so a deleted index
    /// leaves no stranded blank lines.
    /// </summary>
    private static (int Start, int Length) RemovalSpan(string text, TSqlFragment fragment)
    {
        var start = fragment.StartOffset;
        var end = fragment.StartOffset + fragment.FragmentLength;

        while (end < text.Length && (text[end] is ' ' or '\t' or '\r' or '\n'))
        {
            end++;
        }

        if (end + 1 < text.Length
            && (text[end] is 'G' or 'g')
            && (text[end + 1] is 'O' or 'o')
            && (end + 2 == text.Length || text[end + 2] is '\r' or '\n' or ';' || char.IsWhiteSpace(text[end + 2])))
        {
            end += 2;
            if (end < text.Length && text[end] == '\r')
            {
                end++;
            }

            if (end < text.Length && text[end] == '\n')
            {
                end++;
            }
        }

        while (end < text.Length)
        {
            var eol = text.IndexOfAny(['\r', '\n'], end);
            if (eol < 0)
            {
                break;
            }

            var lineBlank = true;
            for (var j = end; j < eol; j++)
            {
                if (!char.IsWhiteSpace(text[j]))
                {
                    lineBlank = false;
                    break;
                }
            }

            if (!lineBlank)
            {
                break;
            }

            end = eol + 1;
            if (text[eol] == '\r' && end < text.Length && text[end] == '\n')
            {
                end++;
            }
        }

        return (start, end - start);
    }

    private static string DetectLineEnding(string text)
    {
        var idx = text.IndexOf('\n');
        if (idx <= 0)
        {
            return "\n";
        }

        return text[idx - 1] == '\r' ? "\r\n" : "\n";
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Taking over changed table '{Table}' ({File}) to avoid DacFx PublishChangesToProject crash; {StandaloneCount} standalone child object(s).")]
    private static partial void LogPlanned(ILogger logger, string table, string file, int standaloneCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Rewrote changed table '{Table}' in '{File}'.")]
    private static partial void LogRewrote(ILogger logger, string table, string file);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Changed table '{Table}' has no resolvable source file; leaving it to DacFx.")]
    private static partial void LogSkipUnresolved(ILogger logger, string table);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Changed table '{Table}' is not excludable; leaving it to DacFx.")]
    private static partial void LogCannotExclude(ILogger logger, string table);
}
