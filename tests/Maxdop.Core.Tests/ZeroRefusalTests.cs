using Maxdop.Core.Formatting;
using Maxdop.Core.Printing;

namespace Maxdop.Core.Tests;

/// <summary>
/// Reproductions of the last constructs that made the formatter refuse its own output. Together with
/// the rest of the corpus regression suite, these are what keep the corpus at zero refusals.
/// </summary>
/// <remarks>
/// All hand-written from the shape of each defect, never copied from a corpus file, so the repo stays clean-room.
/// <para>The recurring theme, and the reason most of these are one-line guards: a handler that emits
/// a known set of children must prove it accounted for <em>everything</em> in the node's range.
/// ScriptDom ranges routinely extend past the children a handler knows about — a collation after
/// <c>END</c>, a closing parenthesis after a temporal clause — and the difference between a formatter
/// you can trust and one you cannot is whether it notices.</para>
/// </remarks>
public class ZeroRefusalTests
{
    private static FormatResult Run(string sql, int maxWidth = 120) =>
        SqlFormatter.Format(sql, FormatOptions.Default with { Print = PrintOptions.Default with { MaxWidth = maxWidth } });

    private static string Format(string sql, int maxWidth = 120)
    {
        var result = Run(sql, maxWidth);
        if (result.Status != FormatStatus.Formatted)
        {
            Assert.Fail($"{result.Status}: {string.Join(" | ", result.Diagnostics)}");
        }

        return result.Output;
    }

    /// <summary>Formats, and asserts the result is a fixed point after the first pass.</summary>
    private static void AssertStable(string sql, int maxWidth = 120)
    {
        var once = Format(sql, maxWidth);
        var twice = Format(once, maxWidth);
        Assert.Equal(once, twice);
        Assert.Equal(twice, Format(twice, maxWidth));
    }

    // --- trailing clauses inside a node's range --------------------------------------

    [Theory]
    // A collation sits inside the range of the expression it applies to, past every child the
    // handler models. Emitting the children and a closing keyword dropped it.
    [InlineData("SELECT CASE WHEN @a = 1 THEN 'x' ELSE 'y' END COLLATE SQL_Latin1_General_CP1_CI_AS;")]
    [InlineData("SELECT (SELECT TOP 1 a FROM t) COLLATE SQL_Latin1_General_CP1_CI_AS;")]
    [InlineData("SELECT (@a) COLLATE SQL_Latin1_General_CP1_CI_AS;")]
    public void CollationAfterAnExpressionSurvives(string sql)
    {
        Assert.Equal(sql, Format(sql));
        AssertStable(sql);
    }

    [Theory]
    // `FOR SYSTEM_TIME CONTAINED IN ('a','b')` puts its closing parenthesis outside both the query
    // expression's range *and* the table reference's, so only a statement-level check sees it.
    [InlineData("SELECT * FROM T FOR SYSTEM_TIME CONTAINED IN ('01/02/03', '04/05/06');")]
    [InlineData("SELECT * FROM T FOR SYSTEM_TIME BETWEEN @a AND @b;")]
    [InlineData("SELECT * FROM T FOR SYSTEM_TIME AS OF @a;")]
    public void TemporalClausesSurvive(string sql)
    {
        Assert.Equal(sql, Format(sql));
        AssertStable(sql);
    }

    // --- constructs whose operands hide their own keywords ---------------------------

    [Theory]
    [InlineData("SELECT * FROM NODE AS N, EDGE AS E, NODE AS N2 WHERE MATCH(N-(E)->N2);")]
    [InlineData("SELECT * FROM NODE AS N, EDGE AS E, NODE AS N2 WHERE (MATCH(N-(E)->N2));")]
    public void GraphMatchPredicatesSurvive(string sql)
    {
        // Operand ranges begin after `MATCH(`, so both the enclosing boolean chain and any wrapping
        // parentheses had to learn to notice.
        Assert.Equal(sql, Format(sql));
        AssertStable(sql);
    }

    [Fact]
    public void TypeMethodCallKeepsItsDoubleColon()
    {
        // The call-target separator is `.` for a schema but `::` for a type. Hard-coding `.` turned
        // `t2::f()` into `t2::.f()`.
        const string sql = "SELECT t2::f() FROM t;";
        Assert.Equal(sql, Format(sql));
    }

    [Fact]
    public void IsDistinctFromIsNotReducedToIsNull()
    {
        // SQL 2022's `IS [NOT] DISTINCT FROM` also parses as a null test; writing out a bare
        // `IS NULL` silently replaced it with a different predicate.
        const string sql = "SELECT a FROM t WHERE t.id IS NOT DISTINCT FROM NULL;";
        Assert.Equal(sql, Format(sql));
    }

    // --- tokens no node owns ---------------------------------------------------------

    [Theory]
    [InlineData("commit Work")]
    [InlineData("rollback Work")]
    public void TransactionWorkKeywordSurvives(string sql)
    {
        // `commit Work` parses as a statement covering just `commit`, leaving `Work` owned by
        // nothing. Statement sequencing now emits the gap.
        Assert.Contains("Work", Format(sql), StringComparison.Ordinal);
        AssertStable(sql);
    }

    [Fact]
    public void ConsecutiveEmptyBatchesEachKeepTheirOwnLine()
    {
        // Runs of `GO` with nothing between them produce no batches at all, so the tokens are
        // unowned — and folding them onto one line is invalid, because GO must stand alone.
        Assert.Equal(
            """
            GO
            GO
            GO
            SELECT 1;
            """,
            Format("go\ngo\ngo\nSELECT 1;"));
    }

    [Fact]
    public void CommentBetweenDoubledTerminatorsLandsBetweenThem()
    {
        // The `;WITH` idiom folds a redundant semicolon into the previous statement's range, and a
        // section header often sits between the two. This used to be passed through whole, because the
        // attacher had no way to say "between two children" and handed the comment to the statement's
        // last clause — which emitted it *before* the semicolon it introduced.
        //
        // A comment past a construct's terminator now attaches to the construct itself, and the
        // terminator run interleaves it, so it lands exactly where the author put it.
        Assert.Equal(
            """
            UPDATE #h
            SET a = NULL
            WHERE b = 0;

            /* Section 8 */
            ;WITH cte AS (
                SELECT 1 AS x
            )
            SELECT x FROM cte;
            """,
            Format("UPDATE #h SET a = NULL WHERE b = 0;\n\n/* Section 8 */\n;WITH cte AS (SELECT 1 AS x)\nSELECT x FROM cte;"));
    }

    [Fact]
    public void LineCommentBetweenTerminatorsDoesNotSwallowTheSemicolon()
    {
        // The dangerous half. A `--` comment alone on its line is emitted as plain text, so a semicolon
        // appended to that line ends up *inside* the comment and the statement silently loses its
        // terminator — which the round-trip verifier caught as "expected Semicolon, got WITH". The break
        // after an interleaved comment is therefore unconditional.
        var result = Format(
            "CREATE PROCEDURE dbo.p\nAS\nBEGIN\n    DECLARE @n INT = 0;\n\n    -- before a CTE\n"
            + "    ;WITH c AS (SELECT 1 AS x)\n    SELECT x FROM c;\nEND");

        Assert.Contains("DECLARE @n INT = 0;", result, StringComparison.Ordinal);
        Assert.Contains("-- before a CTE\n    ;WITH", result, StringComparison.Ordinal);
        AssertStable(
            "CREATE PROCEDURE dbo.p\nAS\nBEGIN\n    DECLARE @n INT = 0;\n\n    -- before a CTE\n"
            + "    ;WITH c AS (SELECT 1 AS x)\n    SELECT x FROM c;\nEND");
    }

    // --- coverage reclaimed from over-eager guards ------------------------------------

    [Fact]
    public void StraySemicolonAfterBeginDoesNotFreezeTheBlock()
    {
        // `BEGIN;` is common in generated scripts, and bailing on it froze whole procedure bodies —
        // the single largest block of unformatted text left in the corpus, all over one semicolon.
        // The opening region is emitted rather than assumed to be a bare keyword.
        Assert.Equal(
            "BEGIN;\n    SELECT 1;\nEND",
            Format("BEGIN;\n  select 1;\nEND"));
    }

    [Fact]
    public void DoubledTerminatorDoesNotFreezeTheBlock()
    {
        // `END;;` made the "nothing but END follows" check see `END ;`, because only one trailing
        // semicolon was being excluded from the range.
        Assert.Equal(
            "BEGIN\n    SELECT 1;\nEND;;",
            Format("BEGIN\n  select 1;\nEND;;"));
    }

    [Fact]
    public void NativelyCompiledBodyStillFormatsItsStatements()
    {
        // The same region carries `BEGIN ATOMIC WITH (…)`, which now survives *and* lets the body
        // format, where before it forced the whole block through verbatim.
        var result = Format(
            "CREATE PROCEDURE dbo.p\nWITH NATIVE_COMPILATION, SCHEMABINDING\nAS\n"
            + "BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'english')\n"
            + "  select 1;\nEND");

        Assert.Contains("ATOMIC WITH", result, StringComparison.Ordinal);
        Assert.Contains("SELECT 1;", result, StringComparison.Ordinal);
    }

    // --- comments must not move code -------------------------------------------------

    [Fact]
    public void CommentWidthDoesNotDecideWhereCodeBreaks()
    {
        // The last idempotency failure in the corpus. An inline comment's width was measured, so a
        // long comment forced the code around it to break; reformatting then moved the code that
        // followed the comment, reclassifying it as end-of-line, at which point its width stopped
        // counting and the code un-broke. Comments are now excluded from measurement entirely.
        const string shortComment = "SELECT a FROM t WHERE x >= DATEADD(DAY, -14, GETDATE()) /* short */;";
        const string longComment = "SELECT a FROM t WHERE x >= DATEADD(DAY, -14, GETDATE()) /* a considerably longer explanation of exactly why fourteen days */;";

        // Same code, different comment length: the code must lay out identically.
        var withShort = Format(shortComment, maxWidth: 80);
        var withLong = Format(longComment, maxWidth: 80);

        Assert.Equal(
            withShort.Replace(" /* short */", string.Empty, StringComparison.Ordinal),
            withLong[..withLong.IndexOf("/*", StringComparison.Ordinal)].TrimEnd() + ";");
    }

    [Fact]
    public void InlineCommentFollowedByCodeIsStable()
    {
        // The exact shape from the corpus: a comment with a closing parenthesis after it, so it
        // classifies as inline on the first pass and end-of-line on the second.
        AssertStable(
            "SELECT a FROM t WHERE (x >= DATEADD(DAY, -14, GETDATE()) /* In the last 2 weeks */);",
            maxWidth: 60);
    }
}
