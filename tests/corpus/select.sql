-- SELECT: projection, filtering, joins, grouping, windowing, set-returning shapes.

SELECT 1;
GO

SELECT * FROM dbo.Orders;
GO

SELECT o.OrderId, o.OrderDate, o.Total
FROM dbo.Orders AS o
WHERE o.Total > 100.00
ORDER BY o.OrderDate DESC;
GO

SELECT DISTINCT TOP (10) c.Country
FROM dbo.Customers AS c
WHERE c.Country IS NOT NULL;
GO

SELECT TOP (5) PERCENT WITH TIES o.OrderId, o.Total
FROM dbo.Orders AS o
ORDER BY o.Total DESC;
GO

-- Aliases, both spellings.
SELECT o.OrderId AS Id, Amount = o.Total, [Order Date] = o.OrderDate
FROM dbo.Orders AS o;
GO

-- Every join kind, including the older comma form and a cross join.
SELECT c.Name, o.OrderId, s.Name AS Shipper
FROM dbo.Customers AS c
INNER JOIN dbo.Orders AS o ON o.CustomerId = c.CustomerId
LEFT OUTER JOIN dbo.Shipments AS sh ON sh.OrderId = o.OrderId
RIGHT OUTER JOIN dbo.Shippers AS s ON s.ShipperId = sh.ShipperId
FULL OUTER JOIN dbo.Returns AS r ON r.OrderId = o.OrderId
WHERE c.Active = 1;
GO

SELECT c.Name, r.RegionName
FROM dbo.Customers AS c, dbo.Regions AS r
WHERE c.RegionId = r.RegionId;
GO

SELECT c.Name, n.Number
FROM dbo.Customers AS c
CROSS JOIN dbo.Numbers AS n;
GO

-- APPLY, both forms.
SELECT c.CustomerId, latest.OrderId
FROM dbo.Customers AS c
CROSS APPLY (
    SELECT TOP (1) o.OrderId
    FROM dbo.Orders AS o
    WHERE o.CustomerId = c.CustomerId
    ORDER BY o.OrderDate DESC
) AS latest;
GO

SELECT c.CustomerId, f.Total
FROM dbo.Customers AS c
OUTER APPLY dbo.fnCustomerTotal(c.CustomerId) AS f;
GO

-- Derived table, table hints, and a self join.
SELECT summary.CustomerId, summary.OrderCount
FROM (
    SELECT o.CustomerId, COUNT(*) AS OrderCount
    FROM dbo.Orders AS o WITH (NOLOCK)
    GROUP BY o.CustomerId
) AS summary
WHERE summary.OrderCount > 3;
GO

SELECT e.Name AS Employee, m.Name AS Manager
FROM dbo.Employees AS e
LEFT JOIN dbo.Employees AS m ON m.EmployeeId = e.ManagerId;
GO

-- Grouping, including the roll-up forms.
SELECT o.CustomerId, COUNT(*) AS Orders, SUM(o.Total) AS Revenue, AVG(o.Total) AS Average
FROM dbo.Orders AS o
GROUP BY o.CustomerId
HAVING SUM(o.Total) > 1000.00
ORDER BY Revenue DESC;
GO

SELECT o.Region, o.Country, SUM(o.Total) AS Revenue
FROM dbo.Orders AS o
GROUP BY ROLLUP (o.Region, o.Country);
GO

SELECT o.Region, o.Country, SUM(o.Total) AS Revenue
FROM dbo.Orders AS o
GROUP BY CUBE (o.Region, o.Country);
GO

SELECT o.Region, o.Country, SUM(o.Total) AS Revenue
FROM dbo.Orders AS o
GROUP BY GROUPING SETS ((o.Region, o.Country), (o.Region), ());
GO

SELECT o.Region, SUM(o.Total) AS Revenue
FROM dbo.Orders AS o
GROUP BY o.Region WITH ROLLUP;
GO

-- Window functions.
SELECT
    o.OrderId,
    o.CustomerId,
    ROW_NUMBER() OVER (PARTITION BY o.CustomerId ORDER BY o.OrderDate DESC) AS Recency,
    RANK() OVER (ORDER BY o.Total DESC) AS TotalRank,
    DENSE_RANK() OVER (ORDER BY o.Total DESC) AS DenseRank,
    NTILE(4) OVER (ORDER BY o.Total) AS Quartile,
    SUM(o.Total) OVER (PARTITION BY o.CustomerId) AS CustomerTotal,
    LAG(o.Total, 1, 0.00) OVER (PARTITION BY o.CustomerId ORDER BY o.OrderDate) AS Previous,
    LEAD(o.Total) OVER (PARTITION BY o.CustomerId ORDER BY o.OrderDate) AS Next,
    SUM(o.Total) OVER (
        PARTITION BY o.CustomerId
        ORDER BY o.OrderDate
        ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
    ) AS RunningTotal
FROM dbo.Orders AS o;
GO

-- Paging.
SELECT o.OrderId
FROM dbo.Orders AS o
ORDER BY o.OrderId
OFFSET 20 ROWS FETCH NEXT 10 ROWS ONLY;
GO

-- Subqueries in every position.
SELECT
    c.Name,
    (SELECT COUNT(*) FROM dbo.Orders AS o WHERE o.CustomerId = c.CustomerId) AS OrderCount
FROM dbo.Customers AS c
WHERE c.CustomerId IN (SELECT o.CustomerId FROM dbo.Orders AS o WHERE o.Total > 500.00)
  AND EXISTS (SELECT 1 FROM dbo.Shipments AS s WHERE s.CustomerId = c.CustomerId)
  AND c.RegionId <> ALL (SELECT r.RegionId FROM dbo.Regions AS r WHERE r.Retired = 1);
GO

-- Common table expressions, including a recursive one and several in a list.
WITH RecentOrders AS (
    SELECT o.OrderId, o.CustomerId, o.Total
    FROM dbo.Orders AS o
    WHERE o.OrderDate >= '2026-01-01'
)
SELECT r.CustomerId, SUM(r.Total) AS Revenue
FROM RecentOrders AS r
GROUP BY r.CustomerId;
GO

WITH Totals AS (
    SELECT o.CustomerId, SUM(o.Total) AS Revenue
    FROM dbo.Orders AS o
    GROUP BY o.CustomerId
),
Ranked AS (
    SELECT t.CustomerId, t.Revenue, ROW_NUMBER() OVER (ORDER BY t.Revenue DESC) AS Position
    FROM Totals AS t
)
SELECT r.CustomerId, r.Revenue
FROM Ranked AS r
WHERE r.Position <= 10;
GO

WITH OrgChart (EmployeeId, ManagerId, Depth) AS (
    SELECT e.EmployeeId, e.ManagerId, 0
    FROM dbo.Employees AS e
    WHERE e.ManagerId IS NULL
    UNION ALL
    SELECT e.EmployeeId, e.ManagerId, oc.Depth + 1
    FROM dbo.Employees AS e
    INNER JOIN OrgChart AS oc ON oc.EmployeeId = e.ManagerId
)
SELECT oc.EmployeeId, oc.Depth
FROM OrgChart AS oc
OPTION (MAXRECURSION 100);
GO

-- PIVOT and UNPIVOT.
SELECT p.Region, p.[2025], p.[2026]
FROM (
    SELECT o.Region, YEAR(o.OrderDate) AS OrderYear, o.Total
    FROM dbo.Orders AS o
) AS src
PIVOT (SUM(src.Total) FOR src.OrderYear IN ([2025], [2026])) AS p;
GO

SELECT u.Region, u.OrderYear, u.Revenue
FROM dbo.RegionRevenue AS r
UNPIVOT (Revenue FOR OrderYear IN ([Y2025], [Y2026])) AS u;
GO

-- Query hints.
SELECT o.OrderId
FROM dbo.Orders AS o
WHERE o.CustomerId = @CustomerId
OPTION (RECOMPILE, MAXDOP 1, OPTIMIZE FOR (@CustomerId = 1));
GO

-- FOR clauses.
SELECT o.OrderId, o.Total FROM dbo.Orders AS o FOR XML PATH('Order'), ROOT('Orders');
GO

SELECT o.OrderId, o.Total FROM dbo.Orders AS o FOR JSON PATH, ROOT('orders');
GO

SELECT o.OrderId FROM dbo.Orders AS o FOR BROWSE;
GO
