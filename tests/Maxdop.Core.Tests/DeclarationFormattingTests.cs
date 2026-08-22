using Maxdop.Core.Formatting;
using Maxdop.Core.Printing;

namespace Maxdop.Core.Tests;

/// <summary>
/// <c>DECLARE</c>, table variables and procedure parameters — a name, a type, and a run of optional
/// modifiers, at three different scales.
/// </summary>
public class DeclarationFormattingTests
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

    // Type names are written in upper case in these inputs on purpose. Built-in type names lex as
    // identifiers, so `int` stays `int` under KeywordCase.Upper — the round-trip verifier compares
    // identifier tokens case-sensitively and cannot tell a built-in type from a user-defined one.
    // See the note on PrintDataType; changing it means changing the verifier, not the printer.

    // --- DECLARE ----------------------------------------------------------------------

    [Fact]
    public void ShortDeclareStaysOnOneLine()
    {
        Assert.Equal("DECLARE @a INT;", Format("declare @a INT;"));
    }

    [Fact]
    public void DefaultValueKeepsItsEqualsSign()
    {
        // The `=` belongs to no node; it comes from the gap between the variable and its value.
        Assert.Equal("DECLARE @b NVARCHAR(50) = N'x';", Format("declare @b NVARCHAR(50)=N'x';"));
    }

    [Fact]
    public void SeveralDeclarationsBreakOnePerLine()
    {
        Assert.Equal(
            """
            DECLARE
                @a INT,
                @b NVARCHAR(50) = N'x',
                @c BIT = 0;
            """,
            Format("declare @a INT, @b NVARCHAR(50)=N'x', @c BIT=0;", maxWidth: 30));
    }

    [Fact]
    public void SemicolonBeforeWithStillLandsOnTheCteLine()
    {
        // ScriptDom folds the defensive semicolon in front of a `WITH` into the *previous* statement's
        // range, so a DECLARE can end with `;` newline `;`. Passthrough preserved that line break for
        // free by emitting source text; once DECLARE had a handler the terminators ran together and
        // produced `DECLARE @n INT;;WITH`.
        Assert.Equal(
            """
            DECLARE @n INT;
            ;WITH c AS (
                SELECT a FROM t
            )
            SELECT a FROM c;
            """,
            Format("DECLARE @n INT;\n;with c as (select a from t) select a from c;", maxWidth: 60));
    }

    // --- table variables ---------------------------------------------------------------

    [Fact]
    public void TableVariableGetsTheSameLayoutAsCreateTable()
    {
        // The point of TableDefinition having its own handler: a table variable and a real table are
        // not formatted differently just because ScriptDom reaches them by different routes.
        Assert.Equal(
            """
            DECLARE @t TABLE (
                a INT NOT NULL,
                b NVARCHAR(10)
            );
            """,
            Format("declare @t table (a INT not null, b NVARCHAR(10));"));
    }

    // --- procedure parameters ----------------------------------------------------------

    [Fact]
    public void ParameterModifiersWithNoNodeSurvive()
    {
        // `OUTPUT` and `READONLY` are flags on ProcedureParameter with no AST node of their own, so
        // they can only arrive through the tail token slice — the same route as PERSISTED on a column.
        // Written in upper case here because they are non-reserved words that lex as identifiers and
        // so keep the author's casing, exactly like OUTPUT on an INSERT.
        var result = Format(
            "create procedure dbo.p @a INT = null, @b NVARCHAR(50) OUTPUT, @c dbo.MyType READONLY as select 1;");

        // `null` stays lower case: it is a literal, and literals are reproduced as the author wrote
        // them rather than recased — the same rule that keeps `N'x'` and numeric formats intact.
        Assert.Contains("@a INT = null", result, StringComparison.Ordinal);
        Assert.Contains("@b NVARCHAR(50) OUTPUT", result, StringComparison.Ordinal);
        Assert.Contains("@c dbo.MyType READONLY", result, StringComparison.Ordinal);
    }

    [Fact]
    public void HandAlignedParameterColumnsAreNormalised()
    {
        // Deliberate, and the same call as for column definitions: an opinionated formatter picks one
        // layout. Safe because the handler accounts for every token in the parameter's range rather
        // than enumerating properties, several of which are flags with no node.
        Assert.Contains(
            "@AsOfDate DATETIME,",
            Format("create procedure dbo.p\n    @AsOfDate      DATETIME,\n    @Debug         BIT = 0\nas select 1;"),
            StringComparison.Ordinal);
    }

    // --- NULL / NOT NULL ---------------------------------------------------------------

    [Theory]
    [InlineData("create table dbo.t (a INT not     null);", "a INT NOT NULL")]
    [InlineData("create table dbo.t (a INT     null);", "a INT NULL")]
    public void NullableConstraintSpacingIsNormalised(string sql, string expected)
    {
        Assert.Contains(expected, Format(sql), StringComparison.Ordinal);
    }

    // --- comments ---------------------------------------------------------------------

    [Fact]
    public void CommentsBetweenParametersSurvive()
    {
        var sql = "CREATE PROCEDURE dbo.p\n    @a INT, -- first\n    /* why */\n    @b INT\nAS\nSELECT 1;";
        var result = Format(sql);

        Assert.Contains("-- first", result, StringComparison.Ordinal);
        Assert.Contains("/* why */", result, StringComparison.Ordinal);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void CommentInsideADeclareSurvives()
    {
        var sql = "DECLARE @a INT /* the counter */, @b INT;";
        var result = Format(sql);

        Assert.Contains("/* the counter */", result, StringComparison.Ordinal);
        Assert.Equal(result, Format(result));
    }
}
