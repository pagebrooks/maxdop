-- Dynamic SQL: both execution forms, parameterised and concatenated.

DECLARE @Sql NVARCHAR(MAX);
DECLARE @Table SYSNAME = N'Orders';
DECLARE @Schema SYSNAME = N'dbo';
DECLARE @CustomerId INT = 1;
DECLARE @RowCount INT;

SET @Sql = N'SELECT COUNT(*) FROM ' + QUOTENAME(@Schema) + N'.' + QUOTENAME(@Table);
EXEC (@Sql);
GO

DECLARE @Sql NVARCHAR(MAX) = N'SELECT @Count = COUNT(*) FROM dbo.Orders WHERE CustomerId = @Id';
DECLARE @Count INT;

EXEC sys.sp_executesql
    @Sql,
    N'@Id INT, @Count INT OUTPUT',
    @Id = 1,
    @Count = @Count OUTPUT;
GO

-- Concatenated across several literals and variables, which is how it is usually written.
DECLARE @Columns NVARCHAR(MAX) = N'OrderId, Total';
DECLARE @Where NVARCHAR(MAX) = N'Total > 100';
DECLARE @Query NVARCHAR(MAX);

SET @Query = N'SELECT ' + @Columns + N'
FROM dbo.Orders
WHERE ' + @Where + N'
ORDER BY OrderId;';

EXEC (@Query);
GO

-- The multi-part EXEC form, where the parts are concatenated by position.
DECLARE @Part1 NVARCHAR(4000) = N'SELECT ';
DECLARE @Part2 NVARCHAR(4000) = N'1';
EXEC (@Part1 + @Part2);
GO

-- Executing at a linked server, and switching principal around a call.
EXEC ('SELECT 1') AT DataSource;
GO

EXECUTE AS USER = N'someuser';
EXEC dbo.GetCustomer @CustomerId = 1;
REVERT;
GO
