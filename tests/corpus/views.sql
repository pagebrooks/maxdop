-- Views.

CREATE VIEW dbo.vCustomerOrders
AS
SELECT c.CustomerId, c.Name, o.OrderId, o.Total
FROM dbo.Customers AS c
INNER JOIN dbo.Orders AS o ON o.CustomerId = c.CustomerId;
GO

CREATE VIEW dbo.vRenamed (Id, CustomerName, Revenue)
AS
SELECT c.CustomerId, c.Name, SUM(o.Total)
FROM dbo.Customers AS c
INNER JOIN dbo.Orders AS o ON o.CustomerId = c.CustomerId
GROUP BY c.CustomerId, c.Name;
GO

CREATE VIEW dbo.vActive
WITH SCHEMABINDING
AS
SELECT c.CustomerId, c.Name
FROM dbo.Customers AS c
WHERE c.Active = 1
WITH CHECK OPTION;
GO

CREATE VIEW dbo.vMeta
WITH VIEW_METADATA, ENCRYPTION
AS
SELECT c.CustomerId FROM dbo.Customers AS c;
GO

-- An indexed view needs SCHEMABINDING, COUNT_BIG and a unique clustered index.
CREATE VIEW dbo.vOrderCounts
WITH SCHEMABINDING
AS
SELECT o.CustomerId, COUNT_BIG(*) AS OrderCount, SUM(ISNULL(o.Total, 0.00)) AS Revenue
FROM dbo.Orders AS o
GROUP BY o.CustomerId;
GO

CREATE UNIQUE CLUSTERED INDEX IX_vOrderCounts ON dbo.vOrderCounts (CustomerId);
GO

CREATE VIEW dbo.vUnion
AS
SELECT c.Name FROM dbo.Customers AS c
UNION ALL
SELECT s.Name FROM dbo.Suppliers AS s;
GO

ALTER VIEW dbo.vMeta
AS
SELECT c.CustomerId, c.Name FROM dbo.Customers AS c;
GO

CREATE OR ALTER VIEW dbo.vSimple
AS
SELECT 1 AS One;
GO

SELECT v.CustomerId FROM dbo.vCustomerOrders AS v WHERE v.Total > 100.00;
GO

DROP VIEW IF EXISTS dbo.vSimple;
GO
