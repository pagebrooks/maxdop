-- UPDATE: every assignment and source form.

UPDATE dbo.Customers SET Active = 0 WHERE LastOrderDate < '2024-01-01';
GO

UPDATE dbo.Orders
SET
    Total = 129.99,
    UpdatedAt = SYSUTCDATETIME(),
    Notes = NULL
WHERE OrderId = 42;
GO

-- Compound assignment operators.
UPDATE dbo.Counters
SET
    Hits += 1,
    Misses -= 1,
    Weight *= 2,
    Ratio /= 2,
    Remainder %= 3,
    Mask &= 12,
    Flags |= 4,
    Toggle ^= 1
WHERE CounterId = 1;
GO

-- The two-FROM form, with a join.
UPDATE o
SET o.CustomerName = c.Name
FROM dbo.Orders AS o
INNER JOIN dbo.Customers AS c ON c.CustomerId = o.CustomerId
WHERE o.CustomerName IS NULL;
GO

UPDATE dbo.Orders
SET Total = Total * 1.2
FROM dbo.Orders AS o
INNER JOIN dbo.Regions AS r ON r.RegionId = o.RegionId
WHERE r.TaxRate > 0;
GO

-- TOP, a CTE target, and a variable assigned in the same statement.
UPDATE TOP (10) dbo.Orders SET Processed = 1 WHERE Processed = 0;
GO

WITH Stale AS (
    SELECT o.OrderId FROM dbo.Orders AS o WHERE o.UpdatedAt < '2025-01-01'
)
UPDATE Stale SET OrderId = OrderId;
GO

DECLARE @NewTotal DECIMAL(18, 2);
UPDATE dbo.Orders SET @NewTotal = Total = Total * 1.1 WHERE OrderId = 42;
GO

-- OUTPUT, both destinations.
UPDATE dbo.Orders
SET Total = Total * 1.05
OUTPUT deleted.Total AS OldTotal, inserted.Total AS NewTotal
WHERE RegionId = 3;
GO

DECLARE @Changes TABLE (OrderId INT, OldTotal DECIMAL(18, 2), NewTotal DECIMAL(18, 2));

UPDATE dbo.Orders
SET Total = Total * 1.05
OUTPUT inserted.OrderId, deleted.Total, inserted.Total INTO @Changes
WHERE RegionId = 4;
GO

-- Hints and a query hint clause.
UPDATE dbo.Orders WITH (ROWLOCK)
SET Processed = 1
WHERE Processed = 0
OPTION (MAXDOP 1);
GO
