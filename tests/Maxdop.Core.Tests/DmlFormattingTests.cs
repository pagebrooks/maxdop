using Maxdop.Core.Formatting;
using Maxdop.Core.Printing;

namespace Maxdop.Core.Tests;

public class DmlFormattingTests
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

    // --- INSERT -----------------------------------------------------------------------

    [Fact]
    public void SingleRowInsertFormats()
    {
        Assert.Equal(
            """
            INSERT INTO dbo.t (a, b)
            VALUES (1, 2);
            """,
            Format("insert into dbo.t (a,b) values (1,2);"));
    }

    [Fact]
    public void OptionalIntoIsPreservedEitherWay()
    {
        // Both spellings are legal; neither is normalised into the other.
        Assert.StartsWith("INSERT INTO dbo.t", Format("insert into dbo.t values (1);"), StringComparison.Ordinal);
        Assert.StartsWith("INSERT dbo.t", Format("insert dbo.t values (1);"), StringComparison.Ordinal);
    }

    [Fact]
    public void TopFilterOnInsertIsPreserved()
    {
        Assert.StartsWith(
            "INSERT TOP (5) INTO dbo.t",
            Format("insert top (5) into dbo.t select a from u;"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MultiRowValuesBreakOnePerRow()
    {
        // One row per line even when they would fit: adding a row should be a one-line diff.
        Assert.Equal(
            """
            INSERT INTO dbo.t (a, b)
            VALUES
                (1, 2),
                (3, 4),
                (5, 6);
            """,
            Format("insert into dbo.t (a,b) values (1,2),(3,4),(5,6);"));
    }

    [Fact]
    public void LongColumnListBreaks()
    {
        Assert.Equal(
            """
            INSERT INTO dbo.LedgerPosting (
                LedgerId,
                Amount,
                PostedAt
            )
            VALUES (1, 2, 3);
            """,
            Format("insert into dbo.LedgerPosting (LedgerId, Amount, PostedAt) values (1,2,3);", maxWidth: 40));
    }

    [Fact]
    public void InsertSelectFormats()
    {
        Assert.Equal(
            """
            INSERT INTO dbo.t (a, b)
            SELECT x, y
            FROM dbo.u
            WHERE x > 0;
            """,
            Format("insert into dbo.t (a,b) select x, y from dbo.u where x > 0;", maxWidth: 24));
    }

    [Fact]
    public void InsertWithoutColumnListFormats()
    {
        Assert.Equal(
            """
            INSERT INTO dbo.t
            SELECT a FROM dbo.u;
            """,
            Format("insert into dbo.t select a from dbo.u;"));
    }

    [Fact]
    public void DefaultValuesIsPreserved()
    {
        Assert.Equal(
            """
            INSERT INTO dbo.t
            DEFAULT VALUES;
            """,
            Format("insert into dbo.t default values;"));
    }

    // --- UPDATE -----------------------------------------------------------------------

    [Fact]
    public void SimpleUpdateFormats()
    {
        Assert.Equal(
            """
            UPDATE dbo.t
            SET a = 1
            WHERE x = 1;
            """,
            Format("update dbo.t set a=1 where x=1;"));
    }

    [Fact]
    public void MultipleSetClausesBreakWhenWide()
    {
        Assert.Equal(
            """
            UPDATE dbo.LedgerPosting
            SET
                Amount = 100,
                PostedAt = GETDATE()
            WHERE LedgerId = 1;
            """,
            Format("update dbo.LedgerPosting set Amount = 100, PostedAt = GETDATE() where LedgerId = 1;", maxWidth: 30));
    }

    [Fact]
    public void ShortSetListStaysOnTheSetLine()
    {
        Assert.Equal(
            """
            UPDATE dbo.t
            SET a = 1, b = 2
            WHERE x = 1;
            """,
            Format("update dbo.t set a=1, b=2 where x=1;"));
    }

    [Fact]
    public void UpdateWithFromClauseFormats()
    {
        Assert.Equal(
            """
            UPDATE t
            SET t.a = u.b
            FROM dbo.t AS t
            INNER JOIN dbo.u AS u ON u.id = t.id;
            """,
            Format("update t set t.a = u.b from dbo.t as t inner join dbo.u as u on u.id = t.id;", maxWidth: 40));
    }

    [Fact]
    public void UpdateTopFilterIsPreserved()
    {
        Assert.StartsWith("UPDATE TOP (5) dbo.t", Format("update top (5) dbo.t set a = 1;"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("update dbo.t set a += 1;", "SET a += 1")]
    [InlineData("update dbo.t set a -= 1;", "SET a -= 1")]
    [InlineData("update dbo.t set a |= 1;", "SET a |= 1")]
    public void CompoundAssignmentInSetClausesIsPreserved(string sql, string expected)
    {
        Assert.Contains(expected, Format(sql), StringComparison.Ordinal);
    }

    [Fact]
    public void SetClauseAssigningToBothVariableAndColumnKeepsBoth()
    {
        // `SET @a = c1 += NULL` updates the column *and* captures the result. Picking one target
        // silently dropped the other.
        Assert.Equal(
            """
            UPDATE dbo.t
            SET @a = c1 += 5;
            """,
            Format("update dbo.t set @a = c1 += 5;"));
    }

    [Fact]
    public void UpdateWithOutputClauseFormats()
    {
        Assert.Equal(
            """
            UPDATE dbo.t
            SET a = 1
            OUTPUT inserted.a
            WHERE x = 1;
            """,
            Format("update dbo.t set a = 1 OUTPUT inserted.a where x = 1;"));
    }

    // --- OUTPUT ------------------------------------------------------------------------

    [Fact]
    public void OutputClauseGetsItsOwnLine()
    {
        // Was deferred, and that deferral was most of what kept INSERT at the top of the
        // passthrough histogram — the clause is a select list in a different position, not a
        // construct that needed new machinery.
        Assert.Equal(
            """
            INSERT INTO dbo.t (a)
            OUTPUT inserted.a
            VALUES (1);
            """,
            Format("insert into dbo.t (a) OUTPUT inserted.a values (1);"));
    }

    [Fact]
    public void OutputIntoClauseKeepsItsTargetAndColumnList()
    {
        Assert.Equal(
            """
            INSERT INTO dbo.t (a, b)
            OUTPUT inserted.a, inserted.b INTO #log (a, b)
            SELECT a, b FROM dbo.src;
            """,
            Format("insert into dbo.t (a,b) OUTPUT inserted.a, inserted.b into #log (a,b) select a,b from dbo.src;"));
    }

    [Fact]
    public void OutputIntoWithoutAColumnListFormats()
    {
        Assert.Equal(
            """
            INSERT INTO dbo.t (a)
            OUTPUT inserted.a INTO #log
            VALUES (1);
            """,
            Format("insert into dbo.t (a) OUTPUT inserted.a into #log values (1);"));
    }

    [Fact]
    public void OutputKeywordIsRecasedDespiteLexingAsAnIdentifier()
    {
        // OUTPUT is a non-reserved word that lexes as an identifier. Nothing but the keyword can
        // appear in that position, so the printer claims it as a keyword position and the verifier
        // permits the case change — where before it had to leave `output` lower case.
        Assert.Contains(
            "OUTPUT inserted.a",
            Format("insert into dbo.t (a) output inserted.a values (1);"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void LongOutputListBreaksOnePerColumn()
    {
        Assert.Equal(
            """
            INSERT INTO dbo.t (a)
            OUTPUT
                inserted.aaaaaaaaaaaaaaaaaaaa,
                inserted.bbbbbbbbbbbbbbbbbbbb,
                inserted.cccccccccccccccccccc
            VALUES (1);
            """,
            Format(
                "insert into dbo.t (a) OUTPUT inserted.aaaaaaaaaaaaaaaaaaaa, inserted.bbbbbbbbbbbbbbbbbbbb,"
                + " inserted.cccccccccccccccccccc values (1);",
                maxWidth: 60));
    }

    [Fact]
    public void OutputClauseOnDeleteFormats()
    {
        Assert.Equal(
            """
            DELETE FROM dbo.t
            OUTPUT deleted.*
            WHERE a = 1;
            """,
            Format("delete from dbo.t OUTPUT deleted.* where a = 1;"));
    }

    // --- DELETE -----------------------------------------------------------------------

    [Fact]
    public void DeleteKeepsBothOfItsFromKeywords()
    {
        // `DELETE FROM t FROM t JOIN u` is legal: the first FROM belongs to the DELETE syntax and
        // is part of the head slice, the second is a real FromClause. Reconstructing either would
        // have lost one of them.
        Assert.Equal(
            """
            DELETE FROM dbo.t
            FROM dbo.t AS x
            INNER JOIN dbo.u AS y ON x.id = y.id
            WHERE y.z = 1;
            """,
            Format("delete from dbo.t from dbo.t as x inner join dbo.u as y on x.id = y.id where y.z = 1;"));
    }

    [Fact]
    public void DeleteTopFilterSurvives()
    {
        Assert.Equal(
            """
            DELETE TOP (5) FROM dbo.t
            WHERE a = 1;
            """,
            Format("delete top (5) from dbo.t where a = 1;"));
    }

    [Fact]
    public void DeleteWithNoWhereClauseFormats()
    {
        Assert.Equal("DELETE FROM dbo.t;", Format("delete from dbo.t;"));
    }

    // --- SET --------------------------------------------------------------------------

    [Fact]
    public void SimpleAssignmentFormats()
    {
        Assert.Equal("SET @x = 1;", Format("set @x=1;"));
    }

    [Theory]
    [InlineData("set @x += 1;", "SET @x += 1;")]
    [InlineData("set @x -= 1;", "SET @x -= 1;")]
    [InlineData("set @x *= 2;", "SET @x *= 2;")]
    [InlineData("set @x /= 2;", "SET @x /= 2;")]
    [InlineData("set @x %= 2;", "SET @x %= 2;")]
    [InlineData("set @x |= 2;", "SET @x |= 2;")]
    [InlineData("set @x &= 2;", "SET @x &= 2;")]
    [InlineData("set @x ^= 2;", "SET @x ^= 2;")]
    public void CompoundAssignmentOperatorsArePreserved(string input, string expected)
    {
        // Read from the tokens rather than mapped from AssignmentKind: nine operators, no enum to
        // get wrong, and exactly what the author wrote.
        Assert.Equal(expected, Format(input));
    }

    [Fact]
    public void AssignmentFromSubqueryFormats()
    {
        Assert.Equal(
            "SET @x = (SELECT MAX(id) FROM dbo.t);",
            Format("set @x = (select MAX(id) from dbo.t);"));
    }

    [Fact]
    public void AssignmentFromExpressionFormats()
    {
        Assert.Equal("SET @x = @y + @z * 2;", Format("set @x=@y+@z*2;"));
    }

    [Fact]
    public void SelectAssignmentInSelectListFormats()
    {
        Assert.Equal("SELECT @x = a FROM dbo.t;", Format("select @x=a from dbo.t;"));
    }

    [Fact]
    public void MultipleSelectAssignmentsFormat()
    {
        Assert.Equal(
            "SELECT @x = a, @y = b FROM dbo.t;",
            Format("select @x=a, @y=b from dbo.t;"));
    }

    [Fact]
    public void SetOptionStatementsPassThrough()
    {
        // SET NOCOUNT ON is a different statement type entirely, with no handler yet.
        Assert.Equal("SET NOCOUNT ON;", Format("SET NOCOUNT ON;"));
        Assert.Equal("SET ANSI_NULLS ON", Format("SET ANSI_NULLS ON"));
    }

    [Fact]
    public void CursorAssignmentFormats()
    {
        // A CursorDefinition's range starts at its first *option*, so even the `CURSOR` keyword sits in
        // the gap after the `=` and has to be read from the tokens.
        Assert.Equal(
            """
            SET @c = CURSOR FORWARD_ONLY STATIC FOR
            SELECT a FROM dbo.t;
            """,
            Format("set @c = cursor forward_only static for select a from dbo.t;"));
    }

    // --- comments ---------------------------------------------------------------------

    [Fact]
    public void CommentsOnInsertColumnsSurvive()
    {
        Assert.Equal(
            """
            INSERT INTO dbo.t (
                a, -- the key
                b
            )
            VALUES (1, 2);
            """,
            Format("insert into dbo.t (a, -- the key\n b) values (1,2);", maxWidth: 200));
    }

    [Fact]
    public void CommentOnAnAssignmentSurvives()
    {
        Assert.Equal("SET @x = 1; -- why", Format("set @x = 1; -- why"));
    }

    // --- stability --------------------------------------------------------------------

    [Theory]
    [InlineData("insert into dbo.t (a,b) values (1,2);")]
    [InlineData("insert into dbo.t (a,b) values (1,2),(3,4);")]
    [InlineData("insert into dbo.t select a, b from dbo.u where a = 1;")]
    [InlineData("insert into dbo.t default values;")]
    [InlineData("INSERT INTO dbo.t (a) OUTPUT inserted.a VALUES (1);")]
    [InlineData("set @x = 1;")]
    [InlineData("set @x += (select MAX(id) from dbo.t);")]
    [InlineData("select @x = a, @y = b from dbo.t where a = 1;")]
    [InlineData("insert into dbo.t (a, -- note\n b) values (1,2);")]
    [InlineData("if @a = 1 begin insert into dbo.t (a) values (1); set @x = 1; end")]
    public void DmlIsIdempotent(string sql)
    {
        var once = Format(sql, maxWidth: 40);
        var twice = Format(once, maxWidth: 40);
        Assert.Equal(once, twice);
        Assert.Equal(twice, Format(twice, maxWidth: 40));
    }

    // --- FOR clause regression --------------------------------------------------------

    [Theory]
    [InlineData("SELECT a FROM t FOR XML PATH('r');")]
    [InlineData("SELECT a FROM t FOR XML AUTO;")]
    [InlineData("SELECT a FROM t FOR XML RAW;")]
    [InlineData("SELECT a FROM t FOR BROWSE;")]
    [InlineData("SELECT a FROM t ORDER BY a FOR XML PATH('r');")]
    public void ForClauseKeepsAllOfItsKeywords(string sql)
    {
        // A FOR clause's range can begin at its own `FOR` or past `FOR XML`, depending on the
        // variant. A single-token look-back left the option stranded: `ORDER BY a PATH('r')`.
        Assert.Equal(sql, Format(sql));
    }

    // --- BULK INSERT ------------------------------------------------------------------

    [Fact]
    public void BulkInsertPutsItsTargetSourceAndOptionsOnSeparateLines()
    {
        Assert.Equal(
            """
            BULK INSERT dbo.t
            FROM 'c:\data\f.csv'
            WITH (FIELDTERMINATOR = ',', FIRSTROW = 2);
            """,
            Format("bulk insert dbo.t from 'c:\\data\\f.csv' with (fieldterminator = ',', firstrow = 2);"));
    }

    [Fact]
    public void BulkInsertWithNoOptionsHasNoWithClause()
    {
        Assert.Equal(
            """
            BULK INSERT dbo.t
            FROM 'f.csv';
            """,
            Format("bulk insert dbo.t from 'f.csv';"));
    }

    [Fact]
    public void BulkInsertOptionListBreaksOnlyWhenItDoesNotFit()
    {
        // Grouped rather than always broken: `WITH (TABLOCK)` reads worse over three lines, while a real
        // import's load options do not fit on one.
        Assert.Contains("WITH (TABLOCK);", Format("bulk insert dbo.t from 'f.csv' with (tablock);"), StringComparison.Ordinal);

        Assert.Equal(
            """
            BULK INSERT dbo.t
            FROM 'f.csv'
            WITH (
                FIELDTERMINATOR = ',',
                ROWTERMINATOR = '\n',
                FIRSTROW = 2,
                TABLOCK
            );
            """,
            Format(
                "bulk insert dbo.t from 'f.csv' with (fieldterminator = ',', rowterminator = '\\n',"
                + " firstrow = 2, tablock);",
                maxWidth: 40));
    }

    [Fact]
    public void BulkInsertOptionNamesAreRecasedButValuesAreNot()
    {
        // Option names are read from the tokens, not mapped from BulkInsertOptionKind, whose spellings
        // (`TabLock`, `CheckConstraints`) do not match the source. The literals are Printed, so a
        // terminator or a file path keeps its exact text.
        Assert.Equal(
            """
            BULK INSERT dbo.t
            FROM 'C:\Load\MyFile.csv'
            WITH (DATAFILETYPE = 'widechar', ERRORFILE = 'C:\Load\Errors.log');
            """,
            Format(
                "bulk insert dbo.t from 'C:\\Load\\MyFile.csv'"
                + " with (datafiletype = 'widechar', errorfile = 'C:\\Load\\Errors.log');"));
    }

    [Fact]
    public void BulkInsertSpacingAroundEqualsIsNormalised()
    {
        Assert.Contains(
            "WITH (FIRSTROW = 2)",
            Format("BULK INSERT dbo.t FROM 'f.csv' WITH (FIRSTROW=2);"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BulkInsertOrderOptionKeepsItsColumnsAndSortOrders()
    {
        Assert.Contains(
            "WITH (ORDER (a ASC, b DESC), TABLOCK)",
            Format("bulk insert dbo.t from 'f.csv' with (order (a asc, b desc), tablock);"),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BULK INSERT dbo.t FROM 'f.csv';")]
    [InlineData("BULK INSERT dbo.t FROM 'f.csv' WITH (TABLOCK);")]
    [InlineData("BULK INSERT dbo.t FROM 'f.csv' WITH (FIELDTERMINATOR = ',', FIRSTROW = 2, KEEPNULLS);")]
    [InlineData("BULK INSERT dbo.t FROM 'f.csv' WITH (ORDER (a ASC, b DESC));")]
    [InlineData("BULK INSERT dbo.t FROM 'f.csv'\n-- why\nWITH (TABLOCK);")]
    public void BulkInsertIsIdempotent(string sql)
    {
        var once = Format(sql, maxWidth: 40);
        Assert.Equal(once, Format(once, maxWidth: 40));
    }

    [Fact]
    public void ACommentBeforeAnOptionClauseStaysWithThePredicate()
    {
        // The option clause used to sit *inside* the WithComments wrapper around the specification,
        // so a comment trailing the specification was flushed after the clause and the terminator:
        // `AND h.[object_id] IS NULL /*don't duplicate the prior check.*/` came back reading as a
        // remark about `OPTION (RECOMPILE);`. Ten occurrences in one corpus file, invisible to every
        // gate — the comment is neither lost nor reordered against other comments, only against the
        // code. The clause now sits outside the wrapper, mirroring the CTE prologue on the far side.
        Assert.Equal(
            """
            INSERT #Results (check_id)
            SELECT 44 AS check_id
            FROM #IndexSanity i
            WHERE i.index_id = 0 /* don't duplicate the prior check */
            OPTION ( RECOMPILE );
            """,
            Format(
                "insert #Results (check_id) select 44 as check_id from #IndexSanity i "
                + "where i.index_id = 0 /* don't duplicate the prior check */\noption ( recompile );",
                maxWidth: 60));
    }
}
