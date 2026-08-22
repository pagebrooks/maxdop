-- Procedures: parameters, options, and every call form.

CREATE PROCEDURE dbo.GetCustomer
    @CustomerId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT c.CustomerId, c.Name, c.Country
    FROM dbo.Customers AS c
    WHERE c.CustomerId = @CustomerId;
END;
GO

-- Defaults, output parameters, and a parenthesised list.
CREATE PROCEDURE dbo.SearchCustomers
(
    @Country CHAR(2) = NULL,
    @NamePattern NVARCHAR(100) = N'%',
    @MatchCount INT OUTPUT
)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT c.CustomerId, c.Name
    FROM dbo.Customers AS c
    WHERE (@Country IS NULL OR c.Country = @Country)
      AND c.Name LIKE @NamePattern;

    SET @MatchCount = @@ROWCOUNT;
END;
GO

CREATE PROCEDURE dbo.NoParameters
AS
    SELECT 1;
GO

CREATE PROCEDURE dbo.WithOptions
    @Id INT
WITH RECOMPILE, ENCRYPTION
AS
    SELECT @Id;
GO

CREATE PROCEDURE dbo.AsCaller
    @Id INT
WITH EXECUTE AS CALLER
AS
    SELECT @Id;
GO

ALTER PROCEDURE dbo.NoParameters
AS
    SELECT 2;
GO

CREATE OR ALTER PROCEDURE dbo.Upserted
    @CustomerId INT,
    @Name NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF EXISTS (SELECT 1 FROM dbo.Customers AS c WHERE c.CustomerId = @CustomerId)
        UPDATE dbo.Customers SET Name = @Name WHERE CustomerId = @CustomerId;
    ELSE
        INSERT INTO dbo.Customers (CustomerId, Name) VALUES (@CustomerId, @Name);

    RETURN 0;
END;
GO

-- Calls: positional, named, output, return value, and with a recompile hint.
EXEC dbo.GetCustomer 1;
GO

EXECUTE dbo.GetCustomer @CustomerId = 1;
GO

DECLARE @Count INT;
EXEC dbo.SearchCustomers @Country = N'GB', @MatchCount = @Count OUTPUT;
GO

DECLARE @ReturnValue INT;
EXEC @ReturnValue = dbo.Upserted @CustomerId = 1, @Name = N'Acme';
GO

EXEC dbo.GetCustomer @CustomerId = 1 WITH RECOMPILE;
GO

EXEC sys.sp_executesql N'SELECT @a', N'@a INT', @a = 1;
GO

DROP PROCEDURE IF EXISTS dbo.NoParameters;
GO
