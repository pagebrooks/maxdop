using Maxdop.Core.Formatting;

namespace Maxdop.Core.Tests;

// `-- maxdop-ignore`: leave the next statement exactly as written, format everything else.
//
// The escape hatch every mature formatter has. It matters more in T-SQL than most languages, because
// people hand-align things that carry meaning to a reader (VALUES grids, CASE ladders, select lists laid
// out to mirror a target table) and before this the only way to protect one statement was to exclude
// the whole file.
//
// Scoped to the next statement on purpose. A start/end block form does not line up with node
// boundaries, which makes it a different and much harder problem; Prettier made the same call.
public class IgnoreDirectiveTests
{
    private static string Format(string sql, FormatOptions? options = null)
    {
        var result = SqlFormatter.Format(sql, options);
        if (result.Status != FormatStatus.Formatted)
        {
            Assert.Fail($"{result.Status}: {string.Join(" | ", result.Diagnostics)}");
        }

        // Every directive test is also an idempotency test: a statement the formatter leaves alone has
        // to stay alone on the second pass, and so does the formatted code around it.
        var again = SqlFormatter.Format(result.Output, options).Output;
        Assert.Equal(result.Output, again);
        return result.Output;
    }

    [Fact]
    public void TheNextStatementIsLeftExactlyAsWrittenAndTheRestIsFormatted()
    {
        const string sql = """
            select a,b from t;

            -- maxdop-ignore
            insert into dbo.Rates (Code, Rate, Description)
            values ('A',  0.10, 'standard'),
                   ('BB', 1.25, 'premium'),
                   ('C',  9.00, 'legacy');

            select c,d from u;
            """;

        Assert.Equal("""
            SELECT a, b FROM t;

            -- maxdop-ignore
            insert into dbo.Rates (Code, Rate, Description)
            values ('A',  0.10, 'standard'),
                   ('BB', 1.25, 'premium'),
                   ('C',  9.00, 'legacy');

            SELECT c, d FROM u;
            """, Format(sql));
    }

    [Fact]
    public void AHandAlignedCaseLadderSurvives()
    {
        const string sql = """
            -- maxdop-ignore
            SELECT CASE WHEN score >= 90 THEN 'A'
                        WHEN score >= 80 THEN 'B'
                        WHEN score >= 70 THEN 'C'
                        ELSE                  'F' END AS grade
            FROM   dbo.Results;
            """;

        Assert.Equal(sql, Format(sql));
    }

    [Fact]
    public void InsideABlockTheStatementMovesAsAUnitAndKeepsItsInternalLayout()
    {
        // Written with two-space indents; maxdop uses four. The ignored statement's first line moves to
        // the block's new indent, and every later line moves with it, so the grid stays aligned against
        // its own first line. Leaving the later lines where they were would shear the grid apart.
        const string sql = """
            CREATE PROCEDURE dbo.p AS
            BEGIN
              set nocount on;
              -- maxdop-ignore
              insert into t (a, b)
                  values (1,  2),
                         (10, 20);
              select a,b from t;
            END
            """;

        Assert.Equal("""
            CREATE PROCEDURE dbo.p
            AS
            BEGIN
                SET NOCOUNT ON;
                -- maxdop-ignore
                insert into t (a, b)
                    values (1,  2),
                           (10, 20);
                SELECT a, b FROM t;
            END
            """, Format(sql));
    }

    [Fact]
    public void BeforeAModuleItIgnoresTheWholeModule()
    {
        // The first statement of a batch, where the comment can belong to the batch rather than the
        // statement. The directive has to work there too: it is where a whole procedure gets protected.
        const string sql = """
            select 1;
            GO
            -- maxdop-ignore
            create procedure dbo.p as
            begin
                select   a ,  b   from t;
            end
            GO
            select 2;
            """;

        Assert.Equal("""
            SELECT 1;
            GO
            -- maxdop-ignore
            create procedure dbo.p as
            begin
                select   a ,  b   from t;
            end
            GO
            SELECT 2;
            """, Format(sql));
    }

    [Theory]
    [InlineData("-- maxdop-ignore")]
    [InlineData("--maxdop-ignore")]
    [InlineData("-- maxdop-ignore: hand-aligned to match the rate card")]
    [InlineData("-- maxdop-ignore keeps the grid")]
    [InlineData("/* maxdop-ignore */")]
    [InlineData("/*maxdop-ignore*/")]
    [InlineData("/* maxdop-ignore: generated */")]
    [InlineData("-- MAXDOP-IGNORE")]
    public void TheDirectiveCanBeSpelledAsAnyOfThese(string directive)
    {
        var sql = $"{directive}\nselect   a ,  b   from t;\n";

        Assert.Equal(sql, Format(sql));
    }

    [Theory]
    [InlineData("-- maxdop-ignored")]
    [InlineData("-- maxdop-ignore-next")]
    [InlineData("-- do not maxdop-ignore this")]
    [InlineData("-- maxdop")]
    public void ACommentThatOnlyResemblesTheDirectiveIsAnOrdinaryComment(string comment)
    {
        Assert.Equal($"{comment}\nSELECT a, b FROM t;\n", Format($"{comment}\nselect   a ,  b   from t;\n"));
    }

    [Fact]
    public void OnlyTheNextStatementIsIgnored()
    {
        const string sql = """
            -- maxdop-ignore
            select   1;
            select   2;
            """;

        Assert.Equal("""
            -- maxdop-ignore
            select   1;
            SELECT 2;
            """, Format(sql));
    }

    [Fact]
    public void OtherCommentsMayStandBetweenTheDirectiveAndTheStatement()
    {
        const string sql = """
            -- maxdop-ignore
            -- Columns mirror the vendor's spec, section 4.2.
            select   a ,  b   from t;
            """;

        Assert.Equal(sql, Format(sql));
    }

    [Fact]
    public void AnEndOfLineCommentAnnotatesTheCodeBeforeItNotTheStatementAfter()
    {
        // `-- maxdop-ignore` at the end of a line is a note about that line. Applying it to whatever
        // statement happens to come next would make the directive's reach depend on line breaks.
        const string sql = """
            select   1; -- maxdop-ignore
            select   2;
            """;

        Assert.Equal("""
            SELECT 1; -- maxdop-ignore
            SELECT 2;
            """, Format(sql));
    }

    [Fact]
    public void AStatementInsideAnIfCanBeIgnored()
    {
        const string sql = """
            if @x = 1
                -- maxdop-ignore
                select   a ,  b   from t;
            """;

        Assert.Equal("""
            IF @x = 1
                -- maxdop-ignore
                select   a ,  b   from t;
            """, Format(sql));
    }

    [Fact]
    public void KeywordCasingIsNotAppliedToTheIgnoredStatement()
    {
        var options = FormatOptions.Default with { KeywordCase = KeywordCase.Lower };
        const string sql = """
            -- maxdop-ignore
            SELECT   A ,  B   FROM T;
            SELECT A, B FROM T;
            """;

        Assert.Equal("""
            -- maxdop-ignore
            SELECT   A ,  B   FROM T;
            select A, B from T;
            """, Format(sql, options));
    }

    [Fact]
    public void AFileThatIsAllIgnoredComesBackUnchanged()
    {
        const string sql = """
            -- maxdop-ignore
            select   a ,  b   from t;
            """;

        var result = SqlFormatter.Format(sql);
        Assert.Equal(FormatStatus.Formatted, result.Status);
        Assert.False(result.Changed);
    }

    [Fact]
    public void TheDirectiveWorksInAFileFormattedBatchByBatch()
    {
        // A file that does not parse as a whole is formatted one GO-separated batch at a time. The
        // directive has to hold on that path too, since migration scripts are where it is most likely.
        const string sql = """
            -- maxdop-ignore
            select   a ,  b   from t;
            GO
            select ((( from nowhere
            GO
            select   c ,  d   from u;
            """;

        var output = SqlFormatter.Format(sql).Output;
        Assert.Contains("-- maxdop-ignore\nselect   a ,  b   from t;", output, StringComparison.Ordinal);
        Assert.Contains("SELECT c, d FROM u;", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ALineLeftOfTheStatementsOwnIndentFallsBackToTheSourceColumns()
    {
        // The closing parenthesis sits left of the line the statement starts on, so the statement cannot
        // be shifted as a block. It falls back to Verbatim: the first line takes the block's indent and
        // the rest stay at their source columns, which still leaves every token where the author put it.
        const string sql = """
            BEGIN
                -- maxdop-ignore
                insert into t (a, b) values (
                    1,  2
            )
            END
            """;

        var output = Format(sql);
        Assert.Contains("    insert into t (a, b) values (\n        1,  2\n)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CommentsAndMultiLineStringsInsideTheStatementAreUntouched()
    {
        const string sql = """
            -- maxdop-ignore
            select   a ,  /* keep */  b,
                     'line one
            line two' as s   -- trailing note
            from t;
            """;

        Assert.Equal(sql, Format(sql));
    }
}
