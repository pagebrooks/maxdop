using Maxdop.Core.Formatting;
using Maxdop.Core.Printing;

namespace Maxdop.Core.Tests;

public class DdlFormattingTests
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

    // --- CREATE TABLE -----------------------------------------------------------------

    [Fact]
    public void ColumnsGetOnePerLine()
    {
        Assert.Equal(
            """
            CREATE TABLE dbo.t (
                a INT NOT NULL,
                b NVARCHAR(50) NULL
            );
            """,
            Format("create table dbo.t (a INT NOT NULL, b NVARCHAR(50) NULL);"));
    }

    [Fact]
    public void ColumnsBreakEvenWhenTheyWouldFit()
    {
        // A schema change should be a one-line diff, so the element list never collapses.
        Assert.Equal(
            """
            CREATE TABLE dbo.t (
                a INT
            );
            """,
            Format("create table dbo.t (a INT);", maxWidth: 200));
    }

    [Fact]
    public void ConstraintsKeepTheirSourceOrderAmongColumns()
    {
        // Columns, constraints and indexes come from three separate ScriptDom lists but interleave
        // freely in the source. Emitting list by list would reorder the schema.
        Assert.Equal(
            """
            CREATE TABLE dbo.t (
                a INT NOT NULL,
                CONSTRAINT PK_t PRIMARY KEY (a),
                b INT NULL,
                CONSTRAINT UQ_t UNIQUE (b)
            );
            """,
            Format("create table dbo.t (a INT NOT NULL, CONSTRAINT PK_t PRIMARY KEY (a), b INT NULL, CONSTRAINT UQ_t UNIQUE (b));"));
    }

    [Fact]
    public void ColumnDefinitionWhitespaceIsNormalised()
    {
        // Hand-maintained column alignment is *not* preserved. That is the deliberate choice an
        // opinionated formatter has to make — Prettier does the same — and it is only safe because
        // the handler accounts for every token in the definition's range rather than enumerating
        // the fifteen optional properties, several of which are flags with no node to enumerate.
        Assert.Equal(
            """
            CREATE TABLE dbo.t (
                [A] INT IDENTITY (1, 1) NOT NULL,
                [B] VARCHAR (50) COLLATE Latin1_General_CI_AS NULL
            );
            """,
            Format(
                "create table dbo.t (\n    [A]  INT           IDENTITY (1, 1) NOT NULL,\n"
                + "    [B]  VARCHAR (50) COLLATE Latin1_General_CI_AS NULL\n);"));
    }

    [Theory]
    // The properties that are flags rather than AST nodes, so nothing in the ColumnDefinition
    // enumerates them. Each has to arrive via a gap or tail token slice.
    [InlineData("create table dbo.t (a INT NOT NULL, c AS (a * 2) PERSISTED);", "c AS (a * 2) PERSISTED")]
    [InlineData("create table dbo.t (a UNIQUEIDENTIFIER ROWGUIDCOL NOT NULL);", "ROWGUIDCOL")]
    [InlineData("create table dbo.t (a INT SPARSE NULL);", "SPARSE")]
    [InlineData("create table dbo.t (a INT IDENTITY (1, 1) NOT FOR REPLICATION NOT NULL);", "NOT FOR REPLICATION")]
    [InlineData("create table dbo.t (a VARBINARY(MAX) FILESTREAM NULL);", "FILESTREAM")]
    public void ColumnFlagsWithNoNodeOfTheirOwnSurvive(string sql, string expected)
    {
        Assert.Contains(expected, Format(sql), StringComparison.Ordinal);
    }

    [Fact]
    public void ComputedColumnExpressionIsFormatted()
    {
        // The point of printing the parts as nodes rather than slicing the whole definition: the
        // printer descends into the parts that hold real expressions. `(a*2)` becoming `(a * 2)` is
        // the proof — a single verbatim slice could not do that.
        Assert.Contains(
            "c AS (a * 2) PERSISTED",
            Format("create table dbo.t (a INT NOT NULL, c AS (a*2) PERSISTED);"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void CommentInsideAColumnDefinitionSurvives()
    {
        var sql = "CREATE TABLE dbo.t (\n    a INT /* surrogate key */ NOT NULL\n);";
        var result = Format(sql);

        Assert.Contains("/* surrogate key */", result, StringComparison.Ordinal);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void TemporalPeriodIsPreserved()
    {
        var result = Format("""
            CREATE TABLE dbo.t (
                a INT NOT NULL,
                s DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
                e DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
                PERIOD FOR SYSTEM_TIME (s, e)
            );
            """);

        Assert.Contains("PERIOD FOR SYSTEM_TIME (s, e)", result, StringComparison.Ordinal);
    }

    [Theory]
    // Table-level trailing clauses now format, on the closing-parenthesis line. This was deferred
    // for a while because a naive slice had silently dropped a temporal table's entire
    // SYSTEM_VERSIONING clause; what makes the slice safe is verifying that the tail starts with the
    // closing parenthesis and that every option node ends inside the range being sliced.
    [InlineData("CREATE TABLE dbo.t (a INT) ON [PRIMARY];", ") ON [PRIMARY];")]
    [InlineData("CREATE TABLE dbo.t (a INT) WITH (MEMORY_OPTIMIZED = ON);", ") WITH (MEMORY_OPTIMIZED = ON);")]
    [InlineData("CREATE TABLE dbo.t (a INT, b VARCHAR(MAX)) TEXTIMAGE_ON [PRIMARY];", ") TEXTIMAGE_ON [PRIMARY];")]
    [InlineData("CREATE TABLE dbo.t (a INT) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];", ") ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];")]
    public void TableLevelOptionsLandOnTheClosingLine(string sql, string expectedTail)
    {
        var result = Format(sql);

        Assert.EndsWith(expectedTail, result, StringComparison.Ordinal);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void SystemVersioningClauseIsNotLost()
    {
        const string sql = """
            CREATE TABLE dbo.t (
                a INT NOT NULL,
                s DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
                e DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
                PERIOD FOR SYSTEM_TIME (s, e)
            )
            WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.t_history));
            """;

        Assert.Contains("SYSTEM_VERSIONING = ON", Format(sql), StringComparison.Ordinal);
        Assert.Contains("HISTORY_TABLE = dbo.t_history", Format(sql), StringComparison.Ordinal);
    }

    [Fact]
    public void CtasPassesThrough()
    {
        const string sql = "CREATE TABLE dbo.t AS SELECT a FROM dbo.u;";
        Assert.Equal(sql, Format(sql));
    }

    [Fact]
    public void CommentsOnColumnsSurvive()
    {
        Assert.Equal(
            """
            CREATE TABLE dbo.t (
                a INT NOT NULL, -- the key
                b INT NULL
            );
            """,
            Format("create table dbo.t (a INT NOT NULL, -- the key\n b INT NULL);"));
    }

    // --- APPLY is a non-reserved word -------------------------------------------------

    [Fact]
    public void ApplyIsRecasedDespiteLexingAsAnIdentifier()
    {
        // `APPLY` lexes as an identifier, so recasing the join keyword run used to be a token change
        // the verifier rejected. Every word the grammar allows between two table references is a join
        // keyword or a join hint — a name cannot appear — so the printer now claims that region and
        // both spellings come out the same.
        const string expected =
            """
            SELECT a
            FROM t
            CROSS APPLY dbo.f(t.id) AS x
            """;

        Assert.Equal(expected, Format("select a from t cross apply dbo.f(t.id) as x"));
        Assert.Equal(expected, Format("select a from t CROSS APPLY dbo.f(t.id) as x"));
    }

    // --- stability --------------------------------------------------------------------

    [Theory]
    [InlineData("update dbo.t set a = 1 where x = 1;")]
    [InlineData("update dbo.t set a = 1, b = 2, c = 3 where x = 1;")]
    [InlineData("update t set t.a = u.b from dbo.t as t inner join dbo.u as u on u.id = t.id;")]
    [InlineData("update dbo.t set @a = c1 += 5;")]
    [InlineData("UPDATE dbo.t SET a = 1 OUTPUT inserted.a WHERE x = 1;")]
    [InlineData("create table dbo.t (a INT NOT NULL, b NVARCHAR(50) NULL);")]
    [InlineData("create table dbo.t (a INT, CONSTRAINT PK_t PRIMARY KEY (a));")]
    [InlineData("CREATE TABLE dbo.t (a INT) ON [PRIMARY];")]
    [InlineData("create table dbo.t (a INT NOT NULL, -- note\n b INT NULL);")]
    [InlineData("select a from t cross apply dbo.f(t.id) as x")]
    [InlineData("if @a = 1 begin update dbo.t set a = 1; create table #x (b INT); end")]
    [InlineData("CREATE TABLE #t (a INT, b INT,);")]
    [InlineData(TemporalTable)]
    [InlineData("ALTER TABLE dbo.t ADD PERIOD FOR SYSTEM_TIME (s, e);")]
    public void DdlIsIdempotent(string sql)
    {
        var once = Format(sql, maxWidth: 40);
        var twice = Format(once, maxWidth: 40);
        Assert.Equal(once, twice);
        Assert.Equal(twice, Format(twice, maxWidth: 40));
    }

    // --- temporal tables --------------------------------------------------------------

    private const string TemporalTable =
        "create table dbo.h (id int not null primary key, v int, "
        + "sysstart datetime2 generated always as row start not null, "
        + "sysend datetime2 generated always as row end not null, "
        + "period for system_time (sysstart, sysend)) "
        + "with (system_versioning = on (history_table = dbo.hHist));";

    [Fact]
    public void TemporalTableColumnsGetOnePerLine()
    {
        // This was the single largest coverage gap in the corpus — 6,935 tokens across 11 files — and
        // none of it was the period's fault beyond a range quirk: `PERIOD FOR SYSTEM_TIME (s, e)` is
        // given the range `s, e)`, so the four tokens that name it fell into the gap between elements,
        // the gap was not a bare comma, and the whole definition went verbatim. Every column here was
        // ordinary.
        Assert.Equal(
            """
            CREATE TABLE dbo.h (
                id INT NOT NULL PRIMARY KEY,
                v INT,
                sysstart DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
                sysend DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
                PERIOD FOR SYSTEM_TIME (sysstart, sysend)
            ) WITH (system_versioning = ON (history_table = dbo.hHist));
            """,
            Format(TemporalTable));
    }

    [Fact]
    public void PeriodColumnNamesKeepTheirExactSpelling()
    {
        // The words around the columns are recased; the columns are Printed, not sliced, so a
        // case-sensitive collation still finds them.
        Assert.Contains(
            "PERIOD FOR SYSTEM_TIME (SysStartTime, SysEndTime)",
            Format(
                "create table dbo.h (id int not null, SysStartTime datetime2 generated always as row start not null,"
                + " SysEndTime datetime2 generated always as row end not null,"
                + " period for system_time (SysStartTime, SysEndTime));"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AlterTableAddPeriodDoesNotEmitItsKeywordsTwice()
    {
        // The correction to the period's range has to propagate up through the TableDefinition that
        // holds it, because a definition containing only a period starts where the period does. Fixing
        // one without the other produced `ALTER TABLE T ADD PERIOD FOR SYSTEM_TIME ( PERIOD FOR
        // SYSTEM_TIME (s, e)` and three corpus files stopped parsing.
        Assert.Equal(
            "ALTER TABLE dbo.t ADD PERIOD FOR SYSTEM_TIME (s, e);",
            Format("ALTER TABLE dbo.t ADD PERIOD FOR SYSTEM_TIME (s, e);"));
    }

    [Fact]
    public void ColumnNamedPeriodIsNotMistakenForOne()
    {
        // The range correction matches on the word sequence, since neither PERIOD nor SYSTEM_TIME has a
        // token type of its own. All four words plus the parenthesis must be in place, which is what
        // stops an ordinary column called `period` from claiming tokens in front of it.
        Assert.Equal(
            """
            CREATE TABLE dbo.t (
                period INT NULL,
                system_time INT NULL
            );
            """,
            Format("create table dbo.t (period int null, system_time int null);"));
    }

    // --- GENERATED ALWAYS casing ------------------------------------------------------

    [Theory]
    [InlineData("generated always as row start", "GENERATED ALWAYS AS ROW START")]
    [InlineData("GENERATED ALWAYS AS ROW START", "GENERATED ALWAYS AS ROW START")]
    [InlineData("generated always as row start hidden", "GENERATED ALWAYS AS ROW START HIDDEN")]
    public void GeneratedAlwaysRunIsRecased(string written, string expected)
    {
        // Every word here is fixed by the GeneratedAlways enum and the IsHidden flag — the parser has
        // already decided this is `RowStart`, so none of it can be an object name. That is what makes
        // recasing provable; the column's gaps as a whole are *not* safe to recase, because the same
        // region carries COLLATE, CONSTRAINT and REFERENCES.
        Assert.Contains(
            $"s DATETIME2 {expected} NOT NULL",
            Format(
                $"create table dbo.h (id int, s datetime2 {written} not null,"
                + " e datetime2 generated always as row end not null, period for system_time (s, e));"),
            StringComparison.Ordinal);
    }

    [Theory]
    // Each of these puts a name in the same region the run above lives in. Recasing any of them would
    // be silent corruption under a case-sensitive collation.
    [InlineData("create table dbo.t (b nvarchar(50) collate SQL_Latin1_General_CP1_CI_AS null);", "SQL_Latin1_General_CP1_CI_AS")]
    [InlineData("create table dbo.t (c int constraint Start_Value default 0);", "Start_Value")]
    [InlineData("create table dbo.t (d int constraint Hidden_Flag check (d > 0));", "Hidden_Flag")]
    [InlineData("create table dbo.t (p int constraint FK_p foreign key references dbo.Parent (Id));", "dbo.Parent")]
    public void NamesBesideTheGeneratedAlwaysRunAreNotRecased(string sql, string preserved)
    {
        Assert.Contains(preserved, Format(sql), StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnsNamedAfterTheseKeywordsAreLeftAlone()
    {
        // The run is anchored on the token after the data type and matched on `GENERATED ALWAYS AS`, so a
        // column *called* `generated` or `hidden` is not caught by it — a spelling-based rule would have
        // renamed both.
        Assert.Equal(
            """
            CREATE TABLE dbo.t (
                hidden INT NULL,
                generated INT NULL,
                start INT NULL
            );
            """,
            Format("create table dbo.t (hidden int null, generated int null, start int null);"));
    }

    // --- trailing commas --------------------------------------------------------------

    [Fact]
    public void TrailingCommaInAColumnListIsKeptAndTheTableStillFormats()
    {
        // Four of the five real-world CREATE TABLE statements the corpus declined were this: First
        // Responder Kit writes it, the parser accepts it, and the tail guard needed the region after the
        // last column to begin with `)`.
        //
        // The comma is emitted rather than dropped. Removing a token is not a tidy-up here — the
        // verifier compares token sequences, so it would be a refusal, and the input is valid as
        // written.
        Assert.Equal(
            """
            CREATE TABLE #stats_agg (
                SqlHandle VARBINARY(64),
                LastUpdate DATETIME2(7),
            );
            """,
            Format("CREATE TABLE #stats_agg\n(\n    SqlHandle VARBINARY(64),\n\tLastUpdate DATETIME2(7),\n);"));
    }

    [Fact]
    public void TrailingCommaSurvivesEvenWhenTheColumnListItselfIsDeclined()
    {
        // The regression that made both halves of this move into one handler. When the element list
        // falls back to verbatim, the comma is outside the definition's range — so a tail that skipped
        // it while the definition declined to emit it lost the token outright, which the verifier caught
        // in AlwaysEncryptedTests130.sql as "expected Comma, got RightParenthesis".
        const string sql =
            "CREATE TABLE dbo.t (\n"
            + "    Age INT NULL,\n"
            + "    ACTNO VARCHAR(11) ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK,"
            + " ENCRYPTION_TYPE = RANDOMIZED, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256'),\n"
            + ");";

        var result = Format(sql).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(",\n);", result, StringComparison.Ordinal);
        Assert.Equal(result, Format(result).Replace("\r\n", "\n", StringComparison.Ordinal));
    }
}
