CREATE PROCEDURE dbo.usp_TopReps @Start DATE, @End DATE
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP 10
        r.RepName,
        SUM(o.Total) AS Revenue,
        CASE
            WHEN SUM(o.Total) > 100000 THEN 'gold'
            WHEN SUM(o.Total) > 50000 THEN 'silver'
            ELSE 'bronze'
        END AS Tier
    FROM dbo.Orders o
    JOIN dbo.Reps r ON r.RepId = o.RepId
    WHERE o.OrderDate >= @Start AND o.OrderDate < @End AND o.Status <> 'cancelled'
    GROUP BY r.RepName
    ORDER BY Revenue DESC;
END
