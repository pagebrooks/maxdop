-- Variables, table variables, and table-valued parameters.

DECLARE @Id INT;
DECLARE @Name NVARCHAR(100), @Country CHAR(2), @Active BIT;
DECLARE @Total DECIMAL(18, 2) = 0.00;
DECLARE @Created DATETIME2(3) = SYSUTCDATETIME();
DECLARE @Payload NVARCHAR(MAX) = N'';
DECLARE @Xml XML;
DECLARE @Guid UNIQUEIDENTIFIER = NEWID();
DECLARE @Custom dbo.AccountNumber;
GO

SET @Id = 1;
SET @Name = N'Acme';
SELECT @Total = SUM(o.Total) FROM dbo.Orders AS o WHERE o.CustomerId = @Id;
SELECT @Id = o.OrderId, @Total = o.Total FROM dbo.Orders AS o WHERE o.OrderId = 1;
SET @Total += 10.00;
GO

-- Table variables.
DECLARE @Customers TABLE (
    CustomerId INT NOT NULL PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL,
    Country CHAR(2) NULL,
    INDEX IX_Country NONCLUSTERED (Country)
);

INSERT INTO @Customers (CustomerId, Name, Country)
VALUES (1, N'Acme', N'GB'), (2, N'Globex', N'US');

SELECT c.CustomerId, c.Name
FROM @Customers AS c
WHERE c.Country = N'GB'
ORDER BY c.Name;

UPDATE @Customers SET Country = N'IE' WHERE CustomerId = 1;
DELETE FROM @Customers WHERE CustomerId = 2;
GO

DECLARE @Simple TABLE (Id INT IDENTITY(1, 1), Value NVARCHAR(50) NULL);
INSERT INTO @Simple (Value) VALUES (N'a');

DECLARE @Count INT = (SELECT COUNT(*) FROM @Simple);
GO

-- Joining a table variable to a real table.
DECLARE @Wanted TABLE (OrderId INT PRIMARY KEY);
INSERT INTO @Wanted (OrderId) SELECT TOP (10) o.OrderId FROM dbo.Orders AS o;

SELECT o.OrderId, o.Total
FROM dbo.Orders AS o
INNER JOIN @Wanted AS w ON w.OrderId = o.OrderId;
GO

-- A table-valued parameter passed to a procedure.
DECLARE @Batch dbo.CustomerList;
INSERT INTO @Batch (CustomerId, Name) VALUES (1, N'Acme');
EXEC dbo.ImportCustomers @Customers = @Batch;
GO

-- Cursor variable.
DECLARE @Cursor CURSOR;
SET @Cursor = CURSOR FORWARD_ONLY STATIC FOR SELECT o.OrderId FROM dbo.Orders AS o;
OPEN @Cursor;
CLOSE @Cursor;
DEALLOCATE @Cursor;
GO
