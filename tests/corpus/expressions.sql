-- Expressions: conditionals, conversion, predicates, and the common built-ins.

-- CASE, both forms, including nested and inside an aggregate.
SELECT
    CASE o.Status
        WHEN 0 THEN N'New'
        WHEN 1 THEN N'Paid'
        ELSE N'Unknown'
    END AS StatusName,
    CASE
        WHEN o.Total > 1000.00 THEN N'Large'
        WHEN o.Total > 100.00 THEN N'Medium'
        ELSE N'Small'
    END AS Band,
    CASE WHEN o.Status = 1 THEN CASE WHEN o.Total > 0 THEN 1 ELSE 0 END ELSE NULL END AS Nested,
    SUM(CASE WHEN o.Status = 1 THEN o.Total ELSE 0 END) OVER (PARTITION BY o.CustomerId) AS PaidTotal
FROM dbo.Orders AS o;
GO

-- Null handling.
SELECT
    ISNULL(o.Notes, N'') AS Notes,
    COALESCE(o.ShippedDate, o.OrderDate, SYSUTCDATETIME()) AS EffectiveDate,
    NULLIF(o.Total, 0.00) AS NonZeroTotal,
    IIF(o.Total > 100.00, N'big', N'small') AS Size,
    CHOOSE(o.Status + 1, N'New', N'Paid', N'Shipped') AS StatusName
FROM dbo.Orders AS o;
GO

-- Conversion, including the TRY_ forms and styles.
SELECT
    CAST(o.Total AS NVARCHAR(30)) AS AsText,
    CONVERT(NVARCHAR(30), o.OrderDate, 120) AS Iso,
    TRY_CAST(o.Notes AS INT) AS MaybeInt,
    TRY_CONVERT(DATE, o.Notes, 103) AS MaybeDate,
    PARSE(N'2026-01-01' AS DATE) AS Parsed,
    CAST(o.Total AS DECIMAL(18, 4)) AS Widened
FROM dbo.Orders AS o;
GO

-- Predicates.
SELECT o.OrderId
FROM dbo.Orders AS o
WHERE o.Total BETWEEN 10.00 AND 100.00
  AND o.Status IN (0, 1, 2)
  AND o.Notes LIKE N'%urgent!%' ESCAPE N'!'
  AND o.ShippedDate IS NULL
  AND NOT (o.Status = 3 OR o.Total < 0)
  AND o.CustomerId > ANY (SELECT c.CustomerId FROM dbo.Customers AS c)
  AND EXISTS (SELECT 1 FROM dbo.Shipments AS s WHERE s.OrderId = o.OrderId);
GO

-- Arithmetic, string and bitwise operators.
SELECT
    (o.Total * 1.2) - 5.00 + 1.00 AS Adjusted,
    o.Total / 3 AS Divided,
    o.Status % 2 AS Parity,
    -o.Total AS Negated,
    o.Flags & 4 AS Masked,
    o.Flags | 8 AS [Set],
    o.Flags ^ 1 AS Toggled,
    ~o.Flags AS Inverted,
    N'a' + N'b' + N'c' AS Concatenated
FROM dbo.Orders AS o;
GO

-- String, date and aggregate built-ins.
SELECT
    LEN(c.Name) AS NameLength,
    LEFT(c.Name, 3) AS Prefix,
    RIGHT(c.Name, 3) AS Suffix,
    SUBSTRING(c.Name, 2, 5) AS Middle,
    UPPER(c.Name) AS Upper,
    LOWER(c.Name) AS Lower,
    LTRIM(RTRIM(c.Name)) AS Trimmed,
    REPLACE(c.Name, N' ', N'_') AS Slugged,
    REVERSE(c.Name) AS Reversed,
    CHARINDEX(N'a', c.Name) AS FirstA,
    PATINDEX(N'%a%', c.Name) AS PatternA,
    CONCAT(c.Name, N' - ', c.Country) AS Label,
    CONCAT_WS(N', ', c.Name, c.Country) AS Joined,
    FORMAT(SYSUTCDATETIME(), N'yyyy-MM-dd') AS Formatted,
    STUFF(c.Name, 1, 1, N'X') AS Stuffed,
    REPLICATE(N'-', 10) AS Line,
    SPACE(4) AS Gap
FROM dbo.Customers AS c;
GO

SELECT
    GETDATE() AS Local,
    SYSDATETIME() AS Precise,
    SYSUTCDATETIME() AS Utc,
    DATEADD(DAY, 7, o.OrderDate) AS Later,
    DATEDIFF(DAY, o.OrderDate, SYSUTCDATETIME()) AS Age,
    DATEPART(YEAR, o.OrderDate) AS OrderYear,
    DATENAME(MONTH, o.OrderDate) AS OrderMonth,
    YEAR(o.OrderDate) AS Y,
    MONTH(o.OrderDate) AS M,
    DAY(o.OrderDate) AS D,
    EOMONTH(o.OrderDate) AS MonthEnd,
    DATEFROMPARTS(2026, 1, 1) AS Built
FROM dbo.Orders AS o;
GO

SELECT
    COUNT(*) AS Rows,
    COUNT(DISTINCT o.CustomerId) AS Customers,
    COUNT_BIG(*) AS BigRows,
    SUM(o.Total) AS Revenue,
    AVG(o.Total) AS Average,
    MIN(o.Total) AS Smallest,
    MAX(o.Total) AS Largest,
    STDEV(o.Total) AS StdDev,
    VAR(o.Total) AS Variance,
    STRING_AGG(CAST(o.OrderId AS NVARCHAR(10)), N',') AS Ids
FROM dbo.Orders AS o;
GO

-- Ordered set and JSON functions.
SELECT STRING_AGG(c.Name, N', ') WITHIN GROUP (ORDER BY c.Name) AS Names
FROM dbo.Customers AS c;
GO

SELECT
    JSON_VALUE(o.Payload, N'$.id') AS Id,
    JSON_QUERY(o.Payload, N'$.items') AS Items,
    ISJSON(o.Payload) AS Valid,
    JSON_MODIFY(o.Payload, N'$.id', 1) AS Updated
FROM dbo.Orders AS o
WHERE ISJSON(o.Payload) = 1;
GO

SELECT j.Id, j.Name
FROM OPENJSON(@Payload, N'$.customers')
WITH (
    Id INT N'$.id',
    Name NVARCHAR(100) N'$.name',
    Tags NVARCHAR(MAX) N'$.tags' AS JSON
) AS j;
GO

-- XML methods.
SELECT
    @Xml.value('(/root/@id)[1]', 'INT') AS Id,
    @Xml.query('/root/child') AS Children,
    @Xml.exist('/root') AS HasRoot
FROM (SELECT 1 AS x) AS dummy;
GO

SELECT n.value('@id', 'INT') AS Id
FROM @Xml.nodes('/root/item') AS t(n);
GO

-- System and metadata functions.
SELECT
    @@VERSION AS Version,
    @@SPID AS Spid,
    @@ROWCOUNT AS Rows,
    @@IDENTITY AS LastIdentity,
    SCOPE_IDENTITY() AS ScopeIdentity,
    IDENT_CURRENT(N'dbo.Orders') AS TableIdentity,
    NEWID() AS Guid,
    DB_NAME() AS DatabaseName,
    OBJECT_NAME(OBJECT_ID(N'dbo.Orders')) AS ObjectName,
    SCHEMA_NAME() AS SchemaName,
    SUSER_SNAME() AS LoginName,
    CURRENT_TIMESTAMP AS Now,
    CURRENT_USER AS CurrentUser,
    SESSION_USER AS SessionUser,
    SYSTEM_USER AS SystemUser,
    HOST_NAME() AS HostName;
GO
