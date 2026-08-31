using Maxdop.Core.Formatting;
using Maxdop.Core.Printing;

namespace Maxdop.Core.Tests;

/// <summary>
/// The function-shaped expressions ScriptDom gives their own node types, and the call targets in
/// front of a method call.
/// </summary>
public class CallFormattingTests
{
    private static string Format(string sql, int maxWidth = 120)
    {
        var result = SqlFormatter.Format(
            sql,
            FormatOptions.Default with { Print = PrintOptions.Default with { MaxWidth = maxWidth } });

        if (result.Status != FormatStatus.Formatted)
        {
            Assert.Fail($"{result.Status}: {string.Join(" | ", result.Diagnostics)}");
        }

        return result.Output;
    }

    // --- keyword calls ----------------------------------------------------------------

    [Theory]
    [InlineData("select nullif(a,0) from t;", "SELECT NULLIF(a, 0) FROM t;")]
    [InlineData("select coalesce(a,b,c) from t;", "SELECT COALESCE(a, b, c) FROM t;")]
    [InlineData("select left(s,3) from t;", "SELECT LEFT(s, 3) FROM t;")]
    [InlineData("select right(s,3) from t;", "SELECT RIGHT(s, 3) FROM t;")]
    public void KeywordCallsNormaliseTheirArgumentSpacing(string input, string expected)
    {
        // Each of these is a distinct ScriptDom type rather than a FunctionCall, so none of them got
        // any benefit from the FunctionCall handler.
        Assert.Equal(expected, Format(input));
    }

    [Fact]
    public void IifIsRecasedDespiteLexingAsAnIdentifier()
    {
        // IIF lexes as an identifier while NULLIF, COALESCE, LEFT and RIGHT have token types of their
        // own — the family is split down the middle. Both are recased anyway: the region holds the
        // call's own keyword and its parenthesis, so it is a keyword position and the printer claims
        // it for the verifier.
        Assert.Equal("SELECT IIF(a > 1, 'y', 'n') FROM t;", Format("select IIF(a>1,'y','n') from t;"));
        Assert.Equal("SELECT IIF(a > 1, 'y', 'n') FROM t;", Format("select iif(a>1,'y','n') from t;"));
    }

    [Fact]
    public void NestedKeywordCallsAreFormattedAllTheWayDown()
    {
        Assert.Equal(
            "SELECT COALESCE(NULLIF(LEFT(s, 3), ''), 'none') FROM t;",
            Format("select coalesce(nullif(left(s,3),''),'none') from t;"));
    }

    [Fact]
    public void LongArgumentListBreaksOnePerLine()
    {
        Assert.Equal(
            """
            SELECT
                COALESCE(
                    @FirstReasonablyLongName,
                    @SecondReasonablyLongName,
                    @ThirdReasonablyLongName
                ) AS x
            FROM t;
            """,
            Format(
                "select coalesce(@FirstReasonablyLongName, @SecondReasonablyLongName,"
                + " @ThirdReasonablyLongName) as x from t;",
                maxWidth: 50));
    }

    // --- WITHIN GROUP -------------------------------------------------------------------

    [Theory]
    // The bug this handler was written for. `WITHIN GROUP` used to reach the output through the slice
    // taken after the closing parenthesis, which recases no identifier — so reserved `GROUP` came up
    // and non-reserved `WITHIN` did not, giving `within GROUP (ORDER BY b)`.
    [InlineData(
        "select string_agg(a, ',') within group (order by b) from t;",
        "SELECT STRING_AGG(a, ',') WITHIN GROUP (ORDER BY b) FROM t;")]
    [InlineData(
        "select string_agg(a, ',') WITHIN GROUP (ORDER BY b) from t;",
        "SELECT STRING_AGG(a, ',') WITHIN GROUP (ORDER BY b) FROM t;")]
    // Argument spacing inside the ordering is normalised like any other ORDER BY, and the direction
    // keyword comes up with it.
    [InlineData(
        "select string_agg(a, ',') within group (order by b desc,c) from t;",
        "SELECT STRING_AGG(a, ',') WITHIN GROUP (ORDER BY b DESC, c) FROM t;")]
    public void WithinGroupIsRecasedAsOneClause(string input, string expected)
    {
        Assert.Equal(expected, Format(input));
    }

    [Fact]
    public void WithinGroupAndOverAppearInWrittenOrder()
    {
        // Both trailing clauses at once, which is why they are collected and sorted by token index
        // rather than emitted in a fixed order.
        Assert.Equal(
            "SELECT PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY x DESC) OVER (PARTITION BY g) FROM t;",
            Format("select percentile_cont(0.5) within group (order by x desc) over (partition by g) from t;"));
    }

    [Fact]
    public void CollationAfterACallStillKeepsItsName()
    {
        // The reason the fix had to be structural. A COLLATE shares the region WITHIN GROUP used to be
        // emitted from, and a collation name is a name — so recasing that whole region to fix the
        // casing of `WITHIN` would have renamed the collation. `WithinGroupClause` is a node and the
        // collation is not, which is exactly the distinction the printer acts on.
        Assert.Equal(
            "SELECT RTRIM(z) COLLATE Latin1_General_BIN FROM t;",
            Format("select rtrim(z) collate Latin1_General_BIN from t;"));
    }

    [Fact]
    public void LongWithinGroupBreaksAtItsOwnParenthesis()
    {
        // Same layout as OVER: the clause is a group of its own, so pressure breaks the construct
        // rather than falling through to the ORDER BY list inside it.
        Assert.Equal(
            """
            SELECT
                STRING_AGG(SomeVeryLongColumnName, ', ') WITHIN GROUP (
                    ORDER BY AnotherLongColumnName DESC, ThirdColumnName
                ) AS Grouped
            FROM t;
            """,
            Format(
                "select string_agg(SomeVeryLongColumnName, ', ') within group "
                + "(order by AnotherLongColumnName desc, ThirdColumnName) as Grouped from t;",
                maxWidth: 80));
    }

    [Fact]
    public void GraphPathWithinGroupIsDeclinedRatherThanModelled()
    {
        // `WITHIN GROUP (GRAPH PATH)` is a different construct wearing the same two words, and it
        // carries no ORDER BY. Left verbatim rather than guessed at — the file still formats.
        var formatted = Format(
            "select last_value(n2.name) within group (graph path) as p "
            + "from Person as n1, friend for path as e, Person for path as n2 "
            + "where match(shortest_path(n1(-(e)->n2)+));");

        // Verbatim, casing included: the clause was declined, so nothing in it was claimed.
        Assert.Contains("within group (graph path)", formatted, StringComparison.Ordinal);
    }

    // --- call targets -----------------------------------------------------------------

    [Fact]
    public void MethodCallOnAColumnKeepsItsTarget()
    {
        // The dot lives in the gap the FunctionCall handler reads, so the target node is a
        // transparent wrapper — but without a handler it froze everything to the left of the dot.
        Assert.Equal(
            "SELECT c.n.value('@PhysicalOp', 'VARCHAR(100)') FROM t;",
            Format("select c.n.value('@PhysicalOp','VARCHAR(100)') from t;"));
    }

    [Fact]
    public void ExpressionCallTargetIsFormattedRatherThanFrozen()
    {
        // The single largest remaining gap in real-world scripts before this handler existed: XML
        // shredding puts a whole CONVERT — or a subquery — in the target position, and passthrough is
        // subtree-scoped, so all of it came out untouched. `CONVERT(XML,d)` becoming
        // `CONVERT(XML, d)` is the proof that the printer now descends into it.
        Assert.Equal(
            "SELECT CONVERT(XML, d).value('(/a)[1]', 'int') FROM t;",
            Format("select CONVERT(XML,d).value('(/a)[1]','int') from t;"));
    }

    [Fact]
    public void TypeMethodCallStillKeepsItsDoubleColon()
    {
        // The call-target separator is `::` for a type and `.` for a schema. Forwarding the target
        // through a handler must not disturb that.
        Assert.Equal("SELECT t2::f() FROM t;", Format("SELECT t2::f() FROM t;"));
    }

    [Fact]
    public void CommentInsideAKeywordCallSurvives()
    {
        var sql = "SELECT COALESCE(a /* first choice */, b) FROM t;";
        var result = Format(sql);

        Assert.Contains("/* first choice */", result, StringComparison.Ordinal);
        Assert.Equal(result, Format(result));
    }
}
