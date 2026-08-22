using Maxdop.Core.Formatting;
using Maxdop.Core.Printing;

namespace Maxdop.Core.Tests;

public class ControlFlowFormattingTests
{
    private static string Format(string sql, int maxWidth = 120, FormatOptions? options = null)
    {
        options ??= FormatOptions.Default;
        options = options with { Print = options.Print with { MaxWidth = maxWidth } };

        var result = SqlFormatter.Format(sql, options);
        if (result.Status != FormatStatus.Formatted)
        {
            Assert.Fail($"{result.Status}: {string.Join(" | ", result.Diagnostics)}");
        }

        return result.Output;
    }

    // --- IF ---------------------------------------------------------------------------

    [Fact]
    public void BareThenBranchIsIndented()
    {
        Assert.Equal(
            """
            IF @a = 1
                SELECT 1;
            """,
            Format("if @a = 1 select 1;"));
    }

    [Fact]
    public void BlockThenBranchSitsAtTheIfsOwnIndent()
    {
        // The block carries its own nesting through BEGIN/END, so indenting it as well would
        // double the depth. This is the prevailing T-SQL convention.
        Assert.Equal(
            """
            IF @a = 1
            BEGIN
                SELECT 1;
            END
            """,
            Format("if @a = 1 begin select 1; end"));
    }

    [Fact]
    public void ElseBranchFormats()
    {
        Assert.Equal(
            """
            IF @a = 1
            BEGIN
                SELECT 1;
            END
            ELSE
            BEGIN
                SELECT 2;
            END
            """,
            Format("if @a = 1 begin select 1; end else begin select 2; end"));
    }

    [Fact]
    public void BareElseBranchIsIndented()
    {
        Assert.Equal(
            """
            IF @a = 1
                SELECT 1;
            ELSE
                SELECT 2;
            """,
            Format("if @a = 1 select 1; else select 2;"));
    }

    [Fact]
    public void ElseIfChainStaysFlat()
    {
        // ScriptDom nests these as IfStatements inside ElseStatements. Printing that shape as an
        // ordinary else-branch would indent each rung one level further.
        Assert.Equal(
            """
            IF @a = 1
                SELECT 1;
            ELSE IF @a = 2
                SELECT 2;
            ELSE IF @a = 3
                SELECT 3;
            ELSE
                SELECT 4;
            """,
            Format("if @a = 1 select 1; else if @a = 2 select 2; else if @a = 3 select 3; else select 4;"));
    }

    [Fact]
    public void LongElseIfChainDoesNotMarchRight()
    {
        var sql = string.Concat(Enumerable.Range(1, 12).Select(i => $"if @a = {i} select {i}; else "));
        var result = Format(sql + "select 0;");

        // Every ELSE IF starts at column zero.
        var elseLines = result.Split('\n').Where(l => l.Contains("ELSE", StringComparison.Ordinal)).ToList();
        Assert.Equal(12, elseLines.Count);
        Assert.All(elseLines, line => Assert.StartsWith("ELSE", line, StringComparison.Ordinal));
    }

    [Fact]
    public void LongPredicateAlignsUnderTheCondition()
    {
        // Aligned rather than indented: the body is indented one level, so a predicate broken to
        // the same depth would read as the statement it guards.
        Assert.Equal(
            """
            IF @alpha = 1
               AND @beta = 2
               AND @gamma = 3
            BEGIN
                SELECT 1;
            END
            """,
            Format("if @alpha = 1 and @beta = 2 and @gamma = 3 begin select 1; end", maxWidth: 25));
    }

    // --- WHILE ------------------------------------------------------------------------

    [Fact]
    public void WhileWithBlockFormats()
    {
        Assert.Equal(
            """
            WHILE @n > 0
            BEGIN
                SET @n -= 1;
            END
            """,
            Format("while @n > 0 begin SET @n -= 1; end"));
    }

    [Fact]
    public void WhileWithBareStatementIsIndented()
    {
        Assert.Equal(
            """
            WHILE @n > 0
                SET @n -= 1;
            """,
            Format("while @n > 0 SET @n -= 1;"));
    }

    [Fact]
    public void WhilePredicateAlignsUnderTheCondition()
    {
        Assert.Equal(
            """
            WHILE @alpha = 1
                  AND @beta = 2
            BEGIN
                SELECT 1;
            END
            """,
            Format("while @alpha = 1 and @beta = 2 begin select 1; end", maxWidth: 25));
    }

    // --- TRY / CATCH ------------------------------------------------------------------

    [Fact]
    public void TryCatchFormats()
    {
        Assert.Equal(
            """
            BEGIN TRY
                SELECT 1;
            END TRY
            BEGIN CATCH
                THROW;
            END CATCH
            """,
            Format("BEGIN TRY select 1; END TRY BEGIN CATCH THROW; END CATCH"));
    }

    [Fact]
    public void TryCatchWithMultipleStatementsPerBlock()
    {
        Assert.Equal(
            """
            BEGIN TRY
                SELECT 1;
                SELECT 2;
            END TRY
            BEGIN CATCH
                SELECT ERROR_MESSAGE();
                THROW;
            END CATCH
            """,
            Format("BEGIN TRY select 1; select 2; END TRY BEGIN CATCH select ERROR_MESSAGE(); THROW; END CATCH"));
    }

    // --- nesting ----------------------------------------------------------------------

    [Fact]
    public void NestedControlFlowIndentsCumulatively()
    {
        Assert.Equal(
            """
            WHILE @n > 0
            BEGIN
                IF @n = 1
                BEGIN
                    BEGIN TRY
                        SELECT 1;
                    END TRY
                    BEGIN CATCH
                        THROW;
                    END CATCH
                END
                SET @n -= 1;
            END
            """,
            Format("while @n > 0 begin if @n = 1 begin BEGIN TRY select 1; END TRY BEGIN CATCH THROW; END CATCH end SET @n -= 1; end"));
    }

    [Fact]
    public void ControlFlowUnlocksFormattingOfItsBody()
    {
        // The reason these handlers were prioritised: passthrough is subtree-scoped, so before
        // this the inner SELECT could not be formatted at all however good the SELECT handler was.
        Assert.Equal(
            """
            IF @a = 1
            BEGIN
                SELECT
                    alpha,
                    beta
                FROM dbo.t
                WHERE alpha = 1
            END
            """,
            Format("if @a = 1 begin select alpha, beta from dbo.t where alpha = 1 end", maxWidth: 20));
    }

    // --- comments ---------------------------------------------------------------------

    [Fact]
    public void CommentOnTheConditionSurvives()
    {
        Assert.Equal(
            """
            IF @Debug = 1 -- only when debugging
            BEGIN
                PRINT 'x';
            END
            """,
            Format("if @Debug = 1 -- only when debugging\nbegin PRINT 'x'; end"));
    }

    [Fact]
    public void CommentInsideABranchSurvives()
    {
        Assert.Equal(
            """
            IF @a = 1
            BEGIN
                /* nothing to do */
                PRINT 'quiet';
            END
            """,
            Format("if @a = 1 begin\n/* nothing to do */\nPRINT 'quiet'; end"));
    }

    [Fact]
    public void CommentInsideCatchSurvives()
    {
        Assert.Equal(
            """
            BEGIN TRY
                SELECT 1;
            END TRY
            BEGIN CATCH
                THROW; -- rethrow
            END CATCH
            """,
            Format("BEGIN TRY select 1; END TRY BEGIN CATCH THROW; -- rethrow\nEND CATCH"));
    }

    // --- options and stability --------------------------------------------------------

    [Fact]
    public void KeywordCaseLowerAppliesToControlFlow()
    {
        var result = Format(
            "IF @a = 1 BEGIN SELECT 1; END ELSE BEGIN SELECT 2; END",
            options: FormatOptions.Default with { KeywordCase = KeywordCase.Lower });

        Assert.StartsWith("if @a = 1", result, StringComparison.Ordinal);
        Assert.Contains("\nelse\n", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("if @a = 1 select 1;")]
    [InlineData("if @a = 1 begin select 1; end else begin select 2; end")]
    [InlineData("if @a = 1 select 1; else if @a = 2 select 2; else select 3;")]
    [InlineData("while @n > 0 begin set @n -= 1; end")]
    [InlineData("BEGIN TRY select 1; END TRY BEGIN CATCH throw; END CATCH")]
    [InlineData("if @a = 1 and @b = 2 and @c = 3 begin select x from t where y = 1; end")]
    [InlineData("if @Debug = 1 -- note\nbegin print 'x'; end")]
    [InlineData("while @n > 0 begin if @n = 1 begin BEGIN TRY select 1; END TRY BEGIN CATCH throw; END CATCH end end")]
    public void ControlFlowIsIdempotent(string sql)
    {
        var once = Format(sql, maxWidth: 30);
        var twice = Format(once, maxWidth: 30);
        Assert.Equal(once, twice);
        Assert.Equal(twice, Format(twice, maxWidth: 30));
    }

    [Fact]
    public void ACommentAfterElseStaysWithTheElseBranch()
    {
        // It described the else case; attaching it to the preceding node printed it after the then
        // branch's END, where it read as a remark about the end of the *then* branch. 56 occurrences
        // in one corpus file, and every safety gate passed: a comment that moves loses nothing.
        //
        // The rule it drove: a comment with code to its left belongs to that code only when the
        // code is punctuation. A *word* there is a keyword, and a keyword introduces what follows.
        Assert.Equal(
            """
            IF @x = 1
            BEGIN
                SELECT 1;
            END
            ELSE /* No instance installed */
            BEGIN
                SELECT 2;
            END
            """,
            Format(
                "if @x = 1\nbegin\nselect 1;\nend\nelse /* No instance installed */\nbegin\nselect 2;\nend"));
    }

    [Fact]
    public void ABranchWithoutACommentStillStartsOnItsOwnLine()
    {
        Assert.Equal(
            """
            IF @x = 1
                SELECT 1;
            ELSE
                SELECT 2;
            """,
            Format("if @x = 1 select 1; else select 2;"));
    }

    [Fact]
    public void EndConversationKeepsItsOwnKeywordsInsideABlock()
    {
        // `END CONVERSATION @handle` begins its range at the *handle*, so every construct that
        // slices up to its first statement absorbed the keywords: `BEGIN CATCH END CONVERSATION`
        // came out on one line with the handle stranded below it. Three slicers had to learn to use
        // EffectiveFirstToken — the TRY/CATCH block, the BEGIN/END block, and the gap between two
        // statements — and the statement needed a handler of its own, because a node whose range
        // excludes its keywords cannot be passed through either.
        //
        // The `-- Catch body` line above BEGIN CATCH is incidental to that, and this test used to
        // assert it printed *inside* the block. It labels the block, so it now stays above it.
        Assert.Equal(
            """
            BEGIN TRY
                END CONVERSATION 10
            END TRY
            -- Catch body
            BEGIN CATCH
                END CONVERSATION 10
            END CATCH
            """,
            Format("begin try\nend conversation 10\nend try\n-- Catch body\nbegin catch\nend conversation 10\nend catch"));
    }
}
