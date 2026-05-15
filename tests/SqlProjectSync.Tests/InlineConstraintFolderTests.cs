using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace SqlProjectSync.Tests;

public class InlineConstraintFolderTests
{
    [Fact]
    public void Strips_Trailing_Alter_That_Duplicates_Inline_Default()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            """
            CREATE TABLE [dbo].[Demo] (
                [Id]   INT NOT NULL,
                [Note] NVARCHAR (50) CONSTRAINT [DF_Demo_Note] DEFAULT ('hello') NULL,
                PRIMARY KEY CLUSTERED ([Id] ASC)
            );
            GO
            ALTER TABLE [dbo].[Demo]
                ADD CONSTRAINT [DF_Demo_Note] DEFAULT ('hello') FOR [Note];
            GO

            """);

        var dropped = InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.None, NullLogger.Instance);

        dropped.ShouldBe(1);
        var after = File.ReadAllText(path);
        after.ShouldContain("CONSTRAINT [DF_Demo_Note] DEFAULT");
        after.ShouldNotContain("ADD CONSTRAINT [DF_Demo_Note]");
    }

    [Fact]
    public void Strips_Trailing_Alter_That_Duplicates_Inline_Check()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            """
            CREATE TABLE [dbo].[Demo] (
                [Id]        INT NOT NULL,
                [Algorithm] VARCHAR (10) NOT NULL,
                CONSTRAINT [CK_Demo_Algorithm] CHECK ([Algorithm]='RS256')
            );
            GO
            ALTER TABLE [dbo].[Demo]
                ADD CONSTRAINT [CK_Demo_Algorithm] CHECK ([Algorithm]='RS256');
            GO
            """);

        var dropped = InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.None, NullLogger.Instance);

        dropped.ShouldBe(1);
        File.ReadAllText(path).ShouldNotContain("ADD CONSTRAINT [CK_Demo_Algorithm]");
    }

    [Fact]
    public void Strips_Trailing_Alter_That_Duplicates_Inline_ForeignKey()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            """
            CREATE TABLE [dbo].[Demo] (
                [Id]    INT NOT NULL,
                [LogId] INT NULL,
                CONSTRAINT [FK_Demo_SysLog] FOREIGN KEY ([LogId]) REFERENCES [dbo].[SysLog] ([LogId])
            );
            GO
            ALTER TABLE [dbo].[Demo]
                ADD CONSTRAINT [FK_Demo_SysLog] FOREIGN KEY ([LogId]) REFERENCES [dbo].[SysLog] ([LogId]);
            GO
            """);

        var dropped = InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.None, NullLogger.Instance);

        dropped.ShouldBe(1);
        File.ReadAllText(path).ShouldNotContain("ADD CONSTRAINT [FK_Demo_SysLog]");
    }

    [Fact]
    public void Lifts_Standalone_Default_Into_Column_Definition()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            """
            CREATE TABLE [dbo].[Demo] (
                [Id] INT NOT NULL,
                [Note] NVARCHAR (50) NULL
            );
            GO
            ALTER TABLE [dbo].[Demo]
                ADD CONSTRAINT [DF_Demo_Note] DEFAULT ('hello') FOR [Note];
            GO
            """);

        var folded = InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.ModelFidelity, NullLogger.Instance);

        folded.ShouldBe(1);
        var after = File.ReadAllText(path);
        after.ShouldNotContain("ADD CONSTRAINT [DF_Demo_Note]");
        after.ShouldContain("CONSTRAINT [DF_Demo_Note] DEFAULT");
    }

    [Fact]
    public void Lifts_Standalone_Check_Into_Table_Constraints()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            """
            CREATE TABLE [dbo].[Demo] (
                [Id]        INT NOT NULL,
                [Algorithm] VARCHAR (10) NOT NULL
            );
            GO
            ALTER TABLE [dbo].[Demo]
                ADD CONSTRAINT [CK_Demo_Algorithm] CHECK ([Algorithm]='RS256');
            GO
            """);

        var folded = InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.ModelFidelity, NullLogger.Instance);

        folded.ShouldBe(1);
        var after = File.ReadAllText(path);
        after.ShouldNotContain("ADD CONSTRAINT [CK_Demo_Algorithm]");
        after.ShouldContain("CONSTRAINT [CK_Demo_Algorithm] CHECK");
    }

    [Fact]
    public void Lifts_Standalone_ForeignKey_Into_Table_Constraints()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            """
            CREATE TABLE [dbo].[Demo] (
                [Id]    INT NOT NULL,
                [LogId] INT NULL
            );
            GO
            ALTER TABLE [dbo].[Demo]
                ADD CONSTRAINT [FK_Demo_SysLog] FOREIGN KEY ([LogId]) REFERENCES [dbo].[SysLog] ([LogId]);
            GO
            """);

        var folded = InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.ModelFidelity, NullLogger.Instance);

        folded.ShouldBe(1);
        var after = File.ReadAllText(path);
        after.ShouldNotContain("ADD CONSTRAINT [FK_Demo_SysLog]");
        after.ShouldContain("CONSTRAINT [FK_Demo_SysLog] FOREIGN KEY");
    }

    [Fact]
    public void Lifts_Standalone_PrimaryKey_Into_Table_Constraints()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            """
            CREATE TABLE [dbo].[Demo] (
                [Id] INT NOT NULL
            );
            GO
            ALTER TABLE [dbo].[Demo]
                ADD CONSTRAINT [PK_Demo] PRIMARY KEY CLUSTERED ([Id] ASC);
            GO
            """);

        var folded = InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.ModelFidelity, NullLogger.Instance);

        folded.ShouldBe(1);
        var after = File.ReadAllText(path);
        after.ShouldNotContain("ADD CONSTRAINT [PK_Demo]");
        after.ShouldContain("CONSTRAINT [PK_Demo] PRIMARY KEY");
    }

    [Fact]
    public void Lifts_Standalone_Unique_Into_Table_Constraints()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            """
            CREATE TABLE [dbo].[Demo] (
                [Id]   INT NOT NULL,
                [Code] VARCHAR (20) NOT NULL
            );
            GO
            ALTER TABLE [dbo].[Demo]
                ADD CONSTRAINT [UQ_Demo_Code] UNIQUE ([Code] ASC);
            GO
            """);

        var folded = InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.ModelFidelity, NullLogger.Instance);

        folded.ShouldBe(1);
        var after = File.ReadAllText(path);
        after.ShouldNotContain("ADD CONSTRAINT [UQ_Demo_Code]");
        after.ShouldContain("CONSTRAINT [UQ_Demo_Code] UNIQUE");
    }

    [Fact]
    public void Lifts_Mixed_Batch_Of_Redundant_And_Liftable_Together()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            """
            CREATE TABLE [dbo].[Demo] (
                [Id] INT NOT NULL,
                [Flag] BIT CONSTRAINT [DF_Demo_Flag] DEFAULT ((0)) NOT NULL
            );
            GO
            ALTER TABLE [dbo].[Demo]
                ADD CONSTRAINT [DF_Demo_Flag] DEFAULT ((0)) FOR [Flag];
            GO
            ALTER TABLE [dbo].[Demo]
                ADD CONSTRAINT [PK_Demo] PRIMARY KEY CLUSTERED ([Id] ASC);
            GO
            """);

        var folded = InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.ModelFidelity, NullLogger.Instance);

        folded.ShouldBe(2);
        var after = File.ReadAllText(path);
        after.ShouldNotContain("ADD CONSTRAINT [DF_Demo_Flag]");
        after.ShouldNotContain("ADD CONSTRAINT [PK_Demo]");
        after.ShouldContain("CONSTRAINT [DF_Demo_Flag] DEFAULT");
        after.ShouldContain("CONSTRAINT [PK_Demo] PRIMARY KEY");
    }

    [Fact]
    public void Preserves_Alter_Whose_Target_Column_Is_Missing()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            """
            CREATE TABLE [dbo].[Demo] (
                [Id] INT NOT NULL
            );
            GO
            ALTER TABLE [dbo].[Demo]
                ADD CONSTRAINT [DF_Demo_Missing] DEFAULT ((0)) FOR [DoesNotExist];
            GO
            """);

        var before = File.ReadAllText(path);
        var folded = InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.ModelFidelity, NullLogger.Instance);

        folded.ShouldBe(0);
        File.ReadAllText(path).ShouldBe(before);
    }

    [Fact]
    public void Preserves_Alter_When_Inline_Pk_Already_Exists()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            """
            CREATE TABLE [dbo].[Demo] (
                [Id] INT NOT NULL,
                CONSTRAINT [PK_Demo] PRIMARY KEY CLUSTERED ([Id] ASC)
            );
            GO
            ALTER TABLE [dbo].[Demo]
                ADD CONSTRAINT [PK_Demo_Other] PRIMARY KEY CLUSTERED ([Id] DESC);
            GO
            """);

        var before = File.ReadAllText(path);
        var folded = InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.ModelFidelity, NullLogger.Instance);

        folded.ShouldBe(0);
        File.ReadAllText(path).ShouldBe(before);
    }

    [Fact]
    public void Preserves_Alter_When_Column_Already_Has_A_Different_Default()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            """
            CREATE TABLE [dbo].[Demo] (
                [Id]   INT NOT NULL,
                [Flag] BIT CONSTRAINT [DF_Demo_Flag_Original] DEFAULT ((0)) NOT NULL
            );
            GO
            ALTER TABLE [dbo].[Demo]
                ADD CONSTRAINT [DF_Demo_Flag_New] DEFAULT ((1)) FOR [Flag];
            GO
            """);

        var before = File.ReadAllText(path);
        var folded = InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.ModelFidelity, NullLogger.Instance);

        folded.ShouldBe(0);
        File.ReadAllText(path).ShouldBe(before);
    }

    [Fact]
    public void Preserves_Alter_That_Targets_A_Table_Not_In_This_File()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            """
            CREATE TABLE [dbo].[Demo] (
                [Id] INT NOT NULL
            );
            GO
            ALTER TABLE [dbo].[OtherTable]
                ADD CONSTRAINT [DF_OtherTable_X] DEFAULT ((0)) FOR [Flag];
            GO
            """);

        var before = File.ReadAllText(path);
        var folded = InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.ModelFidelity, NullLogger.Instance);

        folded.ShouldBe(0);
        File.ReadAllText(path).ShouldBe(before);
    }

    [Fact]
    public void Preserves_Alter_Adding_A_Column()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            """
            CREATE TABLE [dbo].[Demo] (
                [Id] INT NOT NULL,
                [Note] NVARCHAR (50) CONSTRAINT [DF_Demo_Note] DEFAULT ('hello') NULL
            );
            GO
            ALTER TABLE [dbo].[Demo]
                ADD [Extra] INT CONSTRAINT [DF_Demo_Note] DEFAULT (1) NULL;
            GO
            """);

        var before = File.ReadAllText(path);
        var dropped = InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.None, NullLogger.Instance);

        // Adding a column is not a pure constraint-add; we never strip these,
        // even when one of the column's inline constraint names collides.
        dropped.ShouldBe(0);
        File.ReadAllText(path).ShouldBe(before);
    }

    [Fact]
    public void Handles_Multiple_Redundant_Alters_Single_File()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/SysJwtSigningKey.sql",
            """
            CREATE TABLE [dbo].[SysJwtSigningKey] (
                [JwtSigningKeyId] INT NOT NULL,
                [Algorithm]       VARCHAR (10) NOT NULL,
                [PrivateKeyPem]   NVARCHAR (MAX) NULL,
                [PublicKeyPem]    NVARCHAR (MAX) NOT NULL,
                [IsPrimary]       BIT NOT NULL,
                [CreatedAt]       DATETIME CONSTRAINT [DF_SysJwtSigningKey_CreatedAt] DEFAULT (getdate()) NOT NULL,
                CONSTRAINT [PK_SysJwtSigningKey] PRIMARY KEY CLUSTERED ([JwtSigningKeyId] ASC),
                CONSTRAINT [CK_SysJwtSigningKey_Algorithm] CHECK ([Algorithm]='RS256'),
                CONSTRAINT [CK_SysJwtSigningKey_PrivateKeyRequiredOnPrimary] CHECK ([IsPrimary]=(0) OR [PrivateKeyPem] IS NOT NULL)
            );
            GO


            ALTER TABLE [dbo].[SysJwtSigningKey]
                ADD CONSTRAINT [CK_SysJwtSigningKey_Algorithm] CHECK ([Algorithm]='RS256');
            GO


            ALTER TABLE [dbo].[SysJwtSigningKey]
                ADD CONSTRAINT [CK_SysJwtSigningKey_PrivateKeyRequiredOnPrimary] CHECK ([IsPrimary]=(0) OR [PrivateKeyPem] IS NOT NULL);
            GO


            ALTER TABLE [dbo].[SysJwtSigningKey]
                ADD CONSTRAINT [DF_SysJwtSigningKey_CreatedAt] DEFAULT (getdate()) FOR [CreatedAt];
            GO
            """);

        var dropped = InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.None, NullLogger.Instance);

        dropped.ShouldBe(3);
        var after = File.ReadAllText(path);
        after.ShouldNotContain("ADD CONSTRAINT [CK_SysJwtSigningKey_Algorithm]");
        after.ShouldNotContain("ADD CONSTRAINT [CK_SysJwtSigningKey_PrivateKeyRequiredOnPrimary]");
        after.ShouldNotContain("ADD CONSTRAINT [DF_SysJwtSigningKey_CreatedAt]");
        // Inline definitions and indexes remain.
        after.ShouldContain("CONSTRAINT [PK_SysJwtSigningKey]");
        after.ShouldContain("CONSTRAINT [CK_SysJwtSigningKey_Algorithm]");
        after.ShouldContain("CONSTRAINT [DF_SysJwtSigningKey_CreatedAt]");
    }

    [Fact]
    public void Is_Idempotent()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            """
            CREATE TABLE [dbo].[Demo] (
                [Id]   INT NOT NULL,
                [Note] NVARCHAR (50) CONSTRAINT [DF_Demo_Note] DEFAULT ('hello') NULL
            );
            GO
            ALTER TABLE [dbo].[Demo]
                ADD CONSTRAINT [DF_Demo_Note] DEFAULT ('hello') FOR [Note];
            GO
            """);

        InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.None, NullLogger.Instance).ShouldBe(1);
        InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.None, NullLogger.Instance).ShouldBe(0);
    }

    [Fact]
    public void Skips_File_With_Parse_Errors_Without_Mutating()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("Garbled.sql", "this is not valid T-SQL @@@");

        var before = File.ReadAllText(path);
        var dropped = InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.None, NullLogger.Instance);

        dropped.ShouldBe(0);
        File.ReadAllText(path).ShouldBe(before);
    }

    [Fact]
    public void Lift_Preserves_Lf_Line_Endings_In_Regenerated_Block()
    {
        using var dir = new TempDirectory();
        var lf = "CREATE TABLE [dbo].[Demo] (\n"
                 + "    [Id] INT NOT NULL,\n"
                 + "    [Note] NVARCHAR (50) NULL\n"
                 + ");\n"
                 + "GO\n"
                 + "ALTER TABLE [dbo].[Demo]\n"
                 + "    ADD CONSTRAINT [PK_Demo] PRIMARY KEY CLUSTERED ([Id] ASC);\n"
                 + "GO\n";
        var path = dir.Write("dbo/Tables/Demo.sql", lf);

        InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.ModelFidelity, NullLogger.Instance);

        var after = File.ReadAllText(path);
        after.ShouldNotContain("\r", customMessage: "Lift introduced CRLF into a file that was LF-only.");
        after.ShouldContain("CONSTRAINT [PK_Demo] PRIMARY KEY");
    }

    [Fact]
    public void Lift_Preserves_Crlf_Line_Endings_In_Regenerated_Block()
    {
        using var dir = new TempDirectory();
        var crlf = "CREATE TABLE [dbo].[Demo] (\r\n"
                   + "    [Id] INT NOT NULL,\r\n"
                   + "    [Note] NVARCHAR (50) NULL\r\n"
                   + ");\r\n"
                   + "GO\r\n"
                   + "ALTER TABLE [dbo].[Demo]\r\n"
                   + "    ADD CONSTRAINT [PK_Demo] PRIMARY KEY CLUSTERED ([Id] ASC);\r\n"
                   + "GO\r\n";
        var path = dir.Write("dbo/Tables/Demo.sql", crlf);

        InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.ModelFidelity, NullLogger.Instance);

        var after = File.ReadAllBytes(path);
        // Every LF must be preceded by CR — no stray lone LFs.
        for (int i = 0; i < after.Length; i++)
        {
            if (after[i] == (byte)'\n')
            {
                (i > 0 && after[i - 1] == (byte)'\r').ShouldBeTrue(
                    customMessage: $"Lone LF at byte {i} in CRLF-encoded file.");
            }
        }
    }

    [Fact]
    public void Lift_Does_Not_Introduce_Trailing_Blank_Lines()
    {
        using var dir = new TempDirectory();
        var input = "CREATE TABLE [dbo].[Demo] (\n"
                    + "    [Id] INT NOT NULL,\n"
                    + "    [Note] NVARCHAR (50) NULL\n"
                    + ");\n"
                    + "GO\n"
                    + "ALTER TABLE [dbo].[Demo]\n"
                    + "    ADD CONSTRAINT [PK_Demo] PRIMARY KEY CLUSTERED ([Id] ASC);\n"
                    + "GO\n";
        var path = dir.Write("dbo/Tables/Demo.sql", input);

        InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.ModelFidelity, NullLogger.Instance);

        var after = File.ReadAllText(path);
        after.ShouldNotContain("\n\n\n", customMessage: "Three consecutive newlines indicate an extra blank line.");
        after.ShouldEndWith("GO\n", customMessage: "File should end with GO + single newline, not extra trailing blanks.");
    }

    [Fact]
    public void Mode_None_Preserves_Standalone_PrimaryKey()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            """
            CREATE TABLE [dbo].[Demo] (
                [Id] INT NOT NULL
            );
            GO
            ALTER TABLE [dbo].[Demo]
                ADD CONSTRAINT [PK_Demo] PRIMARY KEY CLUSTERED ([Id] ASC);
            GO
            """);

        var before = File.ReadAllText(path);
        var folded = InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.None, NullLogger.Instance);

        folded.ShouldBe(0);
        File.ReadAllText(path).ShouldBe(before);
    }

    [Fact]
    public void Mode_None_Preserves_Standalone_Default_With_No_Inline_Twin()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            """
            CREATE TABLE [dbo].[Demo] (
                [Id]   INT NOT NULL,
                [Note] NVARCHAR (50) NULL
            );
            GO
            ALTER TABLE [dbo].[Demo]
                ADD CONSTRAINT [DF_Demo_Note] DEFAULT ('hello') FOR [Note];
            GO
            """);

        var before = File.ReadAllText(path);
        var folded = InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.None, NullLogger.Instance);

        folded.ShouldBe(0);
        File.ReadAllText(path).ShouldBe(before);
    }

    [Fact]
    public void Mode_None_Drops_Duplicate_Batch_But_Skips_Lift_Batch_In_Same_File()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            """
            CREATE TABLE [dbo].[Demo] (
                [Id] INT NOT NULL,
                [Flag] BIT CONSTRAINT [DF_Demo_Flag] DEFAULT ((0)) NOT NULL
            );
            GO
            ALTER TABLE [dbo].[Demo]
                ADD CONSTRAINT [DF_Demo_Flag] DEFAULT ((0)) FOR [Flag];
            GO
            ALTER TABLE [dbo].[Demo]
                ADD CONSTRAINT [PK_Demo] PRIMARY KEY CLUSTERED ([Id] ASC);
            GO
            """);

        var folded = InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.None, NullLogger.Instance);

        // Pure-dup batch dropped (DF_Demo_Flag), lift batch (PK_Demo) preserved.
        folded.ShouldBe(1);
        var after = File.ReadAllText(path);
        after.ShouldNotContain("ADD CONSTRAINT [DF_Demo_Flag]");
        after.ShouldContain("ADD CONSTRAINT [PK_Demo]");
    }

    [Fact]
    public void Preserves_Triggers_And_Indexes_That_Follow_The_Table()
    {
        using var dir = new TempDirectory();
        var path = dir.Write(
            "dbo/Tables/Demo.sql",
            """
            CREATE TABLE [dbo].[Demo] (
                [Id]   INT NOT NULL,
                [Note] NVARCHAR (50) CONSTRAINT [DF_Demo_Note] DEFAULT ('hello') NULL
            );
            GO

            ALTER TABLE [dbo].[Demo]
                ADD CONSTRAINT [DF_Demo_Note] DEFAULT ('hello') FOR [Note];
            GO

            CREATE UNIQUE NONCLUSTERED INDEX [Idx_Demo_Note]
                ON [dbo].[Demo]([Note] ASC);
            GO

            CREATE TRIGGER [dbo].[Tr_Demo_AfterInsert]
                ON [dbo].[Demo]
                AFTER INSERT
            AS
            BEGIN
                SET NOCOUNT ON;
            END
            GO
            """);

        var dropped = InlineConstraintFolder.FoldFile(path, InlineConstraintsMode.None, NullLogger.Instance);

        dropped.ShouldBe(1);
        var after = File.ReadAllText(path);
        after.ShouldNotContain("ADD CONSTRAINT [DF_Demo_Note]");
        after.ShouldContain("CREATE UNIQUE NONCLUSTERED INDEX [Idx_Demo_Note]");
        after.ShouldContain("CREATE TRIGGER [dbo].[Tr_Demo_AfterInsert]");
    }
}
