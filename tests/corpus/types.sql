-- User-defined types: alias types and table types.

CREATE TYPE dbo.AccountNumber FROM CHAR(12) NOT NULL;
GO

CREATE TYPE dbo.Money2 FROM DECIMAL(18, 2) NULL;
GO

CREATE TYPE dbo.CustomerList AS TABLE (
    CustomerId INT NOT NULL PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL,
    Country CHAR(2) NULL,
    CHECK (LEN(Name) > 0)
);
GO

CREATE TYPE dbo.IdList AS TABLE (Id INT NOT NULL PRIMARY KEY CLUSTERED);
GO

CREATE TYPE dbo.KeyValue AS TABLE (
    [Key] NVARCHAR(100) NOT NULL,
    [Value] NVARCHAR(MAX) NULL,
    UNIQUE ([Key]),
    INDEX IX_KeyValue NONCLUSTERED ([Key])
);
GO

-- An alias type used in a declaration, a column, and a parameter.
DECLARE @Account dbo.AccountNumber = '000000000001';
GO

CREATE TABLE dbo.Accounts (
    AccountId INT NOT NULL PRIMARY KEY,
    Number dbo.AccountNumber NOT NULL,
    Balance dbo.Money2 NULL
);
GO

-- A table type as a read-only parameter, which is the only form allowed.
CREATE PROCEDURE dbo.ImportCustomers
    @Customers dbo.CustomerList READONLY
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.Customers (CustomerId, Name, Country)
    SELECT c.CustomerId, c.Name, c.Country
    FROM @Customers AS c;
END;
GO

CREATE FUNCTION dbo.fnCountIds (@Ids dbo.IdList READONLY)
RETURNS INT
AS
BEGIN
    RETURN (SELECT COUNT(*) FROM @Ids);
END;
GO

DROP TYPE IF EXISTS dbo.Money2;
GO

DROP TYPE dbo.KeyValue;
GO
