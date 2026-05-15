using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlProjectSync;

/// <summary>
/// Workaround for <see href="https://github.com/microsoft/DacFx/issues/792">DacFx #792</see>:
/// <see cref="Microsoft.SqlServer.Dac.Compare.SchemaComparisonResult.PublishChangesToProject(string, Microsoft.SqlServer.Dac.DacExtractTarget)"/>
/// emits each PK / FK / CHECK / UNIQUE / DEFAULT constraint as a trailing
/// <c>ALTER TABLE ADD CONSTRAINT</c> in the same file, sometimes *in addition* to
/// an inline declaration inside <c>CREATE TABLE</c> (the duplicate case),
/// sometimes instead of it (when the source endpoint is a database and the
/// constraint has no inline annotation). Both shapes diverge from the legacy
/// VS-tool convention of inline-only constraints.
///
/// This helper parses each touched <c>.sql</c> file with <see cref="TSql160Parser"/>,
/// matches every top-level <c>ALTER TABLE ... ADD CONSTRAINT</c> against the
/// <c>CREATE TABLE</c> for the same table earlier in the file, and either drops
/// the ALTER (when the constraint is already inline) or lifts it inline (when
/// it isn't). Untouched batches keep their original whitespace.
/// </summary>
internal static partial class InlineConstraintFolder
{
    /// <summary>
    /// Walks <paramref name="filePaths"/>, folds trailing
    /// <c>ALTER TABLE ADD CONSTRAINT</c> blocks back into their parent
    /// <c>CREATE TABLE</c>, and rewrites each file only when at least one
    /// batch was dropped or lifted. The drop pass (already-inline duplicates)
    /// always runs; the lift pass (standalone-only constraints) only runs
    /// when <paramref name="mode"/> is <see cref="InlineConstraintsMode.ModelFidelity"/>.
    /// </summary>
    public static void Fold(IEnumerable<string> filePaths, InlineConstraintsMode mode, ILogger logger)
    {
        foreach (var path in filePaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                FoldFile(path, mode, logger);
            }
            catch (Exception ex)
            {
                LogFoldFailed(logger, path, ex.Message);
            }
        }
    }

    internal static int FoldFile(string filePath, InlineConstraintsMode mode, ILogger logger)
    {
        var originalText = File.ReadAllText(filePath);
        if (string.IsNullOrWhiteSpace(originalText))
        {
            return 0;
        }

        var parser = new TSql160Parser(initialQuotedIdentifiers: false);
        IList<ParseError> errors;
        TSqlFragment fragment;
        using (var reader = new StringReader(originalText))
        {
            fragment = parser.Parse(reader, out errors);
        }

        if (errors is { Count: > 0 } || fragment is not TSqlScript script)
        {
            LogParseSkipped(logger, filePath, errors?.Count ?? 0);
            return 0;
        }

        var tables = CollectCreateTables(script);
        if (tables.Count == 0)
        {
            return 0;
        }

        var batchActions = new List<(int Start, int Length, string ConstraintName, bool Lifted)>();

        foreach (var batch in script.Batches)
        {
            if (!TryFoldBatch(batch, tables, mode, out var firstName, out var anyLift))
            {
                continue;
            }

            var span = ComputeRemovalSpan(originalText, batch);
            batchActions.Add((span.Start, span.Length, firstName, anyLift));
        }

        if (batchActions.Count == 0)
        {
            return 0;
        }

        // Collect edits: ALTER removals + CREATE TABLE replacements for any
        // table that received a lift.
        var edits = new List<(int Start, int Length, string Replacement)>();
        foreach (var (start, length, _, _) in batchActions)
        {
            edits.Add((start, length, string.Empty));
        }
        foreach (var ctx in tables.Values)
        {
            if (!ctx.Modified)
            {
                continue;
            }
            SortTableConstraints(ctx.Statement);
            var regen = RegenerateCreateTable(ctx.Statement);
            edits.Add((ctx.Statement.StartOffset, ctx.Statement.FragmentLength, regen));
        }

        // Apply highest offset first so earlier offsets stay valid.
        var rewritten = originalText;
        foreach (var (start, length, replacement) in edits.OrderByDescending(e => e.Start))
        {
            rewritten = rewritten.Remove(start, length).Insert(start, replacement);
        }

        if (string.Equals(rewritten, originalText, StringComparison.Ordinal))
        {
            return 0;
        }

        File.WriteAllText(filePath, rewritten);

        foreach (var (_, _, name, lifted) in batchActions)
        {
            if (lifted)
            {
                LogLifted(logger, filePath, name);
            }
            else
            {
                LogDropped(logger, filePath, name);
            }
        }

        return batchActions.Count;
    }

    private sealed class CreateTableContext
    {
        public CreateTableContext(CreateTableStatement statement)
        {
            Statement = statement;
            InlineNames = CollectInlineNames(statement);
        }

        public CreateTableStatement Statement { get; }
        public HashSet<string> InlineNames { get; }
        public bool Modified { get; set; }
    }

    private static Dictionary<string, CreateTableContext> CollectCreateTables(TSqlScript script)
    {
        var map = new Dictionary<string, CreateTableContext>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in script.Batches)
        {
            foreach (var statement in batch.Statements)
            {
                if (statement is CreateTableStatement create && create.SchemaObjectName is not null)
                {
                    var key = NormalizeTableName(create.SchemaObjectName);
                    if (!map.ContainsKey(key))
                    {
                        map[key] = new CreateTableContext(create);
                    }
                }
            }
        }
        return map;
    }

    private static HashSet<string> CollectInlineNames(CreateTableStatement create)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (create.Definition is null)
        {
            return names;
        }

        foreach (var tc in create.Definition.TableConstraints)
        {
            AddIfNamed(names, tc.ConstraintIdentifier);
        }
        foreach (var col in create.Definition.ColumnDefinitions)
        {
            AddIfNamed(names, col.DefaultConstraint?.ConstraintIdentifier);
            foreach (var cc in col.Constraints)
            {
                AddIfNamed(names, cc.ConstraintIdentifier);
            }
        }
        return names;
    }

    /// <summary>
    /// Classifies a single batch:
    /// <list type="bullet">
    ///   <item>Returns <c>false</c> when the batch isn't a pure-constraint
    ///     <see cref="AlterTableAddTableElementStatement"/> targeting a
    ///     <see cref="CreateTableStatement"/> in this file.</item>
    ///   <item>Returns <c>true</c> when every constraint in the batch can
    ///     be either dropped (already inline) or lifted (passes all guards).
    ///     Mutates the matching <see cref="CreateTableContext"/> in place for
    ///     each lift.</item>
    /// </list>
    /// All-or-nothing per batch — any guard failure aborts the batch entirely.
    /// </summary>
    private static bool TryFoldBatch(
        TSqlBatch batch,
        Dictionary<string, CreateTableContext> tables,
        InlineConstraintsMode mode,
        out string firstConstraintName,
        out bool anyLifted)
    {
        firstConstraintName = string.Empty;
        anyLifted = false;

        if (batch.Statements.Count != 1 || batch.Statements[0] is not AlterTableAddTableElementStatement alter)
        {
            return false;
        }

        if (alter.SchemaObjectName is null || alter.Definition is null)
        {
            return false;
        }

        // Skip anything that's also adding columns. A column add has DataType
        // set; the DEFAULT-only entries DacFx emits for ALTER TABLE ADD
        // CONSTRAINT [DF_X] DEFAULT (expr) FOR [col] have DataType == null.
        if (alter.Definition.ColumnDefinitions.Any(c => c.DataType is not null))
        {
            return false;
        }

        var key = NormalizeTableName(alter.SchemaObjectName);
        if (!tables.TryGetValue(key, out var ctx))
        {
            return false;
        }

        var ops = new List<FoldOp>();

        // ScriptDom places ALL ALTER TABLE ... ADD CONSTRAINT statements into
        // TableConstraints — including standalone DEFAULTs (as
        // DefaultConstraintDefinition with a Column reference). Routing
        // happens here by AST type:
        //   - DefaultConstraintDefinition with Column → lift into the matching
        //     column's DefaultConstraint slot.
        //   - Other ConstraintDefinition (PK/FK/CHECK/UNIQUE) → lift into the
        //     CreateTable's TableConstraints list.
        foreach (var tc in alter.Definition.TableConstraints)
        {
            if (tc.ConstraintIdentifier?.Value is not { Length: > 0 } name)
            {
                continue;
            }

            if (tc is DefaultConstraintDefinition def && def.Column?.Value is { Length: > 0 } targetColumn)
            {
                ops.Add(new FoldOp(name, targetColumn, def, null));
            }
            else
            {
                ops.Add(new FoldOp(name, null, null, tc));
            }
        }

        if (ops.Count == 0)
        {
            return false;
        }

        // First pass: classify + check guards on every op.
        foreach (var op in ops)
        {
            if (ctx.InlineNames.Contains(op.Name))
            {
                op.Decision = OpDecision.Drop;
                continue;
            }

            if (op.ColumnDefault is not null)
            {
                if (string.IsNullOrEmpty(op.TargetColumnName))
                {
                    return false;
                }
                var targetCol = FindColumn(ctx.Statement, op.TargetColumnName!);
                if (targetCol is null)
                {
                    return false;
                }
                // Column already has a different DEFAULT → don't clobber.
                if (targetCol.DefaultConstraint is not null)
                {
                    return false;
                }
                op.TargetColumn = targetCol;
                op.Decision = OpDecision.Lift;
                continue;
            }

            if (op.TableConstraint is not null)
            {
                if (op.TableConstraint is UniqueConstraintDefinition uc && uc.IsPrimaryKey
                    && ctx.Statement.Definition?.TableConstraints
                        .OfType<UniqueConstraintDefinition>().Any(t => t.IsPrimaryKey) == true)
                {
                    return false;
                }
                op.Decision = OpDecision.Lift;
                continue;
            }

            // Unexpected shape — be conservative.
            return false;
        }

        // Mode gate: in None mode, leave any batch containing a Lift alone.
        // Pure-Drop batches (already-inline duplicates) still go through —
        // dedup is mandatory because the duplicate fails model validation.
        if (mode == InlineConstraintsMode.None && ops.Any(o => o.Decision == OpDecision.Lift))
        {
            return false;
        }

        // Second pass: apply.
        foreach (var op in ops)
        {
            if (op.Decision == OpDecision.Lift)
            {
                if (op.ColumnDefault is not null && op.TargetColumn is not null)
                {
                    // The "FOR [col]" clause is implicit when inline; clear
                    // it so the script generator doesn't reattach it.
                    op.ColumnDefault.Column = null;
                    op.TargetColumn.DefaultConstraint = op.ColumnDefault;
                }
                else if (op.TableConstraint is not null)
                {
                    ctx.Statement.Definition!.TableConstraints.Add(op.TableConstraint);
                }
                ctx.InlineNames.Add(op.Name);
                ctx.Modified = true;
                anyLifted = true;
            }
        }

        firstConstraintName = ops[0].Name;
        return true;
    }

    private static ColumnDefinition? FindColumn(CreateTableStatement create, string columnName)
    {
        if (create.Definition is null)
        {
            return null;
        }
        return create.Definition.ColumnDefinitions.FirstOrDefault(
            c => string.Equals(c.ColumnIdentifier?.Value, columnName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Sort the table-level constraints into the legacy SSDT convention:
    /// PRIMARY KEY first, then everything else ordered alphabetically by
    /// constraint name. Lifted standalone constraints would otherwise land
    /// in DacFx's emission order, which doesn't match what
    /// <c>Microsoft.Data.Tools.DatabaseProject</c> used to produce.
    /// </summary>
    private static void SortTableConstraints(CreateTableStatement create)
    {
        if (create.Definition is null || create.Definition.TableConstraints.Count <= 1)
        {
            return;
        }

        var sorted = create.Definition.TableConstraints
            .OrderBy(c => c is UniqueConstraintDefinition uc && uc.IsPrimaryKey ? 0 : 1)
            .ThenBy(c => c.ConstraintIdentifier?.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

        create.Definition.TableConstraints.Clear();
        foreach (var c in sorted)
        {
            create.Definition.TableConstraints.Add(c);
        }
    }

    private static string RegenerateCreateTable(CreateTableStatement create)
    {
        var options = new SqlScriptGeneratorOptions
        {
            SqlVersion = SqlVersion.Sql160,
            KeywordCasing = KeywordCasing.Uppercase,
            AlignColumnDefinitionFields = true,
            IncludeSemicolons = true,
            IndentationSize = 4,
            NewLineBeforeFromClause = false,
            AsKeywordOnOwnLine = false,
            NumNewlinesAfterStatement = 0,
        };
        var generator = new Sql160ScriptGenerator(options);
        generator.GenerateScript(create, out var text);
        // Sql160ScriptGenerator omits the trailing semicolon for CREATE TABLE
        // even with IncludeSemicolons = true. The Mpleo convention (and
        // DacFx's own emit) is `);` — append it so we don't drop the
        // terminator that was present in the original file.
        if (!text.TrimEnd().EndsWith(';'))
        {
            text = text.TrimEnd() + ";";
        }
        return text;
    }

    /// <summary>
    /// Computes a removal span covering the batch text + the trailing
    /// <c>GO</c> + line terminators that follow it. Falls back to the batch's
    /// own fragment span when no trailing <c>GO</c> can be located. Never
    /// reaches back before the batch — keeps consecutive removals from
    /// overlapping each other's leading whitespace.
    /// </summary>
    private static (int Start, int Length) ComputeRemovalSpan(string text, TSqlBatch batch)
    {
        var start = batch.StartOffset;
        var end = batch.StartOffset + batch.FragmentLength;

        // Skip whitespace up to a trailing GO.
        while (end < text.Length && (text[end] == ' ' || text[end] == '\t' || text[end] == '\r' || text[end] == '\n'))
        {
            end++;
        }

        // Consume a trailing GO (case-insensitive, on its own line).
        if (end + 1 < text.Length
            && (text[end] == 'G' || text[end] == 'g')
            && (text[end + 1] == 'O' || text[end + 1] == 'o')
            && (end + 2 == text.Length || text[end + 2] == '\r' || text[end + 2] == '\n' || text[end + 2] == ';' || char.IsWhiteSpace(text[end + 2])))
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

        return (start, end - start);
    }

    private static string NormalizeTableName(SchemaObjectName name)
    {
        var schema = name.SchemaIdentifier?.Value ?? "dbo";
        var table = name.BaseIdentifier?.Value ?? string.Empty;
        return $"{schema}.{table}";
    }

    private static void AddIfNamed(HashSet<string> set, Identifier? id)
    {
        if (id?.Value is { Length: > 0 } value)
        {
            set.Add(value);
        }
    }

    private enum OpDecision
    {
        Drop,
        Lift,
    }

    private sealed class FoldOp
    {
        public FoldOp(string name, string? targetColumnName, DefaultConstraintDefinition? columnDefault, ConstraintDefinition? tableConstraint)
        {
            Name = name;
            TargetColumnName = targetColumnName;
            ColumnDefault = columnDefault;
            TableConstraint = tableConstraint;
        }

        public string Name { get; }
        public string? TargetColumnName { get; }
        public DefaultConstraintDefinition? ColumnDefault { get; }
        public ConstraintDefinition? TableConstraint { get; }
        public ColumnDefinition? TargetColumn { get; set; }
        public OpDecision Decision { get; set; }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Skipping inline-constraint fold on '{File}' — parse errors ({ErrorCount}).")]
    private static partial void LogParseSkipped(ILogger logger, string file, int errorCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Inline-constraint fold failed for '{File}': {Reason}.")]
    private static partial void LogFoldFailed(ILogger logger, string file, string reason);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Dropped redundant ALTER TABLE ADD CONSTRAINT [{Constraint}] in '{File}' (DacFx #792 workaround).")]
    private static partial void LogDropped(ILogger logger, string file, string constraint);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Lifted ALTER TABLE ADD CONSTRAINT [{Constraint}] into CREATE TABLE in '{File}' (DacFx #792 workaround).")]
    private static partial void LogLifted(ILogger logger, string file, string constraint);
}
