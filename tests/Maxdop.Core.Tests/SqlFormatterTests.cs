using Maxdop.Core.Formatting;
using Maxdop.Core.Printing;

namespace Maxdop.Core.Tests;

/// <summary>
/// End-to-end tests for the structural spine. Everything below the spine is passthrough for
/// now, so these assert structure, comment survival, and the safety behaviours — not
/// expression layout, which does not exist yet.
/// </summary>
public class SqlFormatterTests
{
    private static string Format(string sql, FormatOptions? options = null)
    {
        var result = SqlFormatter.Format(sql, options);
        if (result.Status != FormatStatus.Formatted)
        {
            Assert.Fail($"{result.Status}: {string.Join(" | ", result.Diagnostics)}");
        }

        return result.Output;
    }

    private static FormatOptions Narrow(int width) =>
        FormatOptions.Default with { Print = PrintOptions.Default with { MaxWidth = width } };

    // --- batches and GO -------------------------------------------------------------

    [Fact]
    public void SingleStatementIsPassedThroughUnchanged()
    {
        Assert.Equal("SELECT 1;", Format("SELECT 1;"));
    }

    [Fact]
    public void BatchSeparatorIsEmittedOnItsOwnLine()
    {
        Assert.Equal(
            """
            SET ANSI_NULLS ON
            GO
            SET QUOTED_IDENTIFIER ON
            GO
            """,
            Format("SET ANSI_NULLS ON\nGO\nSET QUOTED_IDENTIFIER ON\nGO"));
    }

    [Fact]
    public void MissingFinalGoIsNotInvented()
    {
        Assert.Equal(
            """
            SET ANSI_NULLS ON
            GO
            SELECT 1;
            """,
            Format("SET ANSI_NULLS ON\nGO\nSELECT 1;"));
    }

    [Fact]
    public void StatementsWithinABatchAreOnePerLine()
    {
        Assert.Equal(
            """
            SELECT 1;
            SELECT 2;
            SELECT 3;
            """,
            Format("SELECT 1; SELECT 2; SELECT 3;"));
    }

    // --- blank line preservation ----------------------------------------------------

    [Fact]
    public void OneBlankLineBetweenStatementsIsPreserved()
    {
        Assert.Equal(
            """
            SELECT 1;

            SELECT 2;
            """,
            Format("SELECT 1;\n\nSELECT 2;"));
    }

    [Fact]
    public void RunsOfBlankLinesAreCollapsedToTheConfiguredMaximum()
    {
        Assert.Equal(
            """
            SELECT 1;

            SELECT 2;
            """,
            Format("SELECT 1;\n\n\n\n\n\nSELECT 2;"));
    }

    [Fact]
    public void MaxBlankLinesZeroRemovesVerticalGrouping()
    {
        Assert.Equal(
            """
            SELECT 1;
            SELECT 2;
            """,
            Format("SELECT 1;\n\n\nSELECT 2;", FormatOptions.Default with { MaxBlankLines = 0 }));
    }

    [Fact]
    public void BlankLineBetweenBatchesIsPreserved()
    {
        Assert.Equal(
            """
            SELECT 1;
            GO

            SELECT 2;
            GO
            """,
            Format("SELECT 1;\nGO\n\nSELECT 2;\nGO"));
    }

    [Fact]
    public void BlankLineAboveACommentedStatementIsMeasuredFromTheComment()
    {
        // The comment prints as part of the second statement, so the gap that matters is the
        // one above the comment, not the one above the statement itself.
        Assert.Equal(
            """
            SELECT 1;

            -- about the second
            SELECT 2;
            """,
            Format("SELECT 1;\n\n-- about the second\nSELECT 2;"));
    }

    // --- procedures -----------------------------------------------------------------

    [Fact]
    public void ShortProcedureSignatureStaysOnOneLine()
    {
        Assert.Equal(
            """
            CREATE PROCEDURE dbo.p
            AS
            BEGIN
                SELECT 1;
            END
            """,
            Format("create procedure dbo.p as begin select 1; end"));
    }

    [Fact]
    public void ParametersBreakOnePerLineWhenTheSignatureIsTooWide()
    {
        Assert.Equal(
            """
            CREATE PROCEDURE dbo.usp_Reconcile
                @AsOfDate DATETIME,
                @IncludeVoided BIT = 0
            AS
            BEGIN
                SELECT 1;
            END
            """,
            Format(
                "CREATE PROCEDURE dbo.usp_Reconcile @AsOfDate DATETIME, @IncludeVoided BIT = 0 AS BEGIN SELECT 1; END",
                Narrow(60)));
    }

    [Fact]
    public void ShortParameterListStaysInline()
    {
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

    [Fact]
    public void AlterProcedureUsesTheSameHandler()
    {
        Assert.StartsWith(
            "ALTER PROCEDURE dbo.p",
            Format("ALTER PROCEDURE dbo.p AS BEGIN SELECT 1; END"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void CreateOrAlterProcedureUsesTheSameHandler()
    {
        Assert.StartsWith(
            "CREATE OR ALTER PROCEDURE dbo.p",
            Format("CREATE OR ALTER PROCEDURE dbo.p AS BEGIN SELECT 1; END"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProcedureOptionsArePreserved()
    {
        Assert.Equal(
            """
            CREATE PROCEDURE dbo.p
            WITH RECOMPILE
            AS
            BEGIN
                SELECT 1;
            END
            """,
            Format("CREATE PROCEDURE dbo.p WITH RECOMPILE AS BEGIN SELECT 1; END"));
    }

    [Fact]
    public void NestedBlocksIndentCumulatively()
    {
        Assert.Equal(
            """
            BEGIN
                BEGIN
                    SELECT 1;
                END
            END
            """,
            Format("BEGIN BEGIN SELECT 1; END END"));
    }

    // --- keyword casing -------------------------------------------------------------

    [Fact]
    public void KeywordsAreUppercasedByDefault()
    {
        Assert.Contains("CREATE PROCEDURE", Format("create procedure dbo.p as begin select 1; end"), StringComparison.Ordinal);
    }

    [Fact]
    public void KeywordCaseLowerAppliesToSpineKeywords()
    {
        var result = Format(
            "CREATE PROCEDURE dbo.p AS BEGIN SELECT 1; END\nGO",
            FormatOptions.Default with { KeywordCase = KeywordCase.Lower });

        Assert.Contains("create procedure", result, StringComparison.Ordinal);
        Assert.Contains("begin", result, StringComparison.Ordinal);
        Assert.Contains("\ngo", result, StringComparison.Ordinal);
    }

    // --- passthrough ----------------------------------------------------------------

    [Fact]
    public void UnhandledStatementInsideAProcedureIsReindentedNotMangled()
    {
        // This is the payoff of building the spine first: an unhandled statement comes through
        // verbatim — but positioned and indented correctly inside the block. `BULK INSERT` is used
        // here because it is genuinely outside the MVP scope; the example used to be `MERGE`, which
        // now has a handler of its own.
        var result = Format(
            """
            CREATE PROCEDURE dbo.p
            AS
            BEGIN
            BULK INSERT dbo.Target
            FROM 'C:\data\feed.csv'
            WITH (FIELDTERMINATOR = ',');
            END
            """);

        Assert.Equal(
            """
            CREATE PROCEDURE dbo.p
            AS
            BEGIN
                BULK INSERT dbo.Target
                FROM 'C:\data\feed.csv'
                WITH (FIELDTERMINATOR = ',');
            END
            """,
            result);
    }

    [Fact]
    public void MultiLineStringLiteralIsNeverReindented()
    {
        // Leading whitespace inside a literal is data. Re-indenting it would change what the
        // script does, so a block containing one is emitted byte-for-byte instead.
        const string sql = "BEGIN\nDECLARE @s NVARCHAR(MAX) = '\n    keep my spaces\n';\nEND";
        var result = Format(sql);

        Assert.Contains("'\n    keep my spaces\n'", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockCommentInteriorIsNeverReflowed()
    {
        const string sql = "BEGIN\nSELECT 1 /* aligned\n         interior */;\nEND";
        var result = Format(sql);

        Assert.Contains("/* aligned\n         interior */", result, StringComparison.Ordinal);
    }

    // --- comments -------------------------------------------------------------------

    [Fact]
    public void FileHeaderCommentsSurvive()
    {
        Assert.Equal(
            """
            -- ====================
            -- Header block
            -- ====================
            SELECT 1;
            """,
            Format("-- ====================\n-- Header block\n-- ====================\nSELECT 1;"));
    }

    [Fact]
    public void TrailingCommentStaysAtEndOfItsLine()
    {
        Assert.Equal(
            """
            SELECT 1; -- first
            SELECT 2; -- second
            """,
            Format("SELECT 1; -- first\nSELECT 2; -- second"));
    }

    [Fact]
    public void CommentAfterGoStaysWithTheGo()
    {
        Assert.Equal(
            """
            SELECT 1;
            GO -- end of batch one
            SELECT 2;
            GO
            """,
            Format("SELECT 1;\nGO -- end of batch one\nSELECT 2;\nGO"));
    }

    [Fact]
    public void BlockCommentOnItsOwnLineIsNotPulledOntoTheNextStatement()
    {
        Assert.Equal(
            """
            /* about the batch */
            SET ANSI_NULLS ON
            GO
            """,
            Format("/* about the batch */\nSET ANSI_NULLS ON\nGO"));
    }

    [Fact]
    public void BlockCommentSharingALineWithCodeStaysOnThatLine()
    {
        Assert.Equal("/* inline */ SELECT 1;", Format("/* inline */ SELECT 1;"));
    }

    [Fact]
    public void OwnLineCommentBeforeGoStaysOnItsOwnLine()
    {
        // The comment falls inside the batch (its range extends over the GO) and attaches as a
        // trailing comment on the statement. Emitting it as a line suffix would move it up onto
        // the SELECT's line.
        Assert.Equal(
            """
            SELECT 1;
            -- about this batch
            GO
            """,
            Format("SELECT 1;\n-- about this batch\nGO"));
    }

    [Fact]
    public void CommentBetweenParametersStaysOnItsOwnLine()
    {
        Assert.Equal(
            """
            CREATE PROCEDURE dbo.p
                @a INT, -- first
                /* about the second */
                @b INT
            AS
            BEGIN
                SELECT 1;
            END
            """,
            Format(
                "CREATE PROCEDURE dbo.p\n    @a INT, -- first\n    /* about the second */\n    @b INT\nAS\nBEGIN\nSELECT 1;\nEND",
                Narrow(40)));
    }

    [Fact]
    public void CommentOnlyFileKeepsItsComments()
    {
        Assert.Equal(
            """
            -- nothing but
            /* comments here */
            """,
            Format("-- nothing but\n/* comments here */"));
    }

    [Fact]
    public void GnarlyFixtureFormatsAndKeepsEveryComment()
    {
        var sql = TestFiles.Read("gnarly.sql");
        var result = SqlFormatter.Format(sql);

        Assert.Equal(FormatStatus.Formatted, result.Status);

        // The comment-preservation check inside Format already asserts order and content;
        // this pins the count so a silent drop shows up as a test failure too.
        Assert.Equal(20, CountComments(result.Output));
        Assert.Equal(20, CountComments(sql));
    }

    private static int CountComments(string sql)
    {
        var parser = ParserFactory.Create(FormatOptions.Default);
        using var reader = new StringReader(sql);
        var root = parser.Parse(reader, out _);
        return root.ScriptTokenStream.Count(t =>
            t.TokenType is Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.SingleLineComment
                or Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.MultilineComment);
    }

    // --- safety ---------------------------------------------------------------------

    [Fact]
    public void ParseErrorReturnsInputUntouched()
    {
        const string sql = "SELECT FROM WHERE ((( ;";
        var result = SqlFormatter.Format(sql);

        Assert.Equal(FormatStatus.ParseFailed, result.Status);
        Assert.Equal(sql, result.Output);
        Assert.False(result.Changed);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void SqlcmdModeFailsToParseAndPassesThrough()
    {
        // The CLI contract predicted this: SQLCMD directives are not T-SQL. The important part is
        // that the file survives.
        const string sql = ":setvar DbName Foo\nUSE $(DbName);\nGO";
        var result = SqlFormatter.Format(sql);

        Assert.Equal(FormatStatus.ParseFailed, result.Status);
        Assert.Equal(sql, result.Output);
    }

    [Fact]
    public void EmptyInputIsReturnedAsIs()
    {
        var result = SqlFormatter.Format("");
        Assert.Equal(FormatStatus.Formatted, result.Status);
        Assert.Equal("", result.Output);
    }

    [Fact]
    public void FormatRejectsNullInput()
    {
        Assert.Throws<ArgumentNullException>(() => SqlFormatter.Format(null!));
    }

    // --- encoding invariants ------------------------------------------------------

    [Fact]
    public void CrlfInputStaysCrlf()
    {
        var result = Format("SELECT 1;\r\nGO\r\nSELECT 2;\r\nGO");

        Assert.Contains("\r\n", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", result.Replace("\r\n", "|", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void LfInputStaysLf()
    {
        Assert.DoesNotContain('\r', Format("SELECT 1;\nGO\nSELECT 2;\nGO"));
    }

    [Fact]
    public void PresenceOfATrailingNewlineIsPreserved()
    {
        Assert.EndsWith("\n", Format("SELECT 1;\n"), StringComparison.Ordinal);
        Assert.EndsWith(";", Format("SELECT 1;"), StringComparison.Ordinal);
    }

    [Fact]
    public void TrailingCrlfIsPreservedAsCrlf()
    {
        Assert.EndsWith("\r\n", Format("SELECT 1;\r\nGO\r\n"), StringComparison.Ordinal);
    }

    // --- idempotency ----------------------------------------------------------------

    [Theory]
    [InlineData("SELECT 1;")]
    [InlineData("SELECT 1;\n\nSELECT 2;")]
    [InlineData("CREATE PROCEDURE dbo.p @a INT AS BEGIN SELECT 1; END\nGO")]
    [InlineData("-- header\nSELECT 1; -- trailing\nGO\n/* tail */")]
    [InlineData("BEGIN BEGIN SELECT 1; END END")]
    [InlineData("SELECT 1;\r\nGO\r\n")]
    public void FormattingIsIdempotent(string sql)
    {
        var once = Format(sql);
        var twice = Format(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void GnarlyFixtureIsIdempotent()
    {
        var once = Format(TestFiles.Read("gnarly.sql"));
        Assert.Equal(once, Format(once));
    }

    // --- parser versions ------------------------------------------------------------

    [Theory]
    [InlineData(80)]
    [InlineData(130)]
    [InlineData(180)]
    public void EveryParserVersionCanFormat(int version)
    {
        var result = SqlFormatter.Format(
            "SELECT 1;\nGO",
            FormatOptions.Default with { ParserVersion = version });

        Assert.Equal(FormatStatus.Formatted, result.Status);
    }

    [Fact]
    public void UnsupportedParserVersionThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SqlFormatter.Format("SELECT 1;", FormatOptions.Default with { ParserVersion = 999 }));
    }

    [Fact]
    public void ThrowIsUnsupportedOnSql2008Grammar()
    {
        // A real use for pinning: THROW arrived in 2012, so a 2008-target repo should be told
        // rather than have the statement silently accepted.
        var result = SqlFormatter.Format(
            "BEGIN TRY SELECT 1; END TRY BEGIN CATCH THROW; END CATCH",
            FormatOptions.Default with { ParserVersion = 100 });

        Assert.Equal(FormatStatus.ParseFailed, result.Status);
    }

    [Theory]
    [InlineData("2016", 130)]
    [InlineData("2022", 160)]
    [InlineData("latest", 180)]
    [InlineData("fabricdw", 0)]
    [InlineData("130", 130)]
    public void ProductYearsMapToGrammarVersions(string input, int expected)
    {
        Assert.True(ParserFactory.TryParseVersion(input, out var version));
        Assert.Equal(expected, version);
    }

    [Theory]
    [InlineData("2011")]
    [InlineData("nonsense")]
    [InlineData("999")]
    public void UnknownParserVersionStringsAreRejected(string input)
    {
        Assert.False(ParserFactory.TryParseVersion(input, out _));
    }
}
