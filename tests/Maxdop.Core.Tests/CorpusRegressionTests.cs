using Maxdop.Core.Formatting;
using Maxdop.Core.Printing;

namespace Maxdop.Core.Tests;

/// <summary>
/// Minimal reproductions of defects found by the corpus run.
/// </summary>
/// <remarks>
/// Every case here is hand-written from the <em>shape</em> of the bug, never copied from a corpus
/// file, so the repo stays clean-room and the tests stay readable. The corpus
/// itself is not committed and is not available in CI.
/// </remarks>
public class CorpusRegressionTests
{
    private static string Format(string sql, int maxWidth = 120)
    {
        var options = FormatOptions.Default with { Print = PrintOptions.Default with { MaxWidth = maxWidth } };
        var result = SqlFormatter.Format(sql, options);
        if (result.Status != FormatStatus.Formatted)
        {
            Assert.Fail($"{result.Status}: {string.Join(" | ", result.Diagnostics)}");
        }

        return result.Output;
    }

    // --- terminators dropped on non-SELECT statements -------------------------------

    [Fact]
    public void BlockStatementKeepsItsTerminator()
    {
        // Only SELECT re-emitted its semicolon, so `END;` before a `GO` silently lost one.
        Assert.Equal(
            """
            BEGIN
                SELECT 1;
            END;
            GO
            """,
            Format("BEGIN SELECT 1; END;\nGO"));
    }

    [Fact]
    public void ProcedureKeepsItsTerminator()
    {
        Assert.Equal(
            """
            CREATE PROCEDURE dbo.p
            AS
            BEGIN
                SELECT 1;
            END;
            GO
            """,
            Format("CREATE PROCEDURE dbo.p AS BEGIN SELECT 1; END;\nGO"));
    }

    [Fact]
    public void SharedTerminatorIsEmittedExactlyOnce()
    {
        // A procedure and its block both end at the same `;` token. Printing is depth-first, so
        // the innermost statement claims it and the enclosing one must not add a second.
        var result = Format("CREATE PROCEDURE dbo.p AS BEGIN SELECT 1; END;");
        Assert.DoesNotContain(";;", result, StringComparison.Ordinal);
        Assert.EndsWith("END;", result, StringComparison.Ordinal);
    }

    [Fact]
    public void PassthroughBodyDoesNotGetASecondTerminator()
    {
        // The regression this exposed: a passthrough statement's verbatim text already contains
        // the semicolon, so the enclosing handled statement must not append another.
        var result = Format("CREATE PROCEDURE dbo.p AS SET NOCOUNT ON;");
        Assert.DoesNotContain(";;", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsentTerminatorIsNotInvented()
    {
        Assert.Equal(
            """
            BEGIN
                SELECT 1;
            END
            """,
            Format("BEGIN SELECT 1; END"));
    }

    // --- CREATE PROC must not be expanded to CREATE PROCEDURE -----------------------

    [Theory]
    [InlineData("create proc dbo.p as begin select 1; end", "CREATE PROC dbo.p")]
    [InlineData("create procedure dbo.p as begin select 1; end", "CREATE PROCEDURE dbo.p")]
    [InlineData("alter proc dbo.p as begin select 1; end", "ALTER PROC dbo.p")]
    [InlineData("alter procedure dbo.p as begin select 1; end", "ALTER PROCEDURE dbo.p")]
    [InlineData("create or alter proc dbo.p as begin select 1; end", "CREATE OR ALTER PROC dbo.p")]
    public void ProcedureKeywordIsPreservedAsWritten(string sql, string expectedHeader)
    {
        // `Proc` and `Procedure` are distinct token types, so expanding the abbreviation is a
        // token change the verifier rejects — and a rewrite nobody asked for.
        Assert.StartsWith(expectedHeader, Format(sql), StringComparison.Ordinal);
    }

    // --- parenthesised parameter lists ----------------------------------------------

    [Fact]
    public void ParenthesisedParameterListKeepsItsParentheses()
    {
        Assert.Equal(
            """
            CREATE PROCEDURE dbo.p (@a INT)
            AS
            BEGIN
                SELECT 1;
            END
            """,
            Format("CREATE PROCEDURE dbo.p (@a INT) AS BEGIN SELECT 1; END"));
    }

    [Fact]
    public void ParenthesisedParameterListBreaksWhenTooWide()
    {
        Assert.Equal(
            """
            CREATE PROCEDURE dbo.usp_Reconcile (
                @AsOfDate DATETIME,
                @IncludeVoided BIT = 0
            )
            AS
            BEGIN
                SELECT 1;
            END
            """,
            Format(
                "CREATE PROCEDURE dbo.usp_Reconcile (@AsOfDate DATETIME, @IncludeVoided BIT = 0) AS BEGIN SELECT 1; END",
                maxWidth: 50));
    }

    [Fact]
    public void UnparenthesisedParameterListStaysUnparenthesised()
    {
        // The author's choice survives in both directions; neither form is normalised.
        Assert.Equal(
            """
            CREATE PROCEDURE dbo.p @a INT
            AS
            BEGIN
                SELECT 1;
            END
            """,
            Format("CREATE PROCEDURE dbo.p @a INT AS BEGIN SELECT 1; END"));
    }

    // --- comment placement must be a fixed point ------------------------------------

    [Fact]
    public void CommentAfterACommaStaysAfterTheComma()
    {
        // The oscillation: a block comment after the comma classified as end-of-line and was
        // emitted as plain text, which printed it *before* the separator comma the handler added.
        // Pass two then saw `= NULL /* c */,`, reclassified it as sitting between code on both
        // sides, and moved it again. Formatting was not a fixed point.
        Assert.Equal(
            """
            CREATE PROCEDURE dbo.p
                @a INT = NULL, /* about a */
                @b INT = 0 /* about b */
            AS
            BEGIN
                SELECT 1;
            END
            """,
            Format("CREATE PROCEDURE dbo.p\n    @a INT = NULL, /* about a */\n    @b INT = 0 /* about b */\nAS\nBEGIN\nSELECT 1;\nEND"));
    }

    [Fact]
    public void CommentSharingItsLineWithFollowingCodeIsNotPushedDown()
    {
        // The complement, and the regression the first attempt at this fix introduced. This
        // comment starts its line but does *not* end it, so a break after it would move the
        // SELECT. "Starts its line" and "ends its line" are independent facts.
        Assert.Equal("/* inline */ SELECT 1;", Format("/* inline */ SELECT 1;"));
    }

    [Fact]
    public void CommentWithCodeOnBothSidesStaysBetweenThem()
    {
        Assert.Equal("SELECT 1 /* between */ + 2;", Format("SELECT 1 /* between */ + 2;"));
    }

    [Fact]
    public void EndOfLineBlockCommentIsOvertakenByPunctuation()
    {
        // Same mechanism as a `--` comment: LineSuffix is about position, not about whether the
        // comment can swallow what follows.
        Assert.Equal(
            """
            SELECT
                a, /* alpha */
                b
            FROM t
            """,
            Format("SELECT a, /* alpha */\n b FROM t", maxWidth: 200));
    }

    [Theory]
    // Every position a comment can occupy relative to a comma, in both styles.
    [InlineData("CREATE PROCEDURE dbo.p @a INT = NULL, /* after comma */ @b INT AS BEGIN SELECT 1; END")]
    [InlineData("CREATE PROCEDURE dbo.p @a INT = NULL /* before comma */, @b INT AS BEGIN SELECT 1; END")]
    [InlineData("CREATE PROCEDURE dbo.p\n  @a INT = NULL, -- after comma\n  @b INT\nAS BEGIN SELECT 1; END")]
    [InlineData("SELECT a, /* one */ b, /* two */ c FROM t;")]
    [InlineData("SELECT a /* one */, b /* two */, c FROM t;")]
    [InlineData("SELECT a, -- one\n b, -- two\n c FROM t;")]
    [InlineData("SELECT a FROM t WHERE x = 1 /* p */ AND y = 2 /* q */;")]
    [InlineData("SELECT a FROM t WHERE x = 1 -- p\n AND y = 2; -- q")]
    public void CommentPlacementIsAFixedPoint(string sql)
    {
        // Two passes is the contract; a third catches a two-cycle rather than a drift.
        var once = Format(sql);
        var twice = Format(once);
        var thrice = Format(twice);

        Assert.Equal(once, twice);
        Assert.Equal(twice, thrice);
    }

    // --- node ranges that omit their own leading keyword ----------------------------

    [Fact]
    public void ExistsKeywordIsEmittedExactlyOnce()
    {
        // `ExistsPredicate.FirstTokenIndex` points at the `(`, not at `EXISTS`. So the enclosing
        // boolean chain's operator slice read `AND EXISTS` and the handler then emitted `EXISTS`
        // again, producing `AND EXISTS EXISTS (` — output that no longer parsed.
        var result = Format("IF @a = 1 AND EXISTS (SELECT 1 FROM t) SELECT 1;");

        Assert.DoesNotContain("EXISTS EXISTS", result, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(result, "EXISTS"));
    }

    [Theory]
    [InlineData("IF EXISTS (SELECT 1 FROM t) SELECT 1;")]
    [InlineData("IF NOT EXISTS (SELECT 1 FROM t) SELECT 1;")]
    [InlineData("IF @a = 1 AND EXISTS (SELECT 1 FROM t) SELECT 1;")]
    [InlineData("IF EXISTS (SELECT 1 FROM t) AND EXISTS (SELECT 1 FROM u) SELECT 1;")]
    [InlineData("SELECT a FROM t WHERE EXISTS (SELECT 1 FROM u) AND NOT EXISTS (SELECT 1 FROM v);")]
    public void ExistsPredicatesSurviveVerification(string sql)
    {
        var once = Format(sql);
        Assert.Equal(once, Format(once));
    }

    [Fact]
    public void ForClauseKeepsItsForKeyword()
    {
        // Same trait: ForClause's range begins after `FOR`, so passthrough of it dropped the
        // keyword. The correction is shared with the operator-slice logic.
        const string sql = "SELECT a FROM t FOR XML PATH('r');";
        Assert.Equal(sql, Format(sql));
    }

    // --- BEGIN is not always a bare keyword -----------------------------------------

    [Fact]
    public void NativelyCompiledBodyPassesThroughIntact()
    {
        // `BEGIN ATOMIC WITH (...)` carries qualifiers between BEGIN and the first statement.
        // Emitting a bare BEGIN silently dropped all of them.
        const string sql = """
            CREATE PROCEDURE dbo.p
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'english')
                SELECT 1;
            END
            """;

        var result = SqlFormatter.Format(sql);
        Assert.Equal(FormatStatus.Formatted, result.Status);
        Assert.Contains("ATOMIC WITH", result.Output, StringComparison.Ordinal);
        Assert.Contains("LANGUAGE = N'english'", result.Output, StringComparison.Ordinal);
    }

    // --- own-line comments must not climb onto a keyword's line ---------------------

    [Fact]
    public void OwnLineCommentBeforeAPredicateOperandKeepsItsLine()
    {
        // The operator was glued to its operand with a space, so a comment that had its own line
        // was pulled up onto the `AND` line — and then reclassified and moved again next pass.
        Assert.Equal(
            """
            IF @a IS NOT NULL
               AND
               /* not a named instance */
               @b = 0
                SELECT 1;
            """,
            Format("IF @a IS NOT NULL\nAND\n/* not a named instance */\n@b = 0\nSELECT 1;", maxWidth: 30));
    }

    // A comment written *before* the operator is the mirror of the case above, and it used to come
    // out on the far side of it — `AND` stranded on its own line with the comment beneath it. Found
    // by diffing maxdop against the mssql extension's formatter on a comment-stress corpus; the
    // safety gates cannot see it, because a moved comment still round-trips.
    [Fact]
    public void OwnLineCommentBeforeAPredicateOperatorStaysBeforeIt()
    {
        Assert.Equal(
            """
            SELECT o.OrderId
            FROM dbo.Orders AS o
            WHERE o.Status = 'open'
                -- added for the 2021 audit
                AND o.Total > 0;
            """,
            Format("SELECT o.OrderId FROM dbo.Orders AS o\nWHERE o.Status = 'open'\n-- added for the 2021 audit\nAND o.Total > 0;"));
    }

    [Fact]
    public void OwnLineCommentBeforeAJoinConditionStaysBeforeTheOn()
    {
        Assert.Equal(
            """
            SELECT 1
            FROM dbo.A AS a
            LEFT JOIN dbo.B AS b
                -- region is optional
                ON b.Id = a.Id;
            """,
            Format("SELECT 1 FROM dbo.A AS a\nLEFT JOIN dbo.B AS b\n-- region is optional\nON b.Id = a.Id;"));
    }

    [Fact]
    public void OwnLineCommentBeforeAnAlignedPredicateOperatorStaysBeforeIt()
    {
        // The alwaysBreakWhere layout right-aligns operators, so the comment is padded to the
        // predicate column rather than the operator's.
        var options = FormatOptions.Default with { AlwaysBreakWhere = true };
        var result = SqlFormatter.Format(
            "SELECT 1 FROM dbo.A AS a\nWHERE a.X = 1\n-- why\nAND a.Y = 2;",
            options);

        Assert.Equal(FormatStatus.Formatted, result.Status);
        Assert.Equal(
            """
            SELECT 1
            FROM dbo.A AS a
            WHERE
                    a.X = 1
                    -- why
                AND a.Y = 2;
            """,
            result.Output);
    }

    [Fact]
    public void OwnLineCommentAboveAProcedureAsStaysAboveIt()
    {
        // A commented-out `WITH EXECUTE AS OWNER` is a note about the header. Printed below the AS
        // it reads as a note about the body's first statement.
        Assert.Equal(
            """
            CREATE PROCEDURE dbo.p @Version VARCHAR(30) = NULL
            -- WITH EXECUTE AS OWNER - maybe not a great idea
            AS
            BEGIN
                SELECT 1;
            END
            """,
            Format("CREATE PROCEDURE dbo.p\n@Version VARCHAR(30) = NULL\n-- WITH EXECUTE AS OWNER - maybe not a great idea\nAS\nBEGIN\nSELECT 1;\nEND"));
    }

    [Fact]
    public void OwnLineCommentBelowAProcedureAsStaysBelowIt()
    {
        // The mirror: written under the AS, it is about the body and must not be hoisted.
        Assert.Equal(
            """
            CREATE PROCEDURE dbo.p
            AS
            -- about the body
            BEGIN
                SELECT 1;
            END
            """,
            Format("CREATE PROCEDURE dbo.p\nAS\n-- about the body\nBEGIN\nSELECT 1;\nEND"));
    }

    [Fact]
    public void OwnLineCommentAboveAUnionStaysAboveIt()
    {
        Assert.Equal(
            """
            SELECT a FROM dbo.T1
            /* second result set: trending */
            UNION ALL
            SELECT a FROM dbo.T2;
            """,
            Format("SELECT a FROM dbo.T1\n/* second result set: trending */\nUNION ALL\nSELECT a FROM dbo.T2;"));
    }

    [Fact]
    public void OwnLineCommentAboveJoinKeywordsStaysAboveThem()
    {
        // Covers APPLY as well as JOIN: both are the keyword run between two table references, and
        // a comment left where it attached printed between `OUTER APPLY` and its operand.
        Assert.Equal(
            """
            SELECT 1
            FROM dbo.A AS a
            -- only rows that matched
            INNER JOIN dbo.B AS b ON b.Id = a.Id;
            """,
            Format("SELECT 1\nFROM dbo.A AS a\n-- only rows that matched\nINNER JOIN dbo.B AS b ON b.Id = a.Id;"));
    }

    [Fact]
    public void OwnLineCommentAboveAnApplyStaysAboveIt()
    {
        Assert.Equal(
            """
            SELECT 1
            FROM dbo.A AS a
            /* partitions differ */
            OUTER APPLY (
                SELECT 1 AS x
            ) AS b;
            """,
            Format("SELECT 1\nFROM dbo.A AS a\n/* partitions differ */\nOUTER APPLY (SELECT 1 AS x) AS b;"));
    }

    [Fact]
    public void OwnLineCommentAboveAConcatenationOperatorStaysAboveIt()
    {
        // This chain hangs its operators at the end of the previous line, so keeping the comment
        // above the operator means this one link leads with it instead.
        Assert.Equal(
            """
            SELECT
                CASE WHEN @a = 1 THEN 'x' END
                /*End non-columnstore case */
                + CASE WHEN @b = 2 THEN 'y' END AS Msg;
            """,
            Format("SELECT CASE WHEN @a = 1 THEN 'x' END\n/*End non-columnstore case */\n+ CASE WHEN @b = 2 THEN 'y' END AS Msg;"));
    }

    [Fact]
    public void OwnLineCommentBelowAConcatenationOperatorStaysBelowIt()
    {
        // The mirror: written under the operator, it keeps the chain's usual trailing layout.
        Assert.Equal(
            """
            SELECT
                'a' +
                /* why */
                'b' AS Msg;
            """,
            Format("SELECT 'a' +\n/* why */\n'b' AS Msg;"));
    }

    [Fact]
    public void EndOfLineCommentBeforeAConcatenationOperatorKeepsItsLine()
    {
        // sp_BlitzIndex's shape, and the one the leading-comment hoist does *not* reach: the comment
        // follows the left operand on its line, so it is a deferred line suffix. Appending the
        // operator to that line printed `END + /*End non-columnstore case */`.
        Assert.Equal(
            """
            SELECT
                CASE WHEN @a = 1 THEN 'x' ELSE 'y' END /*End non-columnstore case */
                + CASE WHEN @b = 2 THEN 'p' ELSE 'q' END AS Msg;
            """,
            Format("SELECT CASE WHEN @a = 1 THEN 'x' ELSE 'y' END /*End non-columnstore case */\n+ CASE WHEN @b = 2 THEN 'p' ELSE 'q' END AS Msg;"));
    }

    [Fact]
    public void InlineCommentBetweenConcatenationOperandsStaysInline()
    {
        // Code followed it on its line, so it is not deferred and the chain keeps its usual shape.
        Assert.Equal("SELECT 'a' /* c */ + 'b' AS Msg;", Format("SELECT 'a' /* c */ + 'b' AS Msg;"));
    }

    [Fact]
    public void OwnLineCommentsAboveAnElseStayAboveIt()
    {
        // sp_BlitzIndex heads a long else branch this way. Printed below the ELSE they read as a
        // note about the branch's first statement.
        Assert.Equal(
            """
            IF @a = 1
            BEGIN
                SELECT 1;
            END
            --If @TableName is NOT specified...
            --Act based on the @Mode and @Filter.
            ELSE
            BEGIN
                SELECT 2;
            END
            """,
            Format("IF @a = 1\nBEGIN\nSELECT 1;\nEND\n--If @TableName is NOT specified...\n--Act based on the @Mode and @Filter.\nELSE\nBEGIN\nSELECT 2;\nEND"));
    }

    [Fact]
    public void CommentAfterAnElseKeywordStaysOnItsLine()
    {
        // The mirror, and the one the printer already handled: written after the keyword, it is
        // about the branch and shares the ELSE's line as the author wrote it.
        Assert.Equal(
            """
            IF @a = 1
            BEGIN
                SELECT 1;
            END
            ELSE -- No instance installed
            BEGIN
                SELECT 2;
            END
            """,
            Format("IF @a = 1\nBEGIN\nSELECT 1;\nEND\nELSE -- No instance installed\nBEGIN\nSELECT 2;\nEND"));
    }

    [Fact]
    public void HoistedElseCommentKeepsTheElseIfLadderFlat()
    {
        // A hoisted comment must not push the nested IF onto its own line: `ELSE IF` is one
        // construct to a reader, and indenting each rung marches a long chain off the margin.
        Assert.Equal(
            """
            IF @a = 1
                SELECT 1;
            -- otherwise try b
            ELSE IF @b = 2
                SELECT 2;
            ELSE
                SELECT 3;
            """,
            Format("IF @a = 1\nSELECT 1;\n-- otherwise try b\nELSE IF @b = 2\nSELECT 2;\nELSE\nSELECT 3;"));
    }

    [Fact]
    public void DefensiveSemicolonStaysWithTheStatementItGuards()
    {
        // sp_BlitzFirst opens a block this way. The semicolon sits outside the statement's range, so
        // the block's head slice swallowed it and printed `BEGIN ;` with the WITH orphaned below.
        Assert.Equal(
            """
            IF @s = 0
            BEGIN
                ;WITH c AS (
                    SELECT 1 AS x
                )
                SELECT x FROM c;
            END
            """,
            Format("IF @s = 0\nBEGIN\n;WITH c AS (SELECT 1 AS x)\nSELECT x FROM c;\nEND"));
    }

    [Fact]
    public void CommentAboveADefensiveSemicolonStaysAboveIt()
    {
        Assert.Equal(
            """
            IF @s = 0
            BEGIN
                /* Measure waits in hours */
                ;WITH c AS (
                    SELECT 1 AS x
                )
                SELECT x FROM c;
            END
            """,
            Format("IF @s = 0\nBEGIN\n/* Measure waits in hours */\n;WITH c AS (SELECT 1 AS x)\nSELECT x FROM c;\nEND"));
    }

    [Fact]
    public void SemicolonOnTheBeginsOwnLineStaysWithTheBegin()
    {
        // `BEGIN;` is an empty statement, not a guard, and the line the author used is what tells
        // the two apart.
        Assert.Equal(
            """
            IF @s = 0
            BEGIN;
                SELECT 1;
            END
            """,
            Format("IF @s = 0\nBEGIN;\nSELECT 1;\nEND"));
    }

    [Fact]
    public void OwnLineCommentAboveAFunctionAsStaysAboveIt()
    {
        // ScriptDom's own FunctionStatementTests.sql documents a function this way. Printed below the
        // AS it reads as a note about the body's first statement.
        Assert.Equal(
            """
            CREATE FUNCTION dbo.f (@a INT)
            RETURNS INT
            /* documents the function */
            AS
            BEGIN
                RETURN 1
            END
            """,
            Format("CREATE FUNCTION dbo.f (@a int)\nRETURNS int\n/* documents the function */\nAS\nBEGIN\nRETURN 1\nEND"));
    }

    [Fact]
    public void OwnLineCommentBelowAFunctionAsStaysBelowIt()
    {
        Assert.Equal(
            """
            CREATE FUNCTION dbo.f (@a INT)
            RETURNS INT
            AS
            -- about the body
            BEGIN
                RETURN 1
            END
            """,
            Format("CREATE FUNCTION dbo.f (@a int)\nRETURNS int\nAS\n-- about the body\nBEGIN\nRETURN 1\nEND"));
    }

    [Fact]
    public void OwnLineCommentAboveAViewAsStaysAboveIt()
    {
        Assert.Equal(
            """
            CREATE VIEW dbo.v
            /* what this view is for */
            AS
            SELECT 1 AS x;
            """,
            Format("CREATE VIEW dbo.v\n/* what this view is for */\nAS\nSELECT 1 AS x;"));
    }

    [Fact]
    public void OwnLineCommentBelowAViewAsStaysBelowIt()
    {
        Assert.Equal(
            """
            CREATE VIEW dbo.v
            AS
            /* about the query */
            SELECT 1 AS x;
            """,
            Format("CREATE VIEW dbo.v\nAS\n/* about the query */\nSELECT 1 AS x;"));
    }

    [Fact]
    public void OwnLineCommentAboveATriggerAsStaysAboveIt()
    {
        Assert.Equal(
            """
            CREATE TRIGGER dbo.t
            ON dbo.T
            AFTER INSERT
            /* what this trigger is for */
            AS
            BEGIN
                SELECT 1;
            END
            """,
            Format("CREATE TRIGGER dbo.t ON dbo.T AFTER INSERT\n/* what this trigger is for */\nAS\nBEGIN\nSELECT 1;\nEND"));
    }

    [Fact]
    public void CommentAboveAViewAsFollowsItsQualifiers()
    {
        // The documented limitation of the AS hoist: with qualifiers present the comment lands below
        // them rather than above, which is still the correct side of the AS.
        Assert.Equal(
            """
            CREATE VIEW dbo.v (x)
            WITH SCHEMABINDING
            /* documents the view */
            AS
            SELECT 1 AS x;
            """,
            Format("CREATE VIEW dbo.v (x)\nWITH SCHEMABINDING\n/* documents the view */\nAS\nSELECT 1 AS x;"));
    }

    [Fact]
    public void OwnLineCommentAboveBeginCatchLabelsTheBlock()
    {
        // `-- Catch body` labels the block, so it stays above it. The TRY half already read
        // correctly, because a comment above BEGIN TRY leads the whole statement.
        Assert.Equal(
            """
            BEGIN TRY
                SELECT 1
            END TRY
            -- Catch body
            BEGIN CATCH
                SELECT 2
            END CATCH
            """,
            Format("begin try\nselect 1\nend try\n-- Catch body\nbegin catch\nselect 2\nend catch"));
    }

    [Fact]
    public void CommentInsideACatchBlockStaysInside()
    {
        Assert.Equal(
            """
            BEGIN TRY
                SELECT 1
            END TRY
            BEGIN CATCH
                -- Catch body
                SELECT 2
            END CATCH
            """,
            Format("begin try\nselect 1\nend try\nbegin catch\n-- Catch body\nselect 2\nend catch"));
    }

    [Fact]
    public void HeaderCommentsStayAboveStrayLeadingSemicolons()
    {
        // The `;;` belongs to no batch, so it was emitted as an unowned region *before* the header
        // comments the author wrote above it.
        Assert.Equal(
            """
            -- see the other file
            ;;
            CREATE TABLE t1 (
                c1 INT
            )
            """,
            Format("-- see the other file\n;;\ncreate table t1(c1 int)"));
    }

    [Fact]
    public void HeaderCommentStaysAboveALeadingGo()
    {
        // The comment leads the *next* batch's first statement, so the GO — owned by the empty batch
        // before it — was printed above the file's own header.
        Assert.Equal(
            """
            -- header
            GO
            SELECT 1;
            """,
            Format("-- header\nGO\nSELECT 1;"));
    }

    [Fact]
    public void CommentAfterAGoStaysWithTheFollowingBatch()
    {
        Assert.Equal(
            """
            SELECT 0;
            GO
            -- about the next batch
            SELECT 1;
            """,
            Format("SELECT 0;\nGO\n-- about the next batch\nSELECT 1;"));
    }

    [Fact]
    public void CommentThenGoAtEndOfFileKeepsTheGo()
    {
        // Zero batches did not mean zero tokens: the GO belongs to no node, and returning only the
        // dangling comments dropped it. The verifier caught it and refused the file.
        Assert.Equal(
            """
            -- header
            GO
            """,
            Format("-- header\nGO"));
    }

    [Fact]
    public void CommentOnlyFileStillFormats()
    {
        Assert.Equal("-- just a comment", Format("-- just a comment"));
    }

    [Fact]
    public void CommentsBetweenLeadingGosStayWhereTheyWereWritten()
    {
        // ScriptDom gives a whole run of leading GOs to one batch, so every comment among them
        // attaches to the statement after the last one. Leaving them to that owner printed the GOs
        // first and the comments after them, and the two comments came out in the wrong order — which
        // the verifier caught and refused, rather than shuffling them.
        Assert.Equal(
            """
            -- c1
            GO
            -- c2
            GO
            SELECT 1;
            """,
            Format("-- c1\nGO\n-- c2\nGO\nSELECT 1;"));
    }

    [Fact]
    public void CommentsBetweenThreeLeadingGosKeepTheirOrder()
    {
        Assert.Equal(
            """
            -- c1
            GO
            -- c2
            GO
            -- c3
            GO
            SELECT 1;
            """,
            Format("-- c1\nGO\n-- c2\nGO\n-- c3\nGO\nSELECT 1;"));
    }

    [Fact]
    public void LineCommentBeforeATerminatorKeepsTheTerminatorBelowIt()
    {
        // sp_Blitz ends a predicate with a `--` comment and puts the semicolon on the line below. The
        // comment is deferred as a line suffix, so appending the terminator to that line emitted it
        // ahead of the comment. It cannot go to the comment's left either — a `--` comment runs to
        // end of line and would swallow it — so the terminator keeps the line the author gave it.
        Assert.Equal(
            """
            SELECT 1
            FROM dbo.sysalerts
            WHERE [enabled] = 1 --bitmask: 1 = email
            ;
            """,
            Format("SELECT 1\nFROM dbo.sysalerts\nWHERE [enabled] = 1 --bitmask: 1 = email\n;", maxWidth: 40));
    }

    [Fact]
    public void CommentAfterATerminatorStaysAfterIt()
    {
        Assert.Equal("SELECT 1; -- note", Format("SELECT 1; -- note"));
    }

    [Fact]
    public void DeferredCommentAfterTheFirstOfTwoTerminatorsStaysAfterIt()
    {
        // The mirror of the case above, and the regression fixing it introduced: this comment is
        // deferred and pending when the terminator is emitted too, but it was written on the far
        // side of it, so flushing it first carried it across the semicolon. sp_Blitz has one.
        Assert.Equal(
            """
            SELECT 1 FROM t WHERE a = 1; /*no read permissions*/
            ;
            """,
            Format("SELECT 1\nFROM t\nWHERE a = 1; /*no read permissions*/\n;"));
    }

    [Fact]
    public void InlineBlockCommentDoesNotDisplaceTheTerminator()
    {
        // Not deferred, so the boundary is a no-op and the terminator stays where it belongs.
        Assert.Equal("SELECT 1 /* note */;", Format("SELECT 1 /* note */;"));
    }

    [Fact]
    public void EmptyCatchHoldingACommentIsFormattedRatherThanDeclined()
    {
        // An empty StatementList has the range [-1..-1], so no node owns a comment inside the block
        // and the attacher gives it to the try body as a trailing comment. The statement used to be
        // passed through whole to stop it migrating there — 68% of all text the First Responder Kit
        // left unformatted, seven procedures frozen over one comment apiece.
        Assert.Equal(
            """
            BEGIN TRY
                SELECT 1;
            END TRY
            BEGIN CATCH
                /* if we cannot read it, skip */
            END CATCH
            """,
            Format("BEGIN TRY\nSELECT 1;\nEND TRY\nBEGIN CATCH\n/* if we cannot read it, skip */\nEND CATCH"));
    }

    [Fact]
    public void EmptyCatchWithoutACommentStillFormats()
    {
        Assert.Equal(
            """
            BEGIN TRY
                SELECT 1;
            END TRY
            BEGIN CATCH
            END CATCH
            """,
            Format("BEGIN TRY\nSELECT 1;\nEND TRY\nBEGIN CATCH\nEND CATCH"));
    }

    [Fact]
    public void TrailingCommentOnATryBodyIsNotPulledIntoTheCatch()
    {
        // The mirror: a comment that really does belong to the try body must not be swept up by the
        // block's new willingness to emit comments itself.
        Assert.Equal(
            """
            BEGIN TRY
                SELECT 1; -- about the select
            END TRY
            BEGIN CATCH
                SELECT 2;
            END CATCH
            """,
            Format("BEGIN TRY\nSELECT 1; -- about the select\nEND TRY\nBEGIN CATCH\nSELECT 2;\nEND CATCH"));
    }

    [Fact]
    public void CommentInsideAKeywordRunIsKeptRatherThanDropped()
    {
        // Keyword slices reproduce their tokens as text and used to skip comments outright, so the
        // survival gate saw the count fall and the whole file was refused. 263 such positions across
        // twenty small files, found by fuzzing a comment into every token boundary.
        Assert.Equal("BEGIN /*fz*/ TRANSACTION;", Format("BEGIN /*fz*/ TRANSACTION;"));
        Assert.Equal("SET /*fz*/ NOCOUNT ON;", Format("SET /*fz*/ NOCOUNT ON;"));
    }

    [Fact]
    public void CommentInsideNotNullIsKept()
    {
        // The nullable constraint rebuilt `NOT NULL` from SignificantTextBetween, which ignores
        // comments — a second way to drop one, independent of the slice.
        Assert.Equal(
            """
            CREATE TABLE dbo.t (
                a INT NOT /*fz*/ NULL
            );
            """,
            Format("CREATE TABLE dbo.t (a INT NOT /*fz*/ NULL);"));
    }

    [Fact]
    public void OwnLineCommentInsideAKeywordRunKeepsItsLine()
    {
        Assert.Equal(
            """
            CREATE TABLE dbo.t (
                a INT NOT
                /*fz*/
                NULL
            );
            """,
            Format("CREATE TABLE dbo.t (a INT NOT\n/*fz*/\nNULL);"));
    }

    [Fact]
    public void CommentInsideAPassedThroughNodesOwnKeywordsIsNotDoubled()
    {
        // A passed-through node emits its source verbatim starting at EffectiveFirstToken, which
        // reaches back over the keywords its range excludes. A comment written among them is both a
        // leading comment of the node and inside that text, and was printed twice.
        Assert.Equal("SELECT a FROM t FOR /*fz*/ JSON PATH;", Format("SELECT a FROM t FOR /*fz*/ JSON PATH;"));
        Assert.Equal("SELECT a FROM t FOR JSON /*fz*/ PATH;", Format("SELECT a FROM t FOR JSON /*fz*/ PATH;"));
    }

    [Fact]
    public void CommentAboveAWindowFrameIsKept()
    {
        // The frame is sliced rather than Printed, so nothing else would emit its comments; the
        // slice starts at the first of them instead of at the clause.
        Assert.Equal(
            """
            SELECT
                SUM(t.a) OVER (
                    PARTITION BY t.b
                    ORDER BY t.c
                    /*fz*/
                    ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                ) AS r
            FROM t;
            """,
            Format("SELECT SUM(t.a) OVER (PARTITION BY t.b ORDER BY t.c\n/*fz*/\nROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS r FROM t;"));
    }

    [Fact]
    public void CommentTrailingASlicedNameIsKept()
    {
        // The method name is emitted as a slice, so the gap between it and the argument list belonged
        // to no emitter; the slice now ends past the comments attached to the name.
        Assert.Equal(
            "SELECT n.value('@id', 'INT') AS Id FROM @Xml.nodes /*fz*/ ('/root/item') AS t (n);",
            Format("SELECT n.value('@id', 'INT') AS Id\nFROM @Xml.nodes /*fz*/ ('/root/item') AS t(n);"));
    }

    [Fact]
    public void CommentOrphanedBeforeAClosingParenIsKept()
    {
        // The attacher hands this one to the next node it can find — a WITH option past the return
        // type — which is emitted by a slice starting well beyond it, so nothing emitted it at all.
        // The handler knows where the author put it even though no node does.
        Assert.Equal(
            """
            CREATE FUNCTION dbo.f (
                @Since DATE
                /*fz*/
            )
            RETURNS TABLE WITH SCHEMABINDING AS RETURN SELECT 1 AS x
            """,
            Format("CREATE FUNCTION dbo.f (@Since DATE\n/*fz*/\n)\nRETURNS TABLE\nWITH SCHEMABINDING\nAS\nRETURN\n    SELECT 1 AS x"));
    }

    [Theory]
    [InlineData("IF @s = 0\nBEGIN\n;WITH c AS (SELECT 1 AS x)\nSELECT x FROM c;\nEND")]
    [InlineData("CREATE FUNCTION dbo.f (@a int)\nRETURNS int\n/* why */\nAS\nBEGIN\nRETURN 1\nEND")]
    [InlineData("CREATE VIEW dbo.v\n/* why */\nAS\nSELECT 1 AS x;")]
    [InlineData("CREATE VIEW dbo.v\nAS\n/* why */\nSELECT 1 AS x;")]
    [InlineData("CREATE TRIGGER dbo.t ON dbo.T AFTER INSERT\n/* why */\nAS\nBEGIN\nSELECT 1;\nEND")]
    [InlineData("CREATE TRIGGER dbo.t ON dbo.T AFTER INSERT\nAS\n/* why */\nBEGIN\nSELECT 1;\nEND")]
    [InlineData("CREATE FUNCTION dbo.f (@a int)\nRETURNS int\nAS\n/* why */\nBEGIN\nRETURN 1\nEND")]
    [InlineData("IF @s = 0\nBEGIN\n/* why */\n;WITH c AS (SELECT 1 AS x)\nSELECT x FROM c;\nEND")]
    [InlineData("IF @s = 0\nBEGIN;\nSELECT 1;\nEND")]
    [InlineData("IF @a = 1\nSELECT 1;\n-- why\nELSE\nSELECT 2;")]
    [InlineData("IF @a = 1\nSELECT 1;\nELSE -- why\nSELECT 2;")]
    [InlineData("SELECT CASE WHEN @a = 1 THEN 'x' ELSE 'y' END /* end */\n+ 'b' AS Msg;")]
    [InlineData("SELECT 'a' /* c */ + 'b' AS Msg;")]
    [InlineData("SELECT 'a'\n/* why */\n+ 'b' AS Msg;")]
    [InlineData("SELECT 'a' +\n/* why */\n'b' AS Msg;")]
    [InlineData("SELECT 1 FROM dbo.A AS a\n-- why\nINNER JOIN dbo.B AS b ON b.Id = a.Id;")]
    [InlineData("SELECT 1 FROM dbo.A AS a\n/* why */\nOUTER APPLY (SELECT 1 AS x) AS b;")]
    [InlineData("SELECT 1 FROM dbo.A AS a\nCROSS\n/* why */\nAPPLY (SELECT 1 AS x) AS b;")]
    [InlineData("CREATE PROCEDURE dbo.p\n@a INT\n-- why\nAS\nBEGIN\nSELECT 1;\nEND")]
    [InlineData("SELECT a FROM t1\n/* why */\nUNION ALL\nSELECT a FROM t2;")]
    [InlineData("SELECT a FROM t WHERE @a = 1\n-- why\nAND @b = 2;")]
    [InlineData("SELECT 1 FROM dbo.A AS a LEFT JOIN dbo.B AS b\n-- why\nON b.Id = a.Id;")]
    [InlineData("IF @a = 1\nAND\n/* why */\n@b = 2\nSELECT 1;")]
    [InlineData("SELECT a FROM t WHERE @a = 1\nAND\n/* why */\n@b = 2;")]
    [InlineData("SELECT a FROM t\nWHERE\n/* the condition */\n@a = 1;")]
    [InlineData("begin try\nselect 1\nend try\n-- Catch body\nbegin catch\nselect 2\nend catch")]
    [InlineData("-- see the other file\n;;\ncreate table t1(c1 int)")]
    [InlineData("-- header\nGO\nSELECT 1;")]
    [InlineData("-- header\nGO")]
    [InlineData("-- c1\nGO\n-- c2\nGO\nSELECT 1;")]
    [InlineData("-- c1\nGO\n-- c2\nGO\n-- c3\nGO\nSELECT 1;")]
    [InlineData("SELECT 1 FROM t WHERE a = 1 --bitmask: 1 = email\n;")]
    [InlineData("SELECT 1; -- note")]
    [InlineData("SELECT 1 /* note */;")]
    [InlineData("SELECT 1 FROM t WHERE a = 1; /*no read permissions*/\n;")]
    [InlineData("BEGIN TRY\nSELECT 1;\nEND TRY\nBEGIN CATCH\n/* skip */\nEND CATCH")]
    [InlineData("BEGIN TRY\nSELECT 1;\nEND TRY\nBEGIN CATCH\nEND CATCH")]
    [InlineData("BEGIN /*fz*/ TRANSACTION;")]
    [InlineData("CREATE TABLE dbo.t (a INT NOT /*fz*/ NULL);")]
    [InlineData("CREATE TABLE dbo.t (a INT NOT\n/*fz*/\nNULL);")]
    [InlineData("SELECT a FROM t FOR /*fz*/ JSON PATH;")]
    [InlineData("SELECT SUM(t.a) OVER (PARTITION BY t.b ORDER BY t.c\n/*fz*/\nROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS r FROM t;")]
    [InlineData("SELECT n.value('@id', 'INT') AS Id\nFROM @Xml.nodes /*fz*/ ('/root/item') AS t(n);")]
    [InlineData("CREATE FUNCTION dbo.f (@Since DATE\n/*fz*/\n)\nRETURNS TABLE\nWITH SCHEMABINDING\nAS\nRETURN\n    SELECT 1 AS x")]
    [InlineData("SELECT STRING_AGG(a, N',')\n/*fz*/\nWITHIN GROUP (ORDER BY a) AS x FROM t;")]
    public void OwnLineCommentsInPredicatesAreAFixedPoint(string sql)
    {
        var once = Format(sql, maxWidth: 40);
        var twice = Format(once, maxWidth: 40);
        Assert.Equal(once, twice);
        Assert.Equal(twice, Format(twice, maxWidth: 40));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    // --- tokens no statement owns ----------------------------------------------------

    [Fact]
    public void DoubledTerminatorIsPreserved()
    {
        // A statement's range really can end in two semicolons. The dispatcher emitted one.
        var result = Format("EXECUTE sp_who;\n;\nSELECT 1;");
        Assert.Equal(2, CountOccurrences(result[..result.IndexOf("SELECT", StringComparison.Ordinal)], ";"));
    }

    [Fact]
    public void PlainGoGainsNoCount()
    {
        Assert.Equal(
            """
            SELECT 1;
            GO
            """,
            Format("SELECT 1;\nGO"));
    }

    [Theory]
    [InlineData("EXECUTE sp_who;\n;\nSELECT 1;")]
    [InlineData("SELECT a FROM t TABLESAMPLE (1000 ROWS);")]
    public void UnownedTokensAreStable(string sql)
    {
        var once = Format(sql);
        Assert.Equal(once, Format(once));
    }

    // --- derived table column alias lists -------------------------------------------

    [Theory]
    [InlineData("SELECT * FROM (SELECT a FROM t) AS d (c1);")]
    [InlineData("SELECT * FROM (SELECT a FROM t) d (c1);")]
    [InlineData("SELECT * FROM (SELECT a, b FROM t) AS d (c1, c2);")]
    public void DerivedTableColumnAliasListSurvives(string sql)
    {
        // `) AS d (c1)` renames the derived table's output columns. Those live on
        // TableReferenceWithAliasAndColumns, one inheritance level above the alias, and were dropped
        // entirely — the single cause behind the largest cluster of corpus refusals.
        var result = Format(sql);

        Assert.Contains("(c1", result.Replace(" (c1", "(c1", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void DerivedTableColumnListFormats()
    {
        Assert.Equal(
            """
            SELECT *
            FROM (
                SELECT a FROM t
            ) AS d (c1)
            """,
            Format("select * from (select a from t) as d (c1)", maxWidth: 30));
    }

    // --- StatementList can carry comments -------------------------------------------

    [Fact]
    public void CommentedOutProcedureQualifierSurvives()
    {
        // A commented-out `WITH EXECUTE AS OWNER` between the parameter list and AS attaches to the
        // procedure's StatementList — which, contrary to the assumption elsewhere, sometimes has a
        // real token range and so stays in the fragment tree. Emitting the body through
        // PrintStatements instead of Print bypassed the dispatcher and dropped it.
        var result = Format("CREATE PROCEDURE dbo.p\n  @a INT = NULL -- about a\n-- WITH EXECUTE AS OWNER\nAS\nBEGIN\n  SELECT 1;\nEND");

        Assert.Contains("-- about a", result, StringComparison.Ordinal);
        Assert.Contains("-- WITH EXECUTE AS OWNER", result, StringComparison.Ordinal);
    }

    [Fact]
    public void CommentBeforeABlockBodySurvives()
    {
        var result = Format("IF @a = 1\n-- why we do this\nBEGIN\n  SELECT 1;\nEND");
        Assert.Contains("-- why we do this", result, StringComparison.Ordinal);
    }

    // --- tokens belonging to no batch ------------------------------------------------

    [Fact]
    public void LeadingSemicolonsBeforeTheFirstBatchArePreserved()
    {
        // `;;CREATE …` puts stray semicolons *outside* every batch's range — a batch begins at its
        // first statement. PrintScript now walks a token cursor so every significant token is
        // somebody's responsibility.
        Assert.Contains(";;", Format(";;CREATE TABLE dbo.t (a INT);"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(";;SELECT 1;")]
    [InlineData(";SELECT 1;\nGO")]
    public void UnownedScriptTokensAreStable(string sql)
    {
        var once = Format(sql);
        Assert.Equal(once, Format(once));
    }

    // --- graph predicates -------------------------------------------------------------

    [Fact]
    public void GraphMatchPredicatePassesThroughIntact()
    {
        // `MATCH(a AND b)` gives its operands ranges that start *after* `MATCH(`, so the enclosing
        // boolean chain read its operator as `) AND MATCH (` — dropping the first `MATCH(` and
        // smuggling the second into the operator text. Output that no longer parsed.
        const string sql = "SELECT * FROM NODE AS N, EDGE AS E, NODE AS N2 WHERE MATCH(N-(E)->N2);";
        Assert.Equal(sql, Format(sql));
    }

    [Fact]
    public void BooleanChainOperatorMustReallyBeAndOrOr()
    {
        // The guard behind the fix above: only AND and OR exist as boolean binary operators, so
        // anything else in the gap means an operand's range does not cover its own text.
        Assert.Equal("SELECT a FROM t WHERE x = 1 AND y = 2", Format("select a from t where x = 1 and y = 2"));
        Assert.Equal("SELECT a FROM t WHERE x = 1 OR y = 2", Format("select a from t where x = 1 or y = 2"));
    }

    // --- comments on intermediate nodes ----------------------------------------------

    [Fact]
    public void CommentBeforeTheFirstColumnSurvives()
    {
        // Attaches to the TableDefinition, which CREATE TABLE never routes through Print because it
        // interleaves four child lists back into source order. Third instance of the same mistake,
        // which is why there is now a named `WithComments` helper for it.
        var result = Format("CREATE TABLE dbo.t (\n    -- Column level tests\n    a INT\n);");

        Assert.Contains("-- Column level tests", result, StringComparison.Ordinal);
        Assert.Equal(result, Format(result));
    }

    // --- comments the OPTION and CTE work exposed -------------------------------------

    [Fact]
    public void CommentBeforeAnOptionClauseIsNotDropped()
    {
        // The `OPTION (…)` slice is emitted through CasedTokens, which skips comments because they
        // are normally emitted via attachment — but this region belongs to no node the printer
        // visits, so there is nothing for a comment here to attach to. Four corpus files lost a
        // `/* why this hint */` line this way, caught only by the comment-preservation gate.
        var sql = "SELECT a\nFROM t\nWHERE b = 1\n/* No need for a GROUP BY. */\nOPTION (RECOMPILE);";
        var result = Format(sql);

        Assert.Contains("/* No need for a GROUP BY. */", result, StringComparison.Ordinal);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void OwnLineCommentBeforeAComparisonOperandStaysOnItsLine()
    {
        // `LTRIM(…) =` then a comment on its own line then `LTRIM(…)`. A plain space before the
        // right-hand operand dragged the comment onto the operator's line, where it has code to its
        // left — so the next pass reclassified it as end-of-line and moved it again.
        var sql = "SELECT a\nFROM t\nWHERE LTRIM(x) =\n    /* trimmed to compare */\n    LTRIM(y);";
        var result = Format(sql);

        Assert.Contains("/* trimmed to compare */", result, StringComparison.Ordinal);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void OwnLineCommentBeforeAnAssignmentAliasExpressionStaysOnItsLine()
    {
        // The `alias = expression` select-list form, with a CASE and an explanatory comment above
        // it. Same defect as the comparison operand, in a different handler.
        var sql = "SELECT\n    wait_hms =\n    /* the more wait time, the less accurate */\n    CASE WHEN a > 1 THEN 'x' ELSE 'y' END\nFROM t;";
        var result = Format(sql);

        Assert.Contains("/* the more wait time, the less accurate */", result, StringComparison.Ordinal);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void CommentBetweenACteAndItsStatementDoesNotJumpAboveTheWith()
    {
        // The comment attaches as a leading comment of the InsertSpecification, and wrapping the
        // CTE prologue inside `WithComments(specification, …)` emitted it before the WITH — above
        // the very clauses it followed. The prologue has to sit outside that wrapper.
        var sql = "WITH c AS (\n    SELECT 1 AS x\n)\n/* 1-second floor keeps the signal */\nINSERT INTO #t (a)\nSELECT x FROM c;";
        var result = Format(sql);

        Assert.Contains("/* 1-second floor keeps the signal */", result, StringComparison.Ordinal);
        Assert.True(
            result.IndexOf("WITH c AS", StringComparison.Ordinal)
            < result.IndexOf("/* 1-second floor", StringComparison.Ordinal),
            $"comment jumped above the WITH:\n{result}");
        Assert.Equal(result, Format(result));
    }

    // --- guards that were declining more than they protected --------------------------

    [Fact]
    public void LongConcatenationChainDoesNotHitTheDepthBackstop()
    {
        // Procedures that build a message with `N'…' + x + N'…' + y + …` nest one BinaryExpression
        // per term, and the corpus has chains long enough to trip MaxDepth — which showed up as the
        // depth backstop firing on ordinary code. Flattening the left spine, as the boolean-chain and
        // join handlers already did, makes depth proportional to real nesting instead of term count.
        var terms = string.Join(" + ", Enumerable.Range(0, 400).Select(i => $"N'part{i}'"));
        var result = Format($"SELECT {terms};");

        Assert.Contains("N'part0'", result, StringComparison.Ordinal);
        Assert.Contains("N'part399'", result, StringComparison.Ordinal);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void XmlNamespacesShareTheirWithClauseWithTheCteList()
    {
        // `WITH XMLNAMESPACES(…), cte AS (…)` is the normal way to write an XML-shredding query, and
        // declining it was the largest single over-eager bail-out in the corpus. The namespace
        // block's own range starts at its first declaration, not at the XMLNAMESPACES keyword.
        var result = Format(
            "WITH XMLNAMESPACES('http://schemas.microsoft.com/sqlserver/2004/07/showplan' AS p),"
            + " ops AS (select 1 as x) select x from ops;");

        Assert.Contains("XMLNAMESPACES('http://schemas.microsoft.com/sqlserver/2004/07/showplan' AS p)", result, StringComparison.Ordinal);
        Assert.Contains("ops AS (", result, StringComparison.Ordinal);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void XmlNamespacesWithNoCteAtAllStillFormats()
    {
        // `WITH XMLNAMESPACES('…' AS p) UPDATE …` is a complete statement with no CTE, and requiring
        // at least one CTE in the WITH clause declined it — 6% of all declined text, across the
        // UPDATE and INSERT handlers together.
        var result = Format(
            "WITH XMLNAMESPACES('http://schemas.microsoft.com/sqlserver/2004/07/showplan' AS p)"
            + " update r set op = 1 from t r where r.x = 1;");

        Assert.StartsWith("WITH XMLNAMESPACES(", result, StringComparison.Ordinal);
        Assert.Contains("UPDATE r", result, StringComparison.Ordinal);
        Assert.DoesNotContain("AS p),", result, StringComparison.Ordinal);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void InlineColumnConstraintIsNotEmittedTwice()
    {
        // A column-level constraint appears both nested inside its ColumnDefinition and in the
        // table's own constraint list. Left in the element list it made consecutive elements overlap
        // rather than be comma-separated — and had the separator check passed, it would have been
        // emitted twice.
        var result = Format("create table t1 (a1 int UNIQUE, a2 int CONSTRAINT C2 UNIQUE NONCLUSTERED);");

        Assert.Equal(
            """
            CREATE TABLE t1 (
                a1 INT UNIQUE,
                a2 INT CONSTRAINT C2 UNIQUE NONCLUSTERED
            );
            """,
            result);
    }

    [Fact]
    public void ExistsPredicateInsideAnAndChainDoesNotDeclineTheWholeClause()
    {
        // A boolean chain's range begins where its leftmost operand begins, and an ExistsPredicate's
        // range begins at the `(` — so `WHERE EXISTS (…) AND x = 1` made the clause's keyword slice
        // read `WHERE EXISTS`, fail the "exactly WHERE" check, and decline the clause. The range
        // correction has to propagate up the left spine, not just apply to the node itself.
        Assert.Equal(
            "SELECT a FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.id = t.id) AND t.x = 1;",
            Format("select a from t where exists (select 1 from u where u.id = t.id) and t.x = 1;"));
    }

    [Fact]
    public void SystemVersioningClauseSurvivesTheTableOptionSlice()
    {
        // The reason table options were deferred: `SystemVersioningTableOption` stops at the history
        // table name, two closing parentheses short of its own clause, so a slice measured from the
        // option node dropped them. Measuring to the statement's end and verifying the option ends
        // inside that range is what makes it safe.
        var result = Format(
            "CREATE TABLE dbo.t (a INT NOT NULL, s DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,"
            + " e DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL, PERIOD FOR SYSTEM_TIME (s, e))"
            + " WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.tHistory));");

        Assert.Contains("SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.tHistory)", result, StringComparison.Ordinal);
        Assert.Equal(result, Format(result));
    }

    // --- everything above must survive verification and be stable -------------------

    [Theory]
    [InlineData("BEGIN SELECT 1; END;\nGO")]
    [InlineData("CREATE PROC dbo.p (@a INT = NULL) AS BEGIN SELECT 1; END;\nGO")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.p AS SET NOCOUNT ON;")]
    [InlineData("SELECT a, SUM(b) FROM t GROUP BY a WITH ROLLUP")]
    [InlineData("SELECT TRIM('[]' FROM a) FROM t;")]
    public void CorpusShapesAreStable(string sql)
    {
        var once = Format(sql);
        Assert.Equal(once, Format(once));
    }
}
