-- DELETE and TRUNCATE.

DELETE FROM dbo.Sessions WHERE ExpiresAt < SYSUTCDATETIME();
GO

DELETE dbo.Sessions WHERE SessionId = 1;
GO

DELETE TOP (1000) FROM dbo.AuditLog WHERE CreatedAt < '2024-01-01';
GO

-- The two-FROM form.
DELETE o
FROM dbo.Orders AS o
INNER JOIN dbo.Customers AS c ON c.CustomerId = o.CustomerId
WHERE c.Active = 0;
GO

DELETE FROM dbo.Orders
FROM dbo.Orders AS o
LEFT JOIN dbo.Customers AS c ON c.CustomerId = o.CustomerId
WHERE c.CustomerId IS NULL;
GO

-- From a CTE.
WITH Duplicates AS (
    SELECT o.OrderId, ROW_NUMBER() OVER (PARTITION BY o.ExternalId ORDER BY o.OrderId) AS rn
    FROM dbo.Orders AS o
)
DELETE FROM Duplicates WHERE rn > 1;
GO

-- OUTPUT.
DELETE FROM dbo.Sessions
OUTPUT deleted.SessionId, deleted.UserId
WHERE ExpiresAt < SYSUTCDATETIME();
GO

DECLARE @Deleted TABLE (SessionId INT);

DELETE FROM dbo.Sessions
OUTPUT deleted.SessionId INTO @Deleted (SessionId)
WHERE UserId = 7;
GO

DELETE FROM dbo.Sessions WITH (ROWLOCK) WHERE SessionId = 2 OPTION (MAXDOP 1);
GO

TRUNCATE TABLE dbo.Staging;
GO
