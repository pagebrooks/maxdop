-- Functions: scalar, inline table-valued, and multi-statement table-valued.

CREATE FUNCTION dbo.fnCustomerTotal (@CustomerId INT)
RETURNS DECIMAL(18, 2)
AS
BEGIN
    DECLARE @Total DECIMAL(18, 2);

    SELECT @Total = SUM(o.Total)
    FROM dbo.Orders AS o
    WHERE o.CustomerId = @CustomerId;

    RETURN ISNULL(@Total, 0.00);
END;
GO

-- Defaults, several parameters, and the schema-binding options.
CREATE FUNCTION dbo.fnDiscount (@Total DECIMAL(18, 2), @Rate DECIMAL(5, 4) = 0.1000)
RETURNS DECIMAL(18, 2)
WITH SCHEMABINDING, RETURNS NULL ON NULL INPUT
AS
BEGIN
    RETURN @Total * (1 - @Rate);
END;
GO

CREATE FUNCTION dbo.fnNoArgs ()
RETURNS INT
AS
BEGIN
    RETURN 1;
END;
GO

-- Inline table-valued: the body is a single query.
CREATE FUNCTION dbo.fnOrdersForCustomer (@CustomerId INT)
RETURNS TABLE
AS
RETURN (
    SELECT o.OrderId, o.Total, o.OrderDate
    FROM dbo.Orders AS o
    WHERE o.CustomerId = @CustomerId
);
GO

CREATE FUNCTION dbo.fnRecentOrders (@Since DATE)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT o.OrderId, o.Total
    FROM dbo.Orders AS o
    WHERE o.OrderDate >= @Since;
GO

-- Multi-statement table-valued: the return type is a table variable.
CREATE FUNCTION dbo.fnCustomerSummary (@CustomerId INT)
RETURNS @Summary TABLE (
    CustomerId INT NOT NULL PRIMARY KEY,
    OrderCount INT NOT NULL,
    Revenue DECIMAL(18, 2) NULL
)
AS
BEGIN
    INSERT INTO @Summary (CustomerId, OrderCount, Revenue)
    SELECT o.CustomerId, COUNT(*), SUM(o.Total)
    FROM dbo.Orders AS o
    WHERE o.CustomerId = @CustomerId
    GROUP BY o.CustomerId;

    IF NOT EXISTS (SELECT 1 FROM @Summary)
    BEGIN
        INSERT INTO @Summary (CustomerId, OrderCount, Revenue) VALUES (@CustomerId, 0, NULL);
    END;

    RETURN;
END;
GO

-- ALTER and CREATE OR ALTER.
ALTER FUNCTION dbo.fnNoArgs ()
RETURNS INT
AS
BEGIN
    RETURN 2;
END;
GO

CREATE OR ALTER FUNCTION dbo.fnSquare (@Value INT)
RETURNS INT
AS
BEGIN
    RETURN @Value * @Value;
END;
GO

-- Calling them, in each position a function can appear.
SELECT dbo.fnCustomerTotal(c.CustomerId) AS Total FROM dbo.Customers AS c;
GO

SELECT o.OrderId FROM dbo.fnOrdersForCustomer(1) AS o;
GO

SELECT s.Revenue FROM dbo.fnCustomerSummary(1) AS s;
GO

SELECT c.CustomerId, o.OrderId
FROM dbo.Customers AS c
CROSS APPLY dbo.fnOrdersForCustomer(c.CustomerId) AS o;
GO

DROP FUNCTION IF EXISTS dbo.fnSquare;
GO
