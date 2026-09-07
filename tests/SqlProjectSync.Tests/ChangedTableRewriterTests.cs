using Microsoft.SqlServer.TransactSql.ScriptDom;
using Shouldly;
using Xunit;

namespace SqlProjectSync.Tests;

/// <summary>
/// Span arithmetic behind <see cref="ChangedTableRewriter"/>'s standalone
/// replace/remove ops. SQL Server stores a trigger's leading comments as part of
/// its definition, so the replacement script DacFx produces already carries the
/// header — the span being replaced has to cover the file's existing header too,
/// or each sync stacks one more copy.
/// </summary>
public class ChangedTableRewriterTests
{
    private const string Header =
        "-- =============================================\n"
        + "-- Author:\t\tAuto Generated\n"
        + "-- Description:\tDelete orphaned Memo rows\n"
        + "-- =============================================\n";

    private const string Trigger =
        "CREATE TRIGGER [dbo].[Tr_Demo_AfterDelete] ON [dbo].[Demo] AFTER DELETE AS BEGIN SET NOCOUNT ON; END";

    private const string Table = "CREATE TABLE [dbo].[Demo] ([Id] INT NOT NULL);\nGO\n";

    [Fact]
    public void LeadingCommentStart_CoversHeaderRunBeforeStatement()
    {
        var sql = Table + "\n" + Header + Trigger + "\nGO\n";
        var trigger = ParseTrigger(sql);

        var start = ChangedTableRewriter.LeadingCommentStart(trigger);

        start.ShouldBe(sql.IndexOf(Header, StringComparison.Ordinal));
    }

    [Fact]
    public void LeadingCommentStart_LeavesBlankSeparatorLineOutsideTheSpan()
    {
        var sql = Table + "\n" + Header + Trigger + "\nGO\n";
        var trigger = ParseTrigger(sql);

        var start = ChangedTableRewriter.LeadingCommentStart(trigger);

        sql.Substring(start - 4, 4).ShouldBe("GO\n\n");
    }

    [Fact]
    public void LeadingCommentStart_IsStatementStartWhenNoCommentPrecedesIt()
    {
        var sql = Table + "\n" + Trigger + "\nGO\n";
        var trigger = ParseTrigger(sql);

        ChangedTableRewriter.LeadingCommentStart(trigger).ShouldBe(trigger.StartOffset);
    }

    [Fact]
    public void LeadingCommentStart_CoversStackedHeadersWithBlankLinesBetween()
    {
        // The shape the bug produced in the wild: several copies of the header,
        // sometimes separated by a blank line, all belonging to the one trigger.
        var sql = Table + "\n" + Header + "\n" + Header + Header + Trigger + "\nGO\n";
        var trigger = ParseTrigger(sql);

        var start = ChangedTableRewriter.LeadingCommentStart(trigger);

        start.ShouldBe(sql.IndexOf(Header, StringComparison.Ordinal));
    }

    [Fact]
    public void LeadingCommentStart_StopsAtPreviousBatchGo()
    {
        // A trailing comment inside the previous batch is that batch's business.
        var sql = "CREATE TABLE [dbo].[Demo] ([Id] INT NOT NULL);\n-- end of table\nGO\n\n" + Trigger + "\nGO\n";
        var trigger = ParseTrigger(sql);

        ChangedTableRewriter.LeadingCommentStart(trigger).ShouldBe(trigger.StartOffset);
    }

    [Fact]
    public void LeadingCommentStart_CoversBlockComments()
    {
        var sql = Table + "\n/* generated */\n" + Trigger + "\nGO\n";
        var trigger = ParseTrigger(sql);

        var start = ChangedTableRewriter.LeadingCommentStart(trigger);

        start.ShouldBe(sql.IndexOf("/* generated */", StringComparison.Ordinal));
    }

    [Fact]
    public void ReplacementSpan_RunsFromHeaderToEndOfStatement()
    {
        var sql = Table + "\n" + Header + Trigger + "\nGO\n";
        var trigger = ParseTrigger(sql);

        var (start, length) = ChangedTableRewriter.ReplacementSpan(trigger);

        sql.Substring(start, length).ShouldBe(Header + Trigger);
    }

    private static CreateTriggerStatement ParseTrigger(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: false);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        errors.ShouldBeEmpty();

        return ((TSqlScript)fragment).Batches
            .SelectMany(b => b.Statements)
            .OfType<CreateTriggerStatement>()
            .Single();
    }
}
