-- UNION, UNION ALL, EXCEPT, INTERSECT.

SELECT c.Name FROM dbo.Customers AS c
UNION
SELECT s.Name FROM dbo.Suppliers AS s;
GO

SELECT o.OrderId FROM dbo.Orders AS o
UNION ALL
SELECT a.OrderId FROM dbo.OrderArchive AS a;
GO

SELECT c.CustomerId FROM dbo.Customers AS c
EXCEPT
SELECT o.CustomerId FROM dbo.Orders AS o;
GO

SELECT c.CustomerId FROM dbo.Customers AS c
INTERSECT
SELECT o.CustomerId FROM dbo.Orders AS o;
GO

-- Three or more branches, mixed operators, and an ORDER BY over the whole thing.
SELECT o.OrderId, o.Total FROM dbo.Orders AS o WHERE o.RegionId = 1
UNION ALL
SELECT o.OrderId, o.Total FROM dbo.Orders AS o WHERE o.RegionId = 2
UNION ALL
SELECT a.OrderId, a.Total FROM dbo.OrderArchive AS a
ORDER BY OrderId DESC;
GO

SELECT c.CustomerId FROM dbo.Customers AS c
EXCEPT
SELECT o.CustomerId FROM dbo.Orders AS o
INTERSECT
SELECT s.CustomerId FROM dbo.Shipments AS s;
GO

-- Parenthesised branches.
(SELECT c.Name FROM dbo.Customers AS c)
UNION ALL
(SELECT s.Name FROM dbo.Suppliers AS s);
GO

-- Inside a CTE, a derived table, and an IN predicate.
WITH AllNames AS (
    SELECT c.Name FROM dbo.Customers AS c
    UNION
    SELECT s.Name FROM dbo.Suppliers AS s
)
SELECT n.Name FROM AllNames AS n ORDER BY n.Name;
GO

SELECT combined.OrderId
FROM (
    SELECT o.OrderId FROM dbo.Orders AS o
    UNION ALL
    SELECT a.OrderId FROM dbo.OrderArchive AS a
) AS combined
WHERE combined.OrderId > 100;
GO

SELECT c.Name
FROM dbo.Customers AS c
WHERE c.CustomerId IN (
    SELECT o.CustomerId FROM dbo.Orders AS o
    UNION
    SELECT s.CustomerId FROM dbo.Shipments AS s
);
GO
