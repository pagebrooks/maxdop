-- Permanent tables, temporary tables, and the ALTER TABLE surface.

CREATE TABLE dbo.Customers (
    CustomerId INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY CLUSTERED,
    Name NVARCHAR(100) NOT NULL,
    Country CHAR(2) NULL,
    Email NVARCHAR(256) NULL CONSTRAINT UQ_Customers_Email UNIQUE,
    Notes NVARCHAR(MAX) NULL,
    Balance DECIMAL(18, 2) NOT NULL CONSTRAINT DF_Customers_Balance DEFAULT (0),
    DisplayName AS (Name + N' (' + Country + N')') PERSISTED,
    RowVersion ROWVERSION,
    Guid UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Customers_Guid DEFAULT (NEWSEQUENTIALID()) ROWGUIDCOL,
    Collated NVARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
    CreatedAt DATETIME2(3) NOT NULL CONSTRAINT DF_Customers_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_Customers_Balance CHECK (Balance >= 0),
    INDEX IX_Customers_Country NONCLUSTERED (Country)
);
GO

CREATE TABLE dbo.Orders (
    OrderId INT NOT NULL,
    CustomerId INT NOT NULL,
    Total DECIMAL(18, 2) NOT NULL,
    OrderDate DATE NOT NULL,
    CONSTRAINT PK_Orders PRIMARY KEY (OrderId),
    CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId)
        REFERENCES dbo.Customers (CustomerId)
        ON DELETE CASCADE
        ON UPDATE NO ACTION,
    CONSTRAINT UQ_Orders UNIQUE NONCLUSTERED (CustomerId, OrderDate)
) ON [PRIMARY];
GO

-- Filegroup placement and a sparse column.
CREATE TABLE dbo.Wide (
    Id INT NOT NULL PRIMARY KEY,
    Payload NVARCHAR(MAX) SPARSE NULL,
    Blob VARBINARY(MAX) NULL
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];
GO

-- A system-versioned (temporal) table.
CREATE TABLE dbo.Prices (
    PriceId INT NOT NULL PRIMARY KEY,
    Amount DECIMAL(18, 2) NOT NULL,
    ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    ValidTo DATETIME2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.PricesHistory));
GO

-- Local temp table, global temp table, and SELECT INTO.
CREATE TABLE #Local (Id INT PRIMARY KEY, Name NVARCHAR(100) NULL);
GO

CREATE TABLE ##Global (Id INT NOT NULL, Payload NVARCHAR(MAX) NULL);
GO

SELECT o.OrderId, o.Total
INTO #Snapshot
FROM dbo.Orders AS o
WHERE o.OrderDate >= '2026-01-01';
GO

SELECT o.OrderId INTO #Union FROM dbo.Orders AS o UNION ALL SELECT a.OrderId FROM dbo.OrderArchive AS a;
GO

IF OBJECT_ID('tempdb..#Local') IS NOT NULL
    DROP TABLE #Local;
GO

DROP TABLE IF EXISTS #Snapshot, #Union;
GO

-- ALTER TABLE.
ALTER TABLE dbo.Customers ADD Phone NVARCHAR(30) NULL;
GO

ALTER TABLE dbo.Customers ADD
    Tier TINYINT NOT NULL CONSTRAINT DF_Customers_Tier DEFAULT (0),
    Segment NVARCHAR(20) NULL;
GO

ALTER TABLE dbo.Customers ALTER COLUMN Name NVARCHAR(200) NOT NULL;
GO

ALTER TABLE dbo.Customers DROP COLUMN Phone;
GO

ALTER TABLE dbo.Customers DROP CONSTRAINT UQ_Customers_Email;
GO

ALTER TABLE dbo.Orders WITH NOCHECK ADD CONSTRAINT CK_Orders_Total CHECK (Total >= 0);
GO

ALTER TABLE dbo.Orders NOCHECK CONSTRAINT CK_Orders_Total;
GO

ALTER TABLE dbo.Orders CHECK CONSTRAINT ALL;
GO

ALTER TABLE dbo.Orders ENABLE TRIGGER ALL;
GO

ALTER TABLE dbo.Orders SET (LOCK_ESCALATION = AUTO);
GO
