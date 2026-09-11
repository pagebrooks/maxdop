-- Revenue by rep, banded.
CREATE PROCEDURE dbo.usp_Revenue @Start DATE
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        r.RepName,
        SUM(o.Total) AS Revenue,
        CASE
            WHEN SUM(o.Total) > 100000 THEN 'A'
            WHEN SUM(o.Total) > 50000 THEN 'B'
            ELSE 'C'
        END AS Tier
    FROM dbo.Orders o
    JOIN dbo.Reps r ON r.RepId = o.RepId
    WHERE o.OrderDate >= @Start
    GROUP BY r.RepName
    ORDER BY Revenue DESC;
END
