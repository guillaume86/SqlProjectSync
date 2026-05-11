CREATE TRIGGER [dbo].[Tr_Table1_Trigger1]
    ON [dbo].[Table1]
    AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
END
