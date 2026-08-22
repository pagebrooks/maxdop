-- MERGE: every action branch and source form.

MERGE INTO dbo.Customers AS target
USING dbo.CustomerStaging AS source
    ON target.CustomerId = source.CustomerId
WHEN MATCHED THEN
    UPDATE SET target.Name = source.Name, target.Country = source.Country
WHEN NOT MATCHED BY TARGET THEN
    INSERT (CustomerId, Name, Country) VALUES (source.CustomerId, source.Name, source.Country)
WHEN NOT MATCHED BY SOURCE THEN
    DELETE;
GO

-- Conditions on the branches, and two MATCHED branches.
MERGE dbo.Inventory AS t
USING dbo.Shipment AS s
    ON t.Sku = s.Sku
WHEN MATCHED AND s.Quantity = 0 THEN
    DELETE
WHEN MATCHED AND s.Quantity > 0 THEN
    UPDATE SET t.Quantity = t.Quantity + s.Quantity
WHEN NOT MATCHED THEN
    INSERT (Sku, Quantity) VALUES (s.Sku, s.Quantity);
GO

-- A row constructor as the source, plus OUTPUT with $action.
MERGE dbo.Settings AS t
USING (VALUES (N'Timeout', N'30'), (N'Retries', N'3')) AS s (SettingName, SettingValue)
    ON t.SettingName = s.SettingName
WHEN MATCHED THEN
    UPDATE SET t.SettingValue = s.SettingValue
WHEN NOT MATCHED THEN
    INSERT (SettingName, SettingValue) VALUES (s.SettingName, s.SettingValue)
OUTPUT $action, inserted.SettingName, deleted.SettingValue;
GO

-- A query as the source, TOP, DEFAULT VALUES, and a WHERE on the OUTPUT target.
MERGE TOP (100) dbo.Totals AS t
USING (
    SELECT o.CustomerId, SUM(o.Total) AS Revenue
    FROM dbo.Orders AS o
    GROUP BY o.CustomerId
) AS s
    ON t.CustomerId = s.CustomerId
WHEN MATCHED THEN
    UPDATE SET t.Revenue = s.Revenue
WHEN NOT MATCHED BY TARGET THEN
    INSERT (CustomerId, Revenue) VALUES (s.CustomerId, s.Revenue);
GO

MERGE dbo.Flags AS t
USING dbo.FlagSource AS s ON t.FlagId = s.FlagId
WHEN NOT MATCHED THEN
    INSERT DEFAULT VALUES;
GO

DECLARE @Audit TABLE (Action NVARCHAR(10), CustomerId INT);

MERGE dbo.Customers AS t
USING dbo.CustomerStaging AS s ON t.CustomerId = s.CustomerId
WHEN MATCHED THEN
    UPDATE SET t.Name = s.Name
OUTPUT $action, ISNULL(inserted.CustomerId, deleted.CustomerId) INTO @Audit
OPTION (MAXDOP 1);
GO
