using Maxdop.Core.Formatting;
using Maxdop.Core.Printing;

namespace Maxdop.Core.Tests;

/// <summary>
/// The long tail: session settings, transactions, cursors, flow control, single-object DDL, and the
/// leaf nodes inside constraints and XML clauses.
/// </summary>
/// <remarks>
/// Individually trivial and collectively the widest-spread gap the corpus had left — <c>SET NOCOUNT
/// ON</c> alone appeared in 31 of 36 real-world files. Nearly all of them go through
/// <c>PrintPartsInTokenOrder</c> rather than having a handler each, so these tests are as much about
/// that helper's spacing rules as about the individual statements.
/// </remarks>
public class SmallStatementFormattingTests
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

    // --- statements with no child nodes at all -----------------------------------------

    // Option names are written in upper case in these inputs on purpose. `NOCOUNT`, `ANSI_NULLS`,
    // `IO`, `ISOLATION`, `XML`, `PATH`, `INCLUDE`, `THROW`, `DELAY`, `NEXT`, `LOCAL` and
    // `XMLNAMESPACES` are all non-reserved words that lex as
    // identifiers, so they keep whatever case the author used — the same rule as `OUTPUT`, `APPLY` and
    // built-in type names. Spacing *is* normalised, which is what these assert.

    [Theory]
    // Their options are enums and bools, so there is nothing to descend into and the whole statement
    // is one keyword run.
    [InlineData("set   NOCOUNT   on;", "SET NOCOUNT ON;")]
    [InlineData("set ANSI_NULLS on;", "SET ANSI_NULLS ON;")]
    [InlineData("set statistics   IO   on;", "SET STATISTICS IO ON;")]
    [InlineData("set transaction ISOLATION LEVEL read   UNCOMMITTED;", "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;")]
    [InlineData("begin   transaction;", "BEGIN TRANSACTION;")]
    [InlineData("commit   tran;", "COMMIT TRAN;")]
    [InlineData("rollback   transaction;", "ROLLBACK TRANSACTION;")]
    [InlineData("break;", "BREAK;")]
    [InlineData("continue;", "CONTINUE;")]
    public void KeywordOnlyStatementsAreNormalised(string input, string expected)
    {
        Assert.Equal(expected, Format(input));
    }

    [Fact]
    public void AbbreviatedTransactionKeywordIsNotExpanded()
    {
        // `TRAN` and `TRANSACTION` are distinct tokens, so expanding one is a change the verifier would
        // reject — and a rewrite nobody asked for.
        Assert.Equal("COMMIT TRAN;", Format("commit tran;"));
        Assert.Equal("COMMIT TRANSACTION;", Format("commit transaction;"));
    }

    // --- statements with one or two children -------------------------------------------

    [Theory]
    [InlineData("return;", "RETURN;")]
    [InlineData("return   0;", "RETURN 0;")]
    [InlineData("return @rc;", "RETURN @rc;")]
    [InlineData("goto   ErrorHandler;", "GOTO ErrorHandler;")]
    [InlineData("waitfor DELAY '00:00:05';", "WAITFOR DELAY '00:00:05';")]
    [InlineData("truncate   table   dbo.t;", "TRUNCATE TABLE dbo.t;")]
    public void SingleChildStatementsAreNormalised(string input, string expected)
    {
        Assert.Equal(expected, Format(input));
    }

    [Fact]
    public void ThrowArgumentsAreCommaSeparatedWithoutAStraySpace()
    {
        // The parts helper joins children with spaces, which is right for keyword separators and wrong
        // for punctuation: without the rule this came out as `THROW 50000 , 'message' , 1`.
        Assert.Equal("THROW 50000, 'message', 1;", Format("THROW 50000,'message',1;"));
        Assert.Equal("THROW;", Format("THROW;"));
    }

    [Theory]
    [InlineData("drop   table   #a;", "DROP TABLE #a;")]
    [InlineData("drop table #a,#b,#c;", "DROP TABLE #a, #b, #c;")]
    [InlineData("drop table if   exists dbo.t;", "DROP TABLE IF EXISTS dbo.t;")]
    public void DropObjectListsAreCommaSeparated(string input, string expected)
    {
        // One handler covers every DROP of a named object via the abstract base, and `IF EXISTS` is a
        // bool with no node — it can only arrive through the keyword slice.
        Assert.Equal(expected, Format(input));
    }

    // --- cursors ------------------------------------------------------------------------

    [Fact]
    public void CursorLifecycleStatementsFormat()
    {
        Assert.Equal("OPEN c;", Format("open   c;"));
        Assert.Equal("CLOSE c;", Format("close   c;"));
        Assert.Equal("DEALLOCATE c;", Format("deallocate   c;"));
    }

    [Fact]
    public void DeclareCursorPutsItsQueryOnItsOwnLine()
    {
        Assert.Equal(
            """
            DECLARE c CURSOR LOCAL FAST_FORWARD FOR
            SELECT a FROM dbo.t;
            """,
            Format("declare c cursor LOCAL FAST_FORWARD for select a from dbo.t;"));
    }

    [Fact]
    public void FetchIntoVariablesSurvive()
    {
        // FetchCursorStatement derives from CursorStatement, whose handler knows only about the cursor
        // name — without its own entry the INTO variables came out as an unformatted tail slice.
        Assert.Equal("FETCH NEXT FROM c INTO @a, @b;", Format("fetch NEXT from c into @a,@b;"));
    }

    // --- expressions --------------------------------------------------------------------

    [Theory]
    [InlineData("select -a from t;", "SELECT -a FROM t;")]
    [InlineData("select ~a from t;", "SELECT ~a FROM t;")]
    [InlineData("select +a from t;", "SELECT +a FROM t;")]
    public void UnaryOperatorsStayAttachedToTheirOperand(string input, string expected)
    {
        Assert.Equal(expected, Format(input));
    }

    [Fact]
    public void DoubleNegationDoesNotBecomeAComment()
    {
        // `- -1` printed without the space is `--1`, which is a line comment and would swallow the rest
        // of the line. The spacing is copied from the source, which cannot produce that.
        Assert.Equal("SELECT - -1;", Format("select - -1;"));
    }

    [Theory]
    [InlineData("select a from t where b between 1 and 10;", "SELECT a FROM t WHERE b BETWEEN 1 AND 10;")]
    [InlineData("select a from t where b not between 1 and 10;", "SELECT a FROM t WHERE b NOT BETWEEN 1 AND 10;")]
    public void BetweenKeepsBothKeywords(string input, string expected)
    {
        Assert.Equal(expected, Format(input));
    }

    [Theory]
    [InlineData("select a from t where b like '%x%';", "SELECT a FROM t WHERE b LIKE '%x%';")]
    [InlineData("select a from t where b not like '%x%';", "SELECT a FROM t WHERE b NOT LIKE '%x%';")]
    [InlineData(@"select a from t where b like '%x%' escape '\';", @"SELECT a FROM t WHERE b LIKE '%x%' ESCAPE '\';")]
    public void LikeKeepsItsOperatorAndEscapeClause(string input, string expected)
    {
        Assert.Equal(expected, Format(input));
    }

    // --- constraints, indexes and table structure ---------------------------------------

    [Fact]
    public void ConstraintKeywordRunStaysTightAgainstItsColumnList()
    {
        // Whether it is a primary key and whether it is clustered are enums with no token range, so
        // they come from the keyword slice — which ends in `(` and must not gain a space after it.
        Assert.Contains(
            "CONSTRAINT PK_t PRIMARY KEY CLUSTERED (a ASC)",
            Format("create table dbo.t (a INT not null, constraint PK_t primary key clustered (a asc));"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void IdentitySeedAndIncrementAreCommaSeparated()
    {
        Assert.Contains(
            "a INT IDENTITY(1, 1) NOT NULL",
            Format("create table dbo.t (a INT identity(1,1) not null);"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AlterTableAddReusesTheTableLayout()
    {
        Assert.Equal("ALTER TABLE dbo.t ADD c INT NULL;", Format("alter table dbo.t add c INT null;"));
    }

    [Fact]
    public void CreateIndexFormats()
    {
        // `dbo.t(a ASC…)` rather than `dbo.t (a ASC…)`: a gap that is only an opening parenthesis binds
        // tight, exactly as in a function call, while a gap that is a keyword run keeps its space —
        // which is why `PRIMARY KEY CLUSTERED (a ASC)` reads the other way.
        Assert.Equal(
            "CREATE NONCLUSTERED INDEX IX_t_a ON dbo.t(a ASC, b DESC) INCLUDE (c);",
            Format("create NONCLUSTERED index IX_t_a on dbo.t (a asc,b desc) INCLUDE (c);"));
    }

    // --- XML ----------------------------------------------------------------------------

    [Fact]
    public void ForXmlOptionsFormat()
    {
        // A ForClause's range can begin past `FOR XML`, which is why the parts helper slices from the
        // corrected start — from the raw index the `FOR XML` would simply disappear.
        Assert.Equal(
            "SELECT a FROM t FOR XML PATH('row'), ROOT('rows');",
            Format("select a from t for XML PATH('row'),ROOT('rows');"));
    }

    [Fact]
    public void XmlNamespaceDeclarationsFormat()
    {
        Assert.Contains(
            "XMLNAMESPACES('http://x' AS p)",
            Format("with XMLNAMESPACES('http://x' as p) select a from t;"),
            StringComparison.Ordinal);
    }

    // --- inline derived tables and dynamic SQL -------------------------------------------

    [Fact]
    public void InlineDerivedTableFormats()
    {
        // A VALUES list standing in for a table; the rows are the same nodes an INSERT uses, so they get
        // the identical layout — which this test used to assert and pin the opposite of. The rows were
        // joined with a line but wrapped in no indent, so a list long enough to break landed at column
        // zero, and the closing parenthesis stayed welded to the last row.
        Assert.Equal(
            """
            SELECT c1
            FROM (VALUES
                (1, 2),
                (3, 4)
            ) AS t (c1, c2);
            """,
            Format("select c1 from (values (1,2),(3,4)) as t (c1,c2);"));
    }

    [Fact]
    public void InsertFromExecuteFormats()
    {
        Assert.Equal(
            """
            INSERT INTO #t (a)
            EXEC dbo.DoThing @p = 1;
            """,
            Format("insert into #t (a) exec dbo.DoThing @p=1;"));
    }

    [Fact]
    public void VariableMethodCallAssignmentFormats()
    {
        // `SET @x.modify(…)` is not an assignment: there is no `=` and no right-hand expression, just a
        // name, a dotted method and its arguments. The `.` must not gain spaces around it.
        Assert.Equal(
            "SET @x.modify(N'delete //@Cost');",
            Format("set @x.modify(N'delete //@Cost');"));
    }
    // --- the last of the plain-SQL gaps -----------------------------------------------

    [Fact]
    public void PositionedUpdateAndDeleteFormat()
    {
        // `WHERE CURRENT OF <cursor>` has a cursor where a condition normally goes. The three words in
        // front of the name are grammar — the cursor is a node, so no name can be in that run.
        Assert.Contains(
            "WHERE CURRENT OF UpdCursor",
            Format("update dbo.t set Total = 1 where current of UpdCursor;"),
            StringComparison.Ordinal);

        Assert.Contains(
            "WHERE CURRENT OF UpdCursor",
            Format("delete from dbo.t where current of UpdCursor;"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteAtLinkedServerKeepsTheServerName()
    {
        // The linked server is an identifier, so it cannot ride in the keyword slice that carries the
        // rest of the tail — recasing it would rename the server.
        Assert.Equal("EXEC ('SELECT 1') AT DataSource;", Format("exec ('SELECT 1') at DataSource;"));
    }

    [Theory]
    [InlineData("select parse('2026-01-01' as date);", "PARSE('2026-01-01' AS DATE)")]
    [InlineData("select parse('x' as int using 'en-US');", "PARSE('x' AS INT USING 'en-US')")]
    public void ParseCallFormats(string sql, string expected)
    {
        // Shaped like CAST but with an optional trailing `USING <culture>`, which is why it cannot share
        // the CAST helper: that one requires the type to be the last thing before the parenthesis.
        Assert.Contains(expected, Format(sql), StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateCallInATriggerFormats()
    {
        Assert.Contains(
            "IF UPDATE(Name)",
            Format("create trigger dbo.tr on dbo.t after update as if update(Name) select 1;"),
            StringComparison.Ordinal);
    }

}
