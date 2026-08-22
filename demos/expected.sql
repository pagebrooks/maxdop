-- Weekly revenue by rep, with a running total.
CREATE PROCEDURE dbo.usp_RepRevenue @Start DATE, @End DATE
AS
BEGIN
    SET NOCOUNT ON;
    WITH sales AS (
        SELECT r.RepId, r.RepName, sum(o.Total) AS Revenue
        FROM dbo.Orders o
        JOIN dbo.Reps r ON r.RepId = o.RepId
        WHERE o.OrderDate >= @Start AND o.OrderDate < @End AND o.Status <> 'cancelled'
        GROUP BY r.RepId, r.RepName
    )
    SELECT
        s.RepName,
        s.Revenue,
        sum(s.Revenue) OVER (ORDER BY s.Revenue DESC ROWS UNBOUNDED PRECEDING) AS RunningTotal,
        CASE
            WHEN s.Revenue > 100000 THEN 'A'
            WHEN s.Revenue > 50000 THEN 'B'
            ELSE 'C'
        END AS Tier
    FROM sales s
    ORDER BY s.Revenue DESC;
END
