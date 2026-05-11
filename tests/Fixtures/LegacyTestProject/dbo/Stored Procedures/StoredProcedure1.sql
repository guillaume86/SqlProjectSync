CREATE PROCEDURE [dbo].[StoredProcedure1]
    @id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [Id], [Name] FROM [dbo].[Table1] WHERE [Id] = @id;
END
