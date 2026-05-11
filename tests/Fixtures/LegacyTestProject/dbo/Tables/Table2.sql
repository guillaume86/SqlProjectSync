CREATE TABLE [dbo].[Table2]
(
    [Id]       INT NOT NULL PRIMARY KEY,
    [Table1Id] INT NOT NULL REFERENCES [dbo].[Table1] ([Id])
);
