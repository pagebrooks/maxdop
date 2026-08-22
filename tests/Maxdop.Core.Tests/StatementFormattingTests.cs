using Maxdop.Core.Formatting;
using Maxdop.Core.Printing;

namespace Maxdop.Core.Tests;

/// <summary>
/// <c>RAISERROR</c> and <c>PRINT</c> — the keyword-and-argument-list statements.
/// </summary>
public class StatementFormattingTests
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

    // --- RAISERROR --------------------------------------------------------------------

    [Fact]
    public void ShortRaiseErrorStaysOnOneLine()
    {
        Assert.Equal(
            "RAISERROR(N'check_id 60: XML indexes', 0, 1);",
            Format("raiserror(N'check_id 60: XML indexes', 0, 1);"));
    }

    [Fact]
    public void SpaceBeforeTheParenthesisIsAccepted()
    {
        // Both spellings are idiomatic, so the head check compares without whitespace rather than
        // insisting on one of them.
        Assert.Equal("RAISERROR('simple', 10, 1);", Format("raiserror ('simple', 10, 1);"));
    }

    [Fact]
    public void WithOptionsSurviveInTheAuthorsCase()
    {
        // LOG, NOWAIT and SETERROR are non-reserved words that lex as identifiers, so recasing them
        // is a token change the verifier rejects — the same rule as NOLOCK and APPLY.
        Assert.Equal(
            "RAISERROR('simple', 10, 1) WITH LOG, SETERROR, NOWAIT;",
            Format("raiserror('simple', 10, 1) with LOG, SETERROR, NOWAIT;"));
    }

    [Fact]
    public void FormatArgumentsArePreservedInOrder()
    {
        // The message, severity and state are three separate properties and the remaining format
        // arguments are a list; reassembling them in the wrong order would silently change which
        // value fills which placeholder.
        Assert.Equal(
            "RAISERROR('Value is %d and %s', 16, 1, @n, @s);",
            Format("raiserror('Value is %d and %s', 16, 1, @n, @s);"));
    }

    [Fact]
    public void LongRaiseErrorBreaksOneArgumentPerLine()
    {
        Assert.Equal(
            """
            RAISERROR(
                N'A considerably longer diagnostic message with %s and %d in it',
                16,
                1,
                @objectName,
                @rowCount
            ) WITH NOWAIT;
            """,
            Format(
                "raiserror(N'A considerably longer diagnostic message with %s and %d in it', 16, 1,"
                + " @objectName, @rowCount) with NOWAIT;",
                maxWidth: 80));
    }

    [Fact]
    public void MessageIdFormIsPreserved()
    {
        Assert.Equal("RAISERROR(50001, 16, 1);", Format("raiserror(50001, 16, 1);"));
    }

    // The legacy `RAISERROR 50001 'msg'` form has its own ScriptDom node type
    // (RaiseErrorLegacyStatement) and so would fall through to verbatim — but there is no test for
    // it, because the default parser version rejects the syntax outright. Same as `GO 5`: a case
    // worth knowing about and not worth asserting.

    [Fact]
    public void CommentBeforeTheWithOptionsIsNotDropped()
    {
        var sql = "RAISERROR('x', 16, 1)\n/* fire immediately */\nWITH NOWAIT;";
        var result = Format(sql);

        Assert.Contains("/* fire immediately */", result, StringComparison.Ordinal);
        Assert.Equal(result, Format(result));
    }

    // --- PRINT ------------------------------------------------------------------------

    [Fact]
    public void PrintFormats()
    {
        Assert.Equal("PRINT 'starting';", Format("print 'starting';"));
    }

    [Fact]
    public void PrintOfAConcatenationKeepsItsExpression()
    {
        Assert.Equal(
            "PRINT N'rows: ' + @n;",
            Format("print N'rows: '+@n;"));
    }
}
