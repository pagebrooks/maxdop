-- ============================================================
-- Spike fixture. Hand-written, clean-room, no employer content.
-- Every comment here sits somewhere that commonly breaks formatters.
-- ============================================================

/* leading block comment before the batch */
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE PROCEDURE dbo.usp_Reconcile_Ledger
    @AsOfDate      DATETIME,          -- trailing comment on a parameter
    /* interior block comment between parameters */
    @IncludeVoided BIT           = 0,
    @Debug         BIT           = 0
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @RowCount INT = 0;

    -- comment immediately before a CTE
    ;WITH Postings AS
    (
        SELECT  p.LedgerId
               ,p.Amount        -- leading-comma style, trailing comment
               ,p.PostedAt
        FROM    dbo.Posting AS p WITH (NOLOCK)   /* legacy hint, deliberately kept */
        WHERE   p.PostedAt <= @AsOfDate
        AND     (@IncludeVoided = 1 OR p.VoidedAt IS NULL)
    ),
    Rollup AS (
        SELECT LedgerId, SUM(Amount) AS Total -- aggregate
        FROM Postings
        GROUP BY LedgerId
        HAVING SUM(Amount) <> 0
    )
    SELECT      l.LedgerId
              , l.Name
              , r.Total
              , CASE WHEN r.Total < 0 THEN 'CREDIT' /* inline in a CASE */
                     WHEN r.Total = 0 THEN 'ZERO'
                     ELSE 'DEBIT' END AS Sign
    FROM        dbo.Ledger AS l
    INNER JOIN  Rollup     AS r ON r.LedgerId = l.LedgerId
    LEFT JOIN   dbo.LedgerOverride AS o
            ON  o.LedgerId = l.LedgerId
            AND o.EffectiveFrom <= @AsOfDate    -- multi-predicate join
    ORDER BY    l.Name;

    SET @RowCount = @@ROWCOUNT;

    IF @Debug = 1 -- comment on an IF
    BEGIN
        RAISERROR(N'Reconcile touched %d rows', 0, 1, @RowCount) WITH NOWAIT;
    END
    ELSE
    BEGIN
        /* nothing to do */
        PRINT 'quiet';
    END

    BEGIN TRY
        MERGE dbo.LedgerSnapshot AS tgt
        USING (SELECT @AsOfDate AS AsOfDate) AS src
           ON tgt.AsOfDate = src.AsOfDate
        WHEN MATCHED THEN
            UPDATE SET tgt.RowCountAtDate = @RowCount
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (AsOfDate, RowCountAtDate) VALUES (src.AsOfDate, @RowCount)
        OUTPUT $action, inserted.AsOfDate;   -- OUTPUT on a MERGE
    END TRY
    BEGIN CATCH
        DECLARE @Msg NVARCHAR(4000) = ERROR_MESSAGE();
        THROW;  -- rethrow, 2012+
    END CATCH

    WHILE @RowCount > 0
    BEGIN
        SET @RowCount -= 1;   -- compound assignment
    END
END
GO

-- trailing comment after the final GO, with no trailing newline below
EXEC dbo.usp_Reconcile_Ledger @AsOfDate = '2026-01-01', @Debug = 1;
GO
/* very last thing in the file is a block comment */
