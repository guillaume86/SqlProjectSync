using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace SqlProjectSync.Tests;

public class LeadingBlankTrimmerTests
{
    [Fact]
    public void Trims_Leading_Blank_Lines()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            "\n\n   \nCREATE TABLE [dbo].[Demo] (\n    [Id] INT NOT NULL\n);\nGO\n");

        LeadingBlankTrimmer.TrimFile(path, NullLogger.Instance).ShouldBeTrue();

        var after = File.ReadAllText(path);
        after.ShouldStartWith("CREATE TABLE [dbo].[Demo]");
    }

    [Fact]
    public void Trims_Leading_Blank_Lines_Before_Comment_Then_Create_Table()
    {
        using var dir = new TempDirectory();
        // The case Guillaume flagged: DacFx emits blank lines before a
        // leading comment that precedes the CREATE TABLE. The blanks go,
        // the comment + CREATE TABLE land at column 0.
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            "\n\n-- Demo table\n-- Owns: HR\nCREATE TABLE [dbo].[Demo] (\n    [Id] INT NOT NULL\n);\nGO\n");

        LeadingBlankTrimmer.TrimFile(path, NullLogger.Instance).ShouldBeTrue();

        var after = File.ReadAllText(path);
        after.ShouldStartWith("-- Demo table\n");
    }

    [Fact]
    public void Trims_Leading_Blank_Lines_With_Crlf_Endings()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            "\r\n\r\nCREATE TABLE [dbo].[Demo] (\r\n    [Id] INT NOT NULL\r\n);\r\nGO\r\n");

        LeadingBlankTrimmer.TrimFile(path, NullLogger.Instance).ShouldBeTrue();

        File.ReadAllText(path).ShouldStartWith("CREATE TABLE [dbo].[Demo]");
    }

    [Fact]
    public void Leaves_File_Without_Leading_Blanks_Untouched()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            "CREATE TABLE [dbo].[Demo] (\n    [Id] INT NOT NULL\n);\nGO\n");

        var before = File.ReadAllText(path);
        LeadingBlankTrimmer.TrimFile(path, NullLogger.Instance).ShouldBeFalse();
        File.ReadAllText(path).ShouldBe(before);
    }

    [Fact]
    public void Preserves_Leading_Bom_With_No_Blanks()
    {
        using var dir = new TempDirectory();
        // BOM at byte 0 followed directly by CREATE TABLE — nothing to trim,
        // and the BOM bytes should survive untouched (File.ReadAllText would
        // normally consume them).
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            "﻿CREATE TABLE [dbo].[Demo] (\n    [Id] INT NOT NULL\n);\nGO\n");

        LeadingBlankTrimmer.TrimFile(path, NullLogger.Instance).ShouldBeFalse();

        var bytes = File.ReadAllBytes(path);
        bytes[0].ShouldBe((byte)0xEF);
        bytes[1].ShouldBe((byte)0xBB);
        bytes[2].ShouldBe((byte)0xBF);
    }

    [Fact]
    public void Handles_File_That_Is_All_Blank_Lines()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("dbo/Tables/Demo.sql", "\n\n\n");

        // Nothing but blank lines — the trim breaks out at end-of-string
        // (no further newline found), leaves text unchanged.
        LeadingBlankTrimmer.TrimFile(path, NullLogger.Instance).ShouldBeFalse();
    }

    [Fact]
    public void Is_Idempotent()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            "\n\nCREATE TABLE [dbo].[Demo] (\n    [Id] INT NOT NULL\n);\nGO\n");

        LeadingBlankTrimmer.TrimFile(path, NullLogger.Instance).ShouldBeTrue();
        LeadingBlankTrimmer.TrimFile(path, NullLogger.Instance).ShouldBeFalse();
    }
}
