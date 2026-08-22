-- INSERT: every source form, plus OUTPUT and identity handling.

INSERT INTO dbo.Customers (Name, Country) VALUES (N'Acme', N'GB');
GO

INSERT dbo.Customers (Name, Country) VALUES (N'Globex', N'US');
GO

INSERT INTO dbo.Customers (Name, Country)
VALUES
    (N'Initech', N'US'),
    (N'Umbrella', N'JP'),
    (N'Stark', N'US');
GO

INSERT INTO dbo.OrderArchive (OrderId, CustomerId, Total)
SELECT o.OrderId, o.CustomerId, o.Total
FROM dbo.Orders AS o
WHERE o.OrderDate < '2025-01-01';
GO

INSERT INTO dbo.AuditLog DEFAULT VALUES;
GO

INSERT INTO dbo.Report (Line) EXEC dbo.BuildReport @Year = 2026;
GO

INSERT INTO dbo.Report (Line) EXECUTE ('SELECT Line FROM dbo.Staging');
GO

-- TOP, and a CTE feeding the insert.
INSERT TOP (100) INTO dbo.Sample (OrderId)
SELECT o.OrderId FROM dbo.Orders AS o;
GO

WITH Recent AS (
    SELECT o.OrderId, o.Total FROM dbo.Orders AS o WHERE o.OrderDate >= '2026-01-01'
)
INSERT INTO dbo.RecentSnapshot (OrderId, Total)
SELECT r.OrderId, r.Total FROM Recent AS r;
GO

-- OUTPUT to the client and into a table.
INSERT INTO dbo.Customers (Name, Country)
OUTPUT inserted.CustomerId, inserted.Name
VALUES (N'Wayne', N'US');
GO

DECLARE @Inserted TABLE (CustomerId INT, Name NVARCHAR(100));

INSERT INTO dbo.Customers (Name, Country)
OUTPUT inserted.CustomerId, inserted.Name INTO @Inserted (CustomerId, Name)
VALUES (N'Tyrell', N'US');
GO

-- Identity and explicit values.
SET IDENTITY_INSERT dbo.Customers ON;
INSERT INTO dbo.Customers (CustomerId, Name, Country) VALUES (999, N'Legacy', N'GB');
SET IDENTITY_INSERT dbo.Customers OFF;
GO

-- Into a table variable and a temp table.
DECLARE @Staging TABLE (Id INT IDENTITY(1, 1) PRIMARY KEY, Name NVARCHAR(100) NOT NULL);
INSERT INTO @Staging (Name) VALUES (N'One'), (N'Two');

CREATE TABLE #Batch (Id INT, Name NVARCHAR(100));
INSERT INTO #Batch (Id, Name) SELECT s.Id, s.Name FROM @Staging AS s;
GO
