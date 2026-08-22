-- Cursors: declaration options, the fetch loop, and every FETCH direction.

DECLARE @OrderId INT, @Total DECIMAL(18, 2);

DECLARE OrderCursor CURSOR FOR
SELECT o.OrderId, o.Total FROM dbo.Orders AS o ORDER BY o.OrderId;

OPEN OrderCursor;
FETCH NEXT FROM OrderCursor INTO @OrderId, @Total;

WHILE @@FETCH_STATUS = 0
BEGIN
    PRINT CONCAT(N'Order ', @OrderId, N' = ', @Total);
    FETCH NEXT FROM OrderCursor INTO @OrderId, @Total;
END;

CLOSE OrderCursor;
DEALLOCATE OrderCursor;
GO

-- Scope and behaviour options, in the combinations the grammar allows.
DECLARE c1 CURSOR LOCAL FAST_FORWARD FOR SELECT o.OrderId FROM dbo.Orders AS o;
GO

DECLARE c2 CURSOR GLOBAL SCROLL STATIC READ_ONLY FOR SELECT o.OrderId FROM dbo.Orders AS o;
GO

DECLARE c3 CURSOR LOCAL FORWARD_ONLY KEYSET SCROLL_LOCKS FOR SELECT o.OrderId FROM dbo.Orders AS o;
GO

DECLARE c4 CURSOR LOCAL SCROLL DYNAMIC OPTIMISTIC FOR SELECT o.OrderId FROM dbo.Orders AS o;
GO

DECLARE c5 CURSOR LOCAL SCROLL FOR
SELECT o.OrderId, o.Total FROM dbo.Orders AS o
FOR UPDATE OF Total;
GO

-- The ISO form, and INSENSITIVE.
DECLARE c6 INSENSITIVE SCROLL CURSOR FOR SELECT o.OrderId FROM dbo.Orders AS o;
GO

-- Every FETCH direction against a scrollable cursor.
DECLARE @Id INT;
DECLARE ScrollCursor CURSOR SCROLL STATIC FOR SELECT o.OrderId FROM dbo.Orders AS o;

OPEN ScrollCursor;
FETCH FIRST FROM ScrollCursor INTO @Id;
FETCH NEXT FROM ScrollCursor INTO @Id;
FETCH PRIOR FROM ScrollCursor INTO @Id;
FETCH LAST FROM ScrollCursor INTO @Id;
FETCH ABSOLUTE 3 FROM ScrollCursor INTO @Id;
FETCH RELATIVE -1 FROM ScrollCursor INTO @Id;
FETCH ScrollCursor INTO @Id;
CLOSE ScrollCursor;
DEALLOCATE ScrollCursor;
GO

-- Positioned update and delete.
DECLARE UpdCursor CURSOR FOR SELECT o.Total FROM dbo.Orders AS o FOR UPDATE OF Total;
OPEN UpdCursor;
FETCH NEXT FROM UpdCursor;
UPDATE dbo.Orders SET Total = Total * 1.1 WHERE CURRENT OF UpdCursor;
DELETE FROM dbo.Orders WHERE CURRENT OF UpdCursor;
CLOSE UpdCursor;
DEALLOCATE UpdCursor;
GO

-- Nested cursors over two levels.
DECLARE @CustomerId INT;
DECLARE CustomerCursor CURSOR LOCAL FAST_FORWARD FOR
SELECT c.CustomerId FROM dbo.Customers AS c;

OPEN CustomerCursor;
FETCH NEXT FROM CustomerCursor INTO @CustomerId;

WHILE @@FETCH_STATUS = 0
BEGIN
    DECLARE @InnerId INT;
    DECLARE InnerCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT o.OrderId FROM dbo.Orders AS o WHERE o.CustomerId = @CustomerId;

    OPEN InnerCursor;
    FETCH NEXT FROM InnerCursor INTO @InnerId;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        FETCH NEXT FROM InnerCursor INTO @InnerId;
    END;

    CLOSE InnerCursor;
    DEALLOCATE InnerCursor;

    FETCH NEXT FROM CustomerCursor INTO @CustomerId;
END;

CLOSE CustomerCursor;
DEALLOCATE CustomerCursor;
GO
