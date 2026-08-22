using Maxdop.Core.Formatting;
using Maxdop.Core.Printing;

namespace Maxdop.Core.Tests;

/// <summary>
/// Regression tests for the separator class of defect, found by the first corpus run.
/// </summary>
/// <remarks>
/// Every list-shaped handler assumed its children were comma-separated. Validating the text
/// before the first child and after the last — which the handlers already did — says nothing
/// about the gaps in between. <c>TRIM('[]' FROM x)</c> passed both outer checks and was re-joined
/// with a comma, producing <c>TRIM('[]', x)</c>: different SQL, silently.
/// <para>All reproductions here are hand-written from the shape of the bug rather than copied out
/// of the corpus, so the repo stays clean-room.</para>
/// </remarks>
public class SeparatorAuditTests
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

    // --- the bug that started the audit --------------------------------------------

    [Fact]
    public void TrimWithFromSeparatorIsNotRejoinedWithAComma()
    {
        // The defect: this came out as TRIM('[]', x), which means something different.
        const string sql = "SELECT TRIM('[]' FROM a) FROM t;";
        var result = Format(sql);

        Assert.DoesNotContain("',", result, StringComparison.Ordinal);
        Assert.Contains("TRIM('[]' FROM a)", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT TRIM('[]' FROM a) FROM t;")]
    [InlineData("SELECT TRIM(LEADING '0' FROM a) FROM t;")]
    [InlineData("SELECT TRIM(TRAILING '0' FROM a) FROM t;")]
    [InlineData("SELECT TRIM(BOTH '0' FROM a) FROM t;")]
    public void KeywordSeparatedFunctionArgumentsPassThroughIntact(string sql)
    {
        // Passthrough is the correct answer here: the construct is not modelled, and emitting it
        // as written is a cosmetic loss where rewriting it would be a correctness bug.
        Assert.Equal(sql, Format(sql));
    }

    [Fact]
    public void OrdinaryCommaSeparatedCallsStillFormat()
    {
        // The guard must not make the common case bail.
        Assert.Equal("SELECT SUBSTRING(a, 1, 2) FROM t;", Format("select SUBSTRING(a,1,2) from t;"));
        Assert.Equal("SELECT ISNULL(a, 0) FROM t;", Format("select ISNULL(a,0) from t;"));
        Assert.Equal("SELECT DATEADD(DAY, 1, a) FROM t;", Format("select DATEADD(DAY,1,a) from t;"));
    }

    // --- the same hole in every other list handler ---------------------------------

    [Fact]
    public void GroupByWithRollupKeepsItsSuffix()
    {
        // WITH ROLLUP sits *after* the grouping specifications; the handler only read the keywords
        // before them, so the suffix was dropped and the query silently changed meaning.
        Assert.Equal(
            "SELECT a, SUM(b) FROM t GROUP BY a WITH ROLLUP",
            Format("select a, SUM(b) from t GROUP BY a WITH ROLLUP"));
    }

    [Fact]
    public void GroupByWithCubeKeepsItsSuffix()
    {
        Assert.Equal(
            "SELECT a, SUM(b) FROM t GROUP BY a, c WITH CUBE",
            Format("select a, SUM(b) from t GROUP BY a, c WITH CUBE"));
    }

    [Fact]
    public void NonReservedWordsKeepTheAuthorsCasingWhileKeywordsDoNot()
    {
        // ROLLUP, CUBE and NOLOCK are non-reserved words: ScriptDom lexes them as Identifier
        // tokens, and the round-trip verifier compares identifiers case-sensitively because
        // under a case-sensitive collation [Foo] and [foo] are different objects. Recasing one
        // would make the formatter reject its own output.
        //
        // So casing is applied per token: reserved words follow the option, non-reserved words
        // are left exactly as written. Slightly inconsistent to read, never wrong.
        Assert.Equal(
            "SELECT a, SUM(b) FROM t GROUP BY a WITH rollup",
            Format("select a, SUM(b) from t group by a with rollup"));

        Assert.Equal(
            "SELECT a FROM dbo.t AS p WITH (nolock)",
            Format("select a from dbo.t as p with (nolock)"));
    }

    [Fact]
    public void PlainGroupByGainsNoSuffix()
    {
        Assert.Equal("SELECT a, SUM(b) FROM t GROUP BY a", Format("select a, SUM(b) from t group by a"));
    }

    [Fact]
    public void GroupingSetsPassThrough()
    {
        const string sql = "SELECT a, b FROM t GROUP BY GROUPING SETS ((a), (b), ())";
        Assert.Equal(sql, Format(sql));
    }

    // --- the guard must be conservative, never creative ----------------------------

    [Theory]
    // Ordinary comma-separated lists in every handler that was audited.
    [InlineData("SELECT a, b, c FROM t;")]
    [InlineData("SELECT a FROM t, u WHERE t.i = u.i;")]
    [InlineData("SELECT a FROM t ORDER BY a, b DESC;")]
    [InlineData("SELECT a FROM t WHERE a IN (1, 2, 3);")]
    [InlineData("WITH c (x, y) AS (SELECT a, b FROM t) SELECT x FROM c;")]
    [InlineData("CREATE PROCEDURE dbo.p @a INT, @b INT AS BEGIN SELECT 1; END")]
    public void CommaSeparatedListsAreUnaffectedByTheGuard(string sql)
    {
        // Formatting must still happen — a guard that bailed on everything would "fix" the bug
        // by doing nothing, and the suite would not notice.
        var result = Format(sql);
        Assert.Contains("SELECT", result, StringComparison.Ordinal);
    }

    [Fact]
    public void GuardDoesNotDisableFormattingWholesale()
    {
        // Proof the guards did not turn the formatter into a no-op: this input is reformatted.
        var result = Format("select    a,b   from   t   where   a=1", maxWidth: 120);
        Assert.Equal("SELECT a, b FROM t WHERE a = 1", result);
    }

    // --- round-trip is the backstop -------------------------------------------------

    [Theory]
    [InlineData("SELECT TRIM('[]' FROM a) FROM t;")]
    [InlineData("SELECT a, SUM(b) FROM t GROUP BY a WITH ROLLUP")]
    [InlineData("SELECT a, SUM(b) FROM t GROUP BY a, b WITH CUBE")]
    [InlineData("SELECT COUNT(DISTINCT a) FROM t;")]
    [InlineData("SELECT STRING_AGG(a, ',') FROM t;")]
    public void SeparatorSensitiveConstructsSurviveVerification(string sql)
    {
        // Whatever the handler decides, the output must mean the same thing. These are exactly the
        // inputs where an assumed separator would change meaning.
        var result = SqlFormatter.Format(sql);
        Assert.Equal(FormatStatus.Formatted, result.Status);

        var second = SqlFormatter.Format(result.Output);
        Assert.Equal(FormatStatus.Formatted, second.Status);
        Assert.Equal(result.Output, second.Output);
    }
}
