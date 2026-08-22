-- Indexes and statistics.

CREATE INDEX IX_Orders_CustomerId ON dbo.Orders (CustomerId);
GO

CREATE UNIQUE NONCLUSTERED INDEX IX_Customers_Email ON dbo.Customers (Email);
GO

CREATE CLUSTERED INDEX IX_Staging_Id ON dbo.Staging (Id ASC);
GO

CREATE NONCLUSTERED INDEX IX_Orders_Covering
ON dbo.Orders (CustomerId ASC, OrderDate DESC)
INCLUDE (Total, Processed)
WHERE Processed = 0
WITH (
    FILLFACTOR = 90,
    PAD_INDEX = ON,
    ONLINE = OFF,
    DATA_COMPRESSION = PAGE,
    IGNORE_DUP_KEY = OFF,
    STATISTICS_NORECOMPUTE = OFF,
    SORT_IN_TEMPDB = ON,
    DROP_EXISTING = OFF
)
ON [PRIMARY];
GO

CREATE COLUMNSTORE INDEX IX_Orders_Columnstore ON dbo.Orders (CustomerId, Total);
GO

CREATE CLUSTERED COLUMNSTORE INDEX IX_Archive_Columnstore ON dbo.OrderArchive;
GO

ALTER INDEX IX_Orders_CustomerId ON dbo.Orders REBUILD;
GO

ALTER INDEX ALL ON dbo.Orders REBUILD WITH (ONLINE = ON, MAXDOP = 4);
GO

ALTER INDEX IX_Orders_CustomerId ON dbo.Orders REORGANIZE;
GO

ALTER INDEX IX_Orders_CustomerId ON dbo.Orders DISABLE;
GO

ALTER INDEX IX_Orders_CustomerId ON dbo.Orders SET (ALLOW_PAGE_LOCKS = OFF);
GO

DROP INDEX IX_Orders_CustomerId ON dbo.Orders;
GO

DROP INDEX IF EXISTS IX_Customers_Email ON dbo.Customers;
GO

CREATE STATISTICS ST_Orders_Total ON dbo.Orders (Total) WITH FULLSCAN;
GO

CREATE STATISTICS ST_Orders_Sample ON dbo.Orders (CustomerId, OrderDate)
WITH SAMPLE 25 PERCENT, NORECOMPUTE;
GO

UPDATE STATISTICS dbo.Orders;
GO

UPDATE STATISTICS dbo.Orders (ST_Orders_Total) WITH FULLSCAN, NORECOMPUTE;
GO

DROP STATISTICS dbo.Orders.ST_Orders_Sample;
GO
