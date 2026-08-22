-- Triggers: DML and DDL.

CREATE TRIGGER dbo.trOrdersAudit
ON dbo.Orders
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.OrderAudit (OrderId, OldTotal, NewTotal, ChangedAt)
    SELECT i.OrderId, d.Total, i.Total, SYSUTCDATETIME()
    FROM inserted AS i
    LEFT JOIN deleted AS d ON d.OrderId = i.OrderId;
END;
GO

CREATE TRIGGER dbo.trOrdersDelete
ON dbo.Orders
FOR DELETE
AS
    INSERT INTO dbo.OrderAudit (OrderId, ChangedAt)
    SELECT d.OrderId, SYSUTCDATETIME() FROM deleted AS d;
GO

CREATE TRIGGER dbo.trViewInsert
ON dbo.vCustomerOrders
INSTEAD OF INSERT
AS
BEGIN
    INSERT INTO dbo.Customers (Name) SELECT i.Name FROM inserted AS i;
END;
GO

-- UPDATE() and COLUMNS_UPDATED() inside a trigger body.
CREATE TRIGGER dbo.trCustomersName
ON dbo.Customers
AFTER UPDATE
NOT FOR REPLICATION
AS
BEGIN
    IF UPDATE(Name)
        INSERT INTO dbo.NameChanges (CustomerId) SELECT i.CustomerId FROM inserted AS i;

    IF COLUMNS_UPDATED() & 2 = 2
        PRINT N'Second column changed';
END;
GO

CREATE TRIGGER dbo.trWithExecuteAs
ON dbo.Orders
WITH EXECUTE AS OWNER
AFTER DELETE
AS
    SELECT 1;
GO

-- DDL triggers, at database and server scope.
CREATE TRIGGER trDatabaseAudit
ON DATABASE
FOR CREATE_TABLE, ALTER_TABLE, DROP_TABLE
AS
BEGIN
    INSERT INTO dbo.DdlAudit (EventData) VALUES (EVENTDATA());
END;
GO

CREATE TRIGGER trServerAudit
ON ALL SERVER
FOR DDL_LOGIN_EVENTS
AS
    PRINT N'Login event';
GO

ALTER TRIGGER dbo.trOrdersDelete
ON dbo.Orders
FOR DELETE
AS
    SELECT 2;
GO

DISABLE TRIGGER dbo.trOrdersAudit ON dbo.Orders;
GO

ENABLE TRIGGER dbo.trOrdersAudit ON dbo.Orders;
GO

DROP TRIGGER IF EXISTS dbo.trWithExecuteAs;
GO
