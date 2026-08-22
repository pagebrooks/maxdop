-- Control flow and error handling.

IF @@ROWCOUNT = 0
    PRINT N'Nothing';
GO

IF EXISTS (SELECT 1 FROM dbo.Orders AS o WHERE o.Total > 1000.00)
BEGIN
    PRINT N'Large orders exist';
END
ELSE
BEGIN
    PRINT N'None';
END;
GO

DECLARE @i INT = 0;

IF @i = 0
BEGIN
    IF @i < 10
        SET @i = 1;
    ELSE IF @i < 20
        SET @i = 2;
    ELSE
        SET @i = 3;
END;
GO

DECLARE @n INT = 0;

WHILE @n < 10
BEGIN
    SET @n += 1;

    IF @n = 3
        CONTINUE;

    IF @n = 8
        BREAK;

    PRINT @n;
END;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    INSERT INTO dbo.Orders (OrderId, CustomerId, Total, OrderDate)
    VALUES (1, 1, 10.00, '2026-01-01');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;

    DECLARE
        @ErrorMessage NVARCHAR(4000) = ERROR_MESSAGE(),
        @ErrorSeverity INT = ERROR_SEVERITY(),
        @ErrorState INT = ERROR_STATE(),
        @ErrorNumber INT = ERROR_NUMBER(),
        @ErrorLine INT = ERROR_LINE(),
        @ErrorProcedure NVARCHAR(128) = ERROR_PROCEDURE();

    RAISERROR(@ErrorMessage, @ErrorSeverity, @ErrorState);
END CATCH;
GO

BEGIN TRY
    THROW 50001, N'Explicit failure', 1;
END TRY
BEGIN CATCH
    THROW;
END CATCH;
GO

RAISERROR(N'Formatted %s and %d', 16, 1, N'text', 42);
GO

RAISERROR(N'With options', 10, 1) WITH NOWAIT;
GO

-- GOTO and a label.
DECLARE @Attempts INT = 0;

Retry:
SET @Attempts += 1;

IF @Attempts < 3
    GOTO Retry;
GO

WAITFOR DELAY '00:00:01';
GO

-- RETURN from a batch, and a nested BEGIN...END.
BEGIN
    BEGIN
        PRINT N'Nested';
    END;
END;
GO

IF 1 = 0
    RETURN;
GO
