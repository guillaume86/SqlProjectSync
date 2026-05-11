CREATE FUNCTION [dbo].[ScalarFunction1] (@input INT)
RETURNS INT
AS
BEGIN
    RETURN @input * 2;
END
