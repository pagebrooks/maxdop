using System.Runtime.ExceptionServices;
using Maxdop.Core.Formatting;
using Maxdop.Core.Printing;

namespace Maxdop.Core.Tests;

public class SelectFormattingTests
{
    private static string Format(string sql, int maxWidth = 80, FormatOptions? options = null)
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

    // --- one query, one line, when it fits ------------------------------------------

    [Theory]
    [InlineData("select 1;", "SELECT 1;")]
    [InlineData("select a from t;", "SELECT a FROM t;")]
    [InlineData("select a, b from dbo.t where a = 1;", "SELECT a, b FROM dbo.t WHERE a = 1;")]
    [InlineData("select * from t;", "SELECT * FROM t;")]
    [InlineData("select t.* from t;", "SELECT t.* FROM t;")]
    [InlineData("SELECT   a   ,   b   FROM   t  ;", "SELECT a, b FROM t;")]
    public void ShortQueriesStayOnOneLine(string input, string expected)
    {
        Assert.Equal(expected, Format(input));
    }

    [Fact]
    public void MissingSemicolonIsNotInvented()
    {
        Assert.Equal("SELECT a FROM t", Format("select a from t"));
    }

    // --- breaking at clause boundaries ----------------------------------------------

    [Fact]
    public void LongQueryBreaksAtClauseBoundaries()
    {
        Assert.Equal(
            """
            SELECT
                LedgerIdentifier,
                AmountInCents,
                PostedAtUtc
            FROM dbo.LedgerPosting
            WHERE PostedAtUtc <= @AsOfDate
            """,
            Format("select LedgerIdentifier, AmountInCents, PostedAtUtc from dbo.LedgerPosting where PostedAtUtc <= @AsOfDate", maxWidth: 40));
    }

    [Fact]
    public void SelectListStaysInlineWhenOnlyTheQueryIsTooLong()
    {
        // The outer group breaks so clauses land on their own lines, but the select list is
        // measured separately and still fits. That per-group decision is the whole point of the
        // layout algorithm.
        Assert.Equal(
            """
            SELECT a, b
            FROM dbo.SomeReasonablyLongTableName
            WHERE a = 1
            """,
            Format("select a, b from dbo.SomeReasonablyLongTableName where a = 1", maxWidth: 40));
    }

    [Fact]
    public void AlwaysBreakSelectListForcesOnePerLine()
    {
        Assert.Equal(
            """
            SELECT
                a,
                b
            FROM t
            """,
            Format("select a, b from t", options: FormatOptions.Default with { AlwaysBreakSelectList = true }));
    }

    [Fact]
    public void AlwaysBreakWhereAlignsPredicatesUnderTheirOperators()
    {
        // Editing, not looks: one predicate per line lets a filter be commented out while working
        // on a query. It cannot help the *first* predicate — commenting that leaves `WHERE AND …` —
        // which is why the `WHERE 1 = 1` idiom exists; maxdop will not insert that, being a rewrite
        // rather than a layout choice.
        Assert.Equal(
            """
            SELECT a
            FROM t
            WHERE
                    o.OrderDate >= @Start
                AND o.OrderDate < @End
                AND o.Status <> 'cancelled'
            """,
            Format(
                "select a from t where o.OrderDate >= @Start and o.OrderDate < @End and o.Status <> 'cancelled'",
                options: FormatOptions.Default with { AlwaysBreakWhere = true }));
    }

    [Fact]
    public void AlwaysBreakWhereLeavesASinglePredicateUnaligned()
    {
        // No operators, so no operator column to align to.
        Assert.Equal(
            """
            SELECT a
            FROM t
            WHERE
                a = 1
            """,
            Format("select a from t where a = 1", options: FormatOptions.Default with { AlwaysBreakWhere = true }));
    }

    [Fact]
    public void AlwaysBreakWhereKeepsAMixedChainNested()
    {
        // `a AND b OR c` is `(a AND b) OR c`. Flattening it into three peers would read as if it
        // evaluated top to bottom — a claim about precedence the SQL does not make — so the inner
        // run stays together and only the top-level operator gets a line.
        Assert.Equal(
            """
            SELECT a
            FROM t
            WHERE
                   a = 1 AND b = 2
                OR c = 3
            """,
            Format(
                "select a from t where a = 1 and b = 2 or c = 3",
                options: FormatOptions.Default with { AlwaysBreakWhere = true }));
    }

    [Fact]
    public void AlwaysBreakWhereAppliesToHavingToo()
    {
        Assert.Equal(
            """
            SELECT a
            FROM t
            GROUP BY a
            HAVING
                    SUM(b) > 1000
                AND COUNT(*) > 5
            """,
            Format(
                "select a from t group by a having sum(b) > 1000 and count(*) > 5",
                options: FormatOptions.Default with { AlwaysBreakWhere = true }));
    }

    [Fact]
    public void AlwaysBreakWhereKeepsACommentAttachedToTheConditionItself()
    {
        // The aligned path prints the chain's operands rather than the chain node, and comment
        // emission is centralised in Print — so a comment on the node itself was dropped, and the
        // comment gate turned the whole file into a refusal. Found in sp_BlitzIndex, which has
        // exactly this shape. The clause falls back to the ordinary condition layout here.
        Assert.Equal(
            """
            SELECT 1
            FROM t
            WHERE
                /* keep me */
                a = 1 AND b = 2
            """,
            Format(
                "select 1 from t\nwhere\n/* keep me */\na = 1\nand b = 2",
                options: FormatOptions.Default with { AlwaysBreakWhere = true }));
    }

    [Fact]
    public void GraphPathAliasSurvivesLowerCaseKeywords()
    {
        // `FOR PATH AS x` after a derived table. PATH is not reserved and lexes as an identifier, so
        // emitting the run as recased *text* was a token change the verifier rejected — every file
        // with FOR PATH refused under keywordCase: lower, while the default upper-case hid it
        // because recasing PATH changed nothing. Found by running the corpus once per option.
        Assert.Equal(
            """
            select *
            from (
                select * from NODE
            ) for path as N2
            """,
            Format(
                "SELECT * FROM (SELECT * FROM NODE) FOR PATH AS N2",
                options: FormatOptions.Default with { KeywordCase = KeywordCase.Lower }));
    }

    [Fact]
    public void AlwaysBreakWhereIsOffByDefault()
    {
        Assert.Equal(
            "SELECT a FROM t WHERE a = 1 AND b = 2",
            Format("select a from t where a = 1 and b = 2"));
    }

    [Fact]
    public void AlwaysBreakSelectListLeavesSingleColumnAlone()
    {
        Assert.Equal("SELECT a FROM t", Format("select a from t", options: FormatOptions.Default with { AlwaysBreakSelectList = true }));
    }

    [Fact]
    public void LeadingCommasKeepEveryItemInOneColumn()
    {
        Assert.Equal(
            """
            SELECT
                  LedgerIdentifier
                , AmountInCents
                , PostedAtUtc
            FROM t
            """,
            Format(
                "select LedgerIdentifier, AmountInCents, PostedAtUtc from t",
                maxWidth: 40,
                options: FormatOptions.Default with { LeadingCommas = true }));
    }

    [Fact]
    public void LeadingCommasAddNoPaddingToAListThatFits()
    {
        // The pad that lines the first item up with the rest is inside an IfBreak: on one line it
        // would be two stray spaces after SELECT.
        Assert.Equal(
            "SELECT a, b FROM t",
            Format("select a, b from t", options: FormatOptions.Default with { LeadingCommas = true }));
    }

    // --- aliases ---------------------------------------------------------------------

    [Fact]
    public void AsAliasIsPreserved()
    {
        Assert.Equal("SELECT a AS Alpha FROM t", Format("select a as Alpha from t"));
    }

    [Fact]
    public void AliasWithoutAsKeywordStaysWithoutIt()
    {
        Assert.Equal("SELECT a Alpha FROM t", Format("select a Alpha from t"));
    }

    [Fact]
    public void AssignmentStyleAliasIsNotRewrittenToAs()
    {
        // `Alpha = a` and `a AS Alpha` produce identical ASTs, so every safety check would pass
        // if this were normalised. It is still a rewrite nobody asked for.
        Assert.Equal("SELECT Alpha = a FROM t", Format("select Alpha = a from t"));
    }

    [Fact]
    public void TableAliasKeywordIsNormalisedButIdentifiersAreNot()
    {
        Assert.Equal("SELECT l.a FROM dbo.Ledger AS l", Format("select l.a from dbo.Ledger as l"));
        Assert.Equal("SELECT l.a FROM dbo.Ledger l", Format("select l.a from dbo.Ledger l"));
    }

    [Fact]
    public void BracketQuotedIdentifiersAreLeftExactlyAsWritten()
    {
        Assert.Equal(
            "SELECT [My Column] AS [My Alias] FROM [dbo].[My Table]",
            Format("select [My Column] as [My Alias] from [dbo].[My Table]"));
    }

    // --- joins -----------------------------------------------------------------------

    [Fact]
    public void EvenAShortJoinGoesOnItsOwnLine()
    {
        // This used to assert the opposite — a short join stayed on the FROM line. A join is a
        // relationship between tables, and reading the relationships down the left edge is what
        // makes a FROM clause scannable, so the chain breaks however short it is. The ON keeps its
        // own group and stays attached while it fits.
        Assert.Equal(
            """
            SELECT a
            FROM t
            INNER JOIN u ON u.id = t.id
            """,
            Format("select a from t inner join u on u.id = t.id"));
    }

    [Fact]
    public void ACommentBeforeTheOnKeepsTheConditionOnItsOwnLine()
    {
        // An end-of-line comment is emitted as a line suffix, so it is pushed to the end of whatever
        // line it lands on. Pulling the condition up put the comment after it —
        // `INNER JOIN u ON u.id = t.id -- keep me with the join` — which reads as a note about the
        // condition rather than about the table. Nothing was lost and the file was still a fixed
        // point, so no safety gate objected; it was simply the wrong thing to have written.
        Assert.Equal(
            """
            SELECT a
            FROM t
            INNER JOIN u -- about the table
                ON u.id = t.id
            """,
            Format("select a from t inner join u -- about the table\non u.id = t.id"));
    }

    [Fact]
    public void WithNoCommentTheOnStaysOnTheJoinLine()
    {
        Assert.Equal(
            """
            SELECT a
            FROM t
            INNER JOIN u ON u.id = t.id
            """,
            Format("select a from t inner join u on u.id = t.id"));
    }

    [Fact]
    public void JoinsLineUpUnderFrom()
    {
        // Joins sit at FROM's own indent rather than a level right, which is the prevailing
        // T-SQL convention and keeps a chain of joins readable.
        Assert.Equal(
            """
            SELECT l.LedgerId
            FROM dbo.Ledger AS l
            INNER JOIN dbo.Rollup AS r
                ON r.LedgerId = l.LedgerId
            """,
            Format("select l.LedgerId from dbo.Ledger as l inner join dbo.Rollup as r on r.LedgerId = l.LedgerId", maxWidth: 40));
    }

    [Fact]
    public void JoinChainIsFlattenedSoEveryJoinSitsAtFromsIndent()
    {
        // Joins nest left-associatively, so without flattening they would indent one level deeper
        // per join — the second join further right than the first, which reads as arbitrary. The
        // chain is flattened so every join sits at FROM's own indent.
        Assert.Equal(
            """
            SELECT a
            FROM dbo.Ledger AS l
            INNER JOIN dbo.Rollup AS r ON r.LedgerId = l.LedgerId
            LEFT JOIN dbo.Override AS o ON o.LedgerId = l.LedgerId
            """,
            Format(
                "select a from dbo.Ledger as l inner join dbo.Rollup as r on r.LedgerId = l.LedgerId left join dbo.Override as o on o.LedgerId = l.LedgerId",
                maxWidth: 60));
    }

    [Fact]
    public void EachJoinInAChainGetsItsOwnLineWithItsOnAttached()
    {
        // Width is irrelevant here — 120 columns is ample and it still breaks — while each ON is
        // measured on its own and stays on the join's line. The two behaviours are separate groups:
        // one that always breaks, one that breaks only under its own pressure.
        Assert.Equal(
            """
            SELECT a
            FROM t
            INNER JOIN u ON u.i = t.i
            LEFT JOIN v ON v.i = t.i
            """,
            Format("select a from t inner join u on u.i = t.i left join v on v.i = t.i", maxWidth: 120));
    }

    [Fact]
    public void SemicolonBeforeWithIsKeptOnTheCteLine()
    {
        // The defensive `;WITH` idiom. ScriptDom folds the bare semicolon into the preceding
        // statement's range, so it arrives already on a line of its own; without suppressing the
        // separator it stays there, looking like a mistake. (DECLARE has no handler yet, so it
        // passes through with the author's casing.)
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

    [Fact]
    public void CommentBeforeSemicolonWithStaysAbove()
    {
        Assert.Equal(
            """
            DECLARE @n INT;
            -- about the CTE
            ;WITH c AS (
                SELECT a FROM t
            )
            SELECT a FROM c;
            """,
            Format("DECLARE @n INT;\n-- about the CTE\n;with c as (select a from t) select a from c;", maxWidth: 60));
    }

    [Fact]
    public void JoinTypeIsTakenFromTheSource()
    {
        Assert.Contains("LEFT OUTER JOIN", Format("select a from t left outer join u on u.id = t.id", maxWidth: 30), StringComparison.Ordinal);
        Assert.Contains("CROSS JOIN", Format("select a from t cross join u", maxWidth: 30), StringComparison.Ordinal);
    }

    [Fact]
    public void MultiPredicateJoinConditionBreaksUnderOn()
    {
        Assert.Equal(
            """
            SELECT a
            FROM t
            LEFT JOIN u
                ON u.id = t.id
                    AND u.effective <= @asOf
            """,
            Format("select a from t left join u on u.id = t.id and u.effective <= @asOf", maxWidth: 30));
    }

    [Fact]
    public void TableHintsArePreserved()
    {
        Assert.Equal(
            "SELECT a FROM dbo.t AS p WITH (NOLOCK)",
            Format("select a from dbo.t AS p WITH (NOLOCK)"));

        Assert.Equal(
            "SELECT a FROM dbo.t AS p WITH (INDEX(IX_Posted_At))",
            Format("select a from dbo.t AS p WITH (INDEX(IX_Posted_At))"));
    }

    [Fact]
    public void TableHintKeywordsAreCasedButHintNamesAndIndexNamesAreNot()
    {
        // Casing is applied per token: `WITH` is reserved and follows the option, while `nolock`
        // and an index name are non-reserved words that lex as identifiers and are left alone.
        // Case-folding an identifier changes which object is referenced under a case-sensitive
        // collation, so the verifier rejects it — this is not a style choice.
        Assert.Equal(
            "SELECT a FROM dbo.t AS p WITH (nolock)",
            Format("select a from dbo.t as p with (nolock)"));

        Assert.Equal(
            "SELECT a FROM dbo.t AS p WITH (INDEX(ix_posted_at))",
            Format("select a from dbo.t as p with (index(ix_posted_at))"));
    }

    [Fact]
    public void CommaSeparatedTableReferencesStillWork()
    {
        Assert.Equal("SELECT a FROM t, u WHERE t.id = u.id", Format("select a from t, u where t.id = u.id"));
    }

    // --- WHERE ----------------------------------------------------------------------

    [Fact]
    public void PredicateChainKeepsFirstTermOnTheWhereLine()
    {
        Assert.Equal(
            """
            SELECT a
            FROM t
            WHERE alpha = 1
                AND beta = 2
                AND gamma = 3
            """,
            Format("select a from t where alpha = 1 and beta = 2 and gamma = 3", maxWidth: 30));
    }

    [Fact]
    public void AndChainIsFlattenedRatherThanNested()
    {
        // ScriptDom parses these left-associatively as ((a AND b) AND c). Printing that shape
        // directly would indent each successive predicate one level further.
        var result = Format("select a from t where a = 1 and b = 2 and c = 3 and d = 4", maxWidth: 25);
        var predicateLines = result.Split('\n').Where(l => l.TrimStart().StartsWith("AND", StringComparison.Ordinal)).ToList();

        Assert.Equal(3, predicateLines.Count);
        Assert.All(predicateLines, line => Assert.Equal("    AND", line[..7]));
    }

    [Fact]
    public void OperatorSpellingIsPreserved()
    {
        // `!=` is not silently normalised to `<>`.
        Assert.Equal("SELECT a FROM t WHERE a != 1", Format("select a from t where a != 1"));
        Assert.Equal("SELECT a FROM t WHERE a <> 1", Format("select a from t where a <> 1"));
    }

    [Fact]
    public void IsNullAndIsNotNullAreEmitted()
    {
        Assert.Equal("SELECT a FROM t WHERE a IS NULL", Format("select a from t where a is null"));
        Assert.Equal("SELECT a FROM t WHERE a IS NOT NULL", Format("select a from t where a is not null"));
    }

    [Fact]
    public void ParenthesisedPredicatesKeepTheirParentheses()
    {
        Assert.Equal(
            "SELECT a FROM t WHERE (a = 1 OR b IS NULL)",
            Format("select a from t where (a = 1 or b is null)"));
    }

    [Fact]
    public void NotIsEmitted()
    {
        Assert.Equal("SELECT a FROM t WHERE NOT a = 1", Format("select a from t where not a = 1"));
    }

    [Fact]
    public void InListFormats()
    {
        Assert.Equal("SELECT a FROM t WHERE a IN (1, 2, 3)", Format("select a from t where a in (1,2,3)"));
        Assert.Equal("SELECT a FROM t WHERE a NOT IN (1, 2)", Format("select a from t where a not in (1,2)"));
    }

    [Fact]
    public void ExistsSubqueryFormats()
    {
        Assert.Equal(
            "SELECT a FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.id = t.id)",
            Format("select a from t where exists (select 1 from u where u.id = t.id)", maxWidth: 120));
    }

    // --- GROUP BY / HAVING / ORDER BY -----------------------------------------------

    [Fact]
    public void GroupByAndHavingFormat()
    {
        Assert.Equal(
            """
            SELECT LedgerId, SUM(Amount) AS Total
            FROM t
            GROUP BY LedgerId
            HAVING SUM(Amount) <> 0
            """,
            Format("select LedgerId, SUM(Amount) as Total from t group by LedgerId having SUM(Amount) <> 0", maxWidth: 45));
    }

    [Fact]
    public void OrderByPreservesSortDirection()
    {
        Assert.Equal("SELECT a FROM t ORDER BY a DESC", Format("select a from t order by a desc"));
        Assert.Equal("SELECT a FROM t ORDER BY a ASC, b DESC", Format("select a from t order by a asc, b desc"));
        Assert.Equal("SELECT a FROM t ORDER BY a", Format("select a from t order by a"));
    }

    // --- expressions ----------------------------------------------------------------

    [Fact]
    public void FunctionCallsFormat()
    {
        Assert.Equal("SELECT ISNULL(a, 0) FROM t", Format("select ISNULL(a,0) from t"));
        Assert.Equal("SELECT GETDATE()", Format("select GETDATE()"));
        Assert.Equal("SELECT dbo.MyFunc(a, b) FROM t", Format("select dbo.MyFunc(a,b) from t"));
    }

    [Fact]
    public void AggregateWithDistinctFallsBackToPassthrough()
    {
        // COUNT(DISTINCT x) has a modifier this handler does not model, so it is emitted as
        // written rather than silently losing the DISTINCT.
        Assert.Equal("SELECT COUNT(DISTINCT a) FROM t", Format("select COUNT(DISTINCT a) from t"));
    }

    // --- window functions -------------------------------------------------------------

    [Fact]
    public void WindowFunctionFormats()
    {
        Assert.Equal(
            "SELECT ROW_NUMBER() OVER (ORDER BY a) FROM t",
            Format("select ROW_NUMBER() OVER (ORDER BY   a) from t"));
    }

    [Fact]
    public void PartitionAndOrderBothSurvive()
    {
        Assert.Equal(
            "SELECT ROW_NUMBER() OVER (PARTITION BY a, b ORDER BY c DESC) AS rn FROM t",
            Format("select ROW_NUMBER() OVER (PARTITION BY a,b ORDER BY c DESC) as rn from t"));
    }

    [Fact]
    public void WindowFrameSurvives()
    {
        // ROWS/RANGE framing is a separate node inside the OVER clause; dropping it changes which
        // rows the aggregate sees.
        Assert.Equal(
            "SELECT SUM(x) OVER (ORDER BY c ROWS UNBOUNDED PRECEDING) FROM t",
            Format("select SUM(x) OVER (ORDER BY c ROWS UNBOUNDED PRECEDING) from t"));
    }

    [Fact]
    public void AWindowTooWideToFitBreaksAtItsOwnClauseBoundaries()
    {
        // The window's clauses used to be joined by hard spaces, so the group around them had no
        // break of its own and the pressure fell through to the innermost group that did — the
        // ORDER BY list. That broke after `ORDER BY` and left the frame stranded on the next line:
        //
        //     SUM(s.Revenue) OVER (ORDER BY
        //         s.Revenue DESC ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS RunningTotal
        //
        // Breaking the outer construct first and leaving the inner ones flat is the right order.
        Assert.Equal(
            """
            SELECT
                s.RepName,
                SUM(s.Revenue) OVER (
                    ORDER BY s.Revenue DESC
                    ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                ) AS RunningTotal
            FROM sales s
            """,
            Format(
                "select s.RepName, sum(s.Revenue) over (order by s.Revenue desc "
                + "rows between unbounded preceding and current row) as RunningTotal from sales s"));
    }

    [Fact]
    public void AFrameIsNeverSplitAcrossLines()
    {
        // A frame is a fixed idiom read as one unit: splitting `BETWEEN … AND …` would make the AND
        // look like a boolean operator joining predicates. It stays whole even when that leaves the
        // line long.
        var formatted = Format(
            "select sum(x) over (partition by a order by b desc "
            + "rows between unbounded preceding and current row) as t from verylongtablename");

        Assert.Contains("ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void APartitionListStaysFlatWhenOnlyTheWindowNeedsToBreak()
    {
        // Its own group, so it is measured on its own. Without one, a two-column PARTITION BY split
        // across lines merely because the frame beside it was long.
        Assert.Equal(
            """
            SELECT
                SUM(s.Revenue) OVER (
                    PARTITION BY s.RegionId, s.Year
                    ORDER BY s.Revenue DESC
                    ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                ) AS RegionTotal
            FROM sales s
            """,
            Format(
                "select sum(s.Revenue) over (partition by s.RegionId, s.Year order by s.Revenue desc "
                + "rows between unbounded preceding and current row) as RegionTotal from sales s"));
    }

    [Fact]
    public void AWindowThatFitsStaysOnOneLine()
    {
        // The first choice is always no break at all; the expanded form is what a narrow line buys,
        // not the default.
        Assert.Equal(
            "SELECT ROW_NUMBER() OVER (PARTITION BY a ORDER BY b DESC) AS rn FROM t",
            Format("select row_number() over (partition by a order by b desc) as rn from t"));
    }

    [Fact]
    public void EmptyWindowIsPreserved()
    {
        // `OVER ()` is legal and means the whole result set — not the same thing as no OVER at all.
        Assert.Equal("SELECT COUNT(*) OVER () FROM t", Format("select COUNT(*) OVER () from t"));
    }

    [Fact]
    public void CountDistinctSurvives()
    {
        // DISTINCT inside a call is an enum on the node with no token range of its own, so it can only
        // come from the slice between the opening parenthesis and the first argument.
        Assert.Equal("SELECT COUNT(DISTINCT a) FROM t", Format("select COUNT(DISTINCT a) from t"));
    }

    [Fact]
    public void CountStarSurvives()
    {
        Assert.Equal("SELECT COUNT(*) FROM t", Format("select COUNT(*) from t"));
    }

    [Fact]
    public void NamedWindowFallsBackToPassthrough()
    {
        // `OVER w` refers to a WINDOW clause, which is a different shape and is not modelled.
        const string sql = "SELECT COUNT(*) OVER w FROM t WINDOW w AS (ORDER BY a)";
        Assert.Equal(sql, Format(sql));
    }

    // --- table-valued functions -------------------------------------------------------

    [Fact]
    public void TableValuedFunctionInFromFormats()
    {
        Assert.Equal("SELECT a FROM dbo.fn(1, 2) AS f", Format("select a from dbo.fn(1,2) as f"));
    }

    [Fact]
    public void TableValuedFunctionColumnRenameListSurvives()
    {
        // Dropping this list would silently change every column name the query then refers to.
        Assert.Equal(
            "SELECT c1 FROM dbo.fn(1) AS f (c1, c2)",
            Format("select c1 from dbo.fn(1) as f (c1,c2)"));
    }

    [Fact]
    public void TableValuedFunctionWithNoArgumentsFormats()
    {
        Assert.Equal("SELECT a FROM dbo.fn() AS f", Format("select a from dbo.fn(  ) as f"));
    }

    [Fact]
    public void ArithmeticFormats()
    {
        Assert.Equal("SELECT a + b * 2 FROM t", Format("select a+b*2 from t"));
        Assert.Equal("SELECT (a + b) * 2 FROM t", Format("select (a+b)*2 from t"));
    }

    [Fact]
    public void DoubledQuotesInsideLiteralsSurviveExactly()
    {
        // `''` is how a quote is escaped in T-SQL, and the doubling is part of the literal's text.
        // Touching any of it changes what the query means, so the token is emitted verbatim — and
        // the round-trip verifier compares literals exactly, which makes a mangled one a refusal
        // rather than silent corruption.
        Assert.Equal(
            "SELECT 'something it''s built for', N'unicode it''s', 'ends with quote''', '''starts', ''''",
            Format(
                "select 'something it''s built for', N'unicode it''s', 'ends with quote''', '''starts', ''''",
                maxWidth: 120));
    }

    [Fact]
    public void NestedEscapingInDynamicSqlIsLeftAlone()
    {
        // Four quotes deep, and the SQL inside the string is a string — not code to be formatted.
        // Recasing or re-laying-out what is inside it would change the literal's value.
        Assert.Equal(
            "SELECT N'select * from t where name = ''O''''Brien'''",
            Format("select N'select * from t where name = ''O''''Brien'''"));
    }

    [Fact]
    public void CommentMarkersInsideLiteralsAreNotComments()
    {
        Assert.Equal(
            "SELECT '-- not a comment', '/* nor this */', 'semicolon; inside', 'GO inside'",
            Format("select '-- not a comment', '/* nor this */', 'semicolon; inside', 'GO inside'"));
    }

    [Fact]
    public void ADoubledBracketInAQuotedIdentifierSurvives()
    {
        Assert.Equal(
            "SELECT [My]]Column] FROM [dbo].[Table]]Name]",
            Format("select [My]]Column] from [dbo].[Table]]Name]"));
    }

    [Fact]
    public void AMultiLineLiteralIsNeverReIndented()
    {
        // The newline and everything after it are inside the string, so indenting the continuation
        // lines to match the surrounding block would change the value. They stay at column 0 however
        // deeply the statement is nested — what the layout engine's literal line exists for.
        Assert.Equal(
            "BEGIN\n    SET @sql = N'line one it''s here\nline two must not be indented';\nEND",
            Format("begin set @sql = N'line one it''s here\nline two must not be indented'; end"));
    }

    [Fact]
    public void LiteralsAreNeverRewritten()
    {
        Assert.Equal(
            "SELECT N'unicode', 'ascii', 1.500, 0x00FF, 1e3",
            Format("select N'unicode', 'ascii', 1.500, 0x00FF, 1e3"));
    }

    [Fact]
    public void ShortCaseStaysInline()
    {
        Assert.Equal(
            "SELECT CASE WHEN a < 0 THEN 'C' ELSE 'D' END FROM t",
            Format("select case when a<0 then 'C' else 'D' end from t"));
    }

    [Fact]
    public void LongCaseBreaksPerWhen()
    {
        // One branch, so only the width forces this open — see MultiBranchCaseAlwaysBreaks for the
        // other reason. The distinction matters: with two branches this test would pass whatever
        // the width rule did, and would quietly stop testing it.
        Assert.Equal(
            """
            SELECT
                CASE
                    WHEN Total < 0 THEN 'CREDIT'
                    ELSE 'DEBIT'
                END AS Sign
            FROM t
            """,
            Format("select case when Total<0 then 'CREDIT' else 'DEBIT' end as Sign from t", maxWidth: 40));
    }

    [Fact]
    public void MultiBranchCaseAlwaysBreaks()
    {
        // Comfortably inside the width, and it breaks anyway. Two branches inline read as a run of
        // keywords to be scanned for the boundaries between them; stacked, they are a column to read
        // down, and adding a branch is a one-line diff rather than a reflow.
        Assert.Equal(
            """
            SELECT
                CASE
                    WHEN a < 0 THEN 'C'
                    WHEN a = 0 THEN 'Z'
                    ELSE 'D'
                END
            FROM t
            """,
            Format("select case when a<0 then 'C' when a=0 then 'Z' else 'D' end from t"));
    }

    [Fact]
    public void MultiBranchSimpleCaseBreaksAndKeepsItsInputOnTheCaseLine()
    {
        Assert.Equal(
            """
            SELECT
                CASE RegionId
                    WHEN 1 THEN 'North'
                    WHEN 2 THEN 'South'
                END
            FROM t
            """,
            Format("select case RegionId when 1 then 'North' when 2 then 'South' end from t"));
    }

    [Fact]
    public void SingleBranchCaseInsideAnAggregateStaysInline()
    {
        // The reason the rule is about branch count rather than "CASE always breaks": this idiom is
        // counting, not branching, and it is everywhere. Forcing it open would be a plain regression.
        Assert.Equal(
            "SELECT SUM(CASE WHEN o.Status = 'shipped' THEN 1 ELSE 0 END) AS Shipped FROM o",
            Format("select sum(case when o.Status='shipped' then 1 else 0 end) as Shipped from o"));
    }

    [Fact]
    public void SimpleCaseKeepsItsInputExpression()
    {
        Assert.Equal(
            "SELECT CASE a WHEN 1 THEN 'one' ELSE 'other' END FROM t",
            Format("select case a when 1 then 'one' else 'other' end from t"));
    }

    [Fact]
    public void ScalarSubqueryInSelectListFormats()
    {
        Assert.Equal(
            "SELECT a, (SELECT MAX(id) FROM u) AS m FROM t",
            Format("select a, (select MAX(id) from u) as m from t"));
    }

    [Fact]
    public void DerivedTableFormats()
    {
        Assert.Equal(
            """
            SELECT d.a
            FROM (
                SELECT a FROM u
            ) AS d
            """,
            Format("select d.a from (select a from u) as d", maxWidth: 30));
    }

    // --- set operations and CTEs ----------------------------------------------------

    [Fact]
    public void UnionAllFormatsWithOperatorOnItsOwnLine()
    {
        Assert.Equal(
            """
            SELECT a FROM t
            UNION ALL
            SELECT b FROM u
            """,
            Format("select a from t union all select b from u"));
    }

    [Fact]
    public void ExceptAndIntersectUseTheSourceKeyword()
    {
        Assert.Contains("\nEXCEPT\n", Format("select a from t except select b from u"), StringComparison.Ordinal);
        Assert.Contains("\nINTERSECT\n", Format("select a from t intersect select b from u"), StringComparison.Ordinal);
    }

    [Fact]
    public void SingleCteFormats()
    {
        Assert.Equal(
            """
            WITH Postings AS (
                SELECT LedgerId, Amount FROM dbo.Posting
            )
            SELECT LedgerId FROM Postings;
            """,
            Format("with Postings as (select LedgerId, Amount from dbo.Posting) select LedgerId from Postings;", maxWidth: 60));
    }

    [Fact]
    public void MultipleCtesEachGetTheirOwnBlock()
    {
        Assert.Equal(
            """
            WITH Postings AS (
                SELECT LedgerId FROM dbo.Posting
            ),
            Rollup AS (
                SELECT LedgerId FROM Postings
            )
            SELECT LedgerId FROM Rollup;
            """,
            Format(
                "with Postings as (select LedgerId from dbo.Posting), Rollup as (select LedgerId from Postings) select LedgerId from Rollup;",
                maxWidth: 60));
    }

    [Fact]
    public void CteWithExplicitColumnListFormats()
    {
        Assert.Equal(
            """
            WITH Postings (LedgerId, Total) AS (
                SELECT LedgerId, Amount FROM dbo.Posting
            )
            SELECT LedgerId FROM Postings;
            """,
            Format(
                "with Postings (LedgerId, Total) as (select LedgerId, Amount from dbo.Posting) select LedgerId from Postings;",
                maxWidth: 60));
    }

    // --- comments inside queries ----------------------------------------------------

    [Fact]
    public void TrailingCommentOnAColumnForcesTheListToBreak()
    {
        Assert.Equal(
            """
            SELECT
                a, -- alpha
                b
            FROM t
            """,
            Format("select a, -- alpha\n b from t", maxWidth: 200));
    }

    [Fact]
    public void CommentOnAPredicateSurvives()
    {
        Assert.Equal(
            """
            SELECT a
            FROM t
            WHERE alpha = 1 -- the important one
                AND beta = 2
            """,
            Format("select a from t where alpha = 1 -- the important one\n and beta = 2", maxWidth: 200));
    }

    [Fact]
    public void CommentInsideACteSurvives()
    {
        var result = Format("with c as (select a /* why */ from t) select a from c;", maxWidth: 60);
        Assert.Contains("/* why */", result, StringComparison.Ordinal);
    }

    // --- passthrough boundaries -----------------------------------------------------

    [Theory]
    [InlineData("SELECT a FROM t FOR XML PATH('r');")]
    public void UnmodelledConstructsPassThroughIntact(string sql)
    {
        // Each of these still contains a SELECT, but the construct as a whole is not modelled,
        // so the statement is emitted as written. Safe, and honest about what is supported.
        Assert.Equal(sql, Format(sql));
    }

    // --- SELECT ... INTO --------------------------------------------------------------

    [Fact]
    public void SelectIntoGetsItsOwnClauseLine()
    {
        // The INTO target hangs off the statement in the AST but is written inside the query,
        // between the select list and FROM, so the statement handler has to hand it down.
        Assert.Equal(
            """
            SELECT a, b
            INTO #tmp
            FROM dbo.t
            WHERE a > 0;
            """,
            Format("select a, b into #tmp from dbo.t where a > 0;", maxWidth: 20));
    }

    [Fact]
    public void ShortSelectIntoStaysOnOneLine()
    {
        Assert.Equal("SELECT a INTO #tmp FROM t;", Format("select a into #tmp from t;"));
    }

    [Fact]
    public void SelectIntoWithNoFromClauseFormats()
    {
        Assert.Equal("SELECT 1 AS x INTO #tmp;", Format("select 1 as x into #tmp;"));
    }

    [Fact]
    public void SelectIntoOverAUnionFormats()
    {
        // The INTO sits inside the *first* branch, so it is threaded down the left spine to the query
        // specification that actually contains it.
        Assert.Equal(
            """
            SELECT a INTO #tmp FROM t
            UNION ALL
            SELECT b FROM u;
            """,
            Format("select a into #tmp from t union all select b from u;"));
    }

    [Fact]
    public void SelectIntoOverAUnionDoesNotEmitItsTargetTwice()
    {
        // A QuerySpecification's range excludes its own INTO when nothing follows it, so `INTO #tmp`
        // lands in the gap the set operator is read from. Reading the operator from the first branch
        // alone produced `INTO #TODELETE UNION` — the target emitted a second time, and upper-cased,
        // because an operator slice is a keyword position. Uninstall.sql stopped parsing.
        var result = Format("SELECT 'x' AS n INTO #ToDelete UNION SELECT 'y' AS n;");

        Assert.Equal(1, result.Split("INTO", StringSplitOptions.None).Length - 1);
        Assert.Contains("#ToDelete", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectIntoIsThreadedThroughNestedSetOperators()
    {
        // Only the *leading* branch can hold the INTO, so the walk goes down the left spine however deep
        // it is. A parenthesised branch anywhere else is irrelevant to it.
        var result = Format("select a into #tmp from t union all select b from u union all (select c from v);");

        Assert.StartsWith("SELECT a INTO #tmp FROM t", result, StringComparison.Ordinal);
        Assert.Equal(1, result.Split("INTO", StringSplitOptions.None).Length - 1);
        Assert.Equal(result, Format(result));
    }

    // --- OPTION hints ----------------------------------------------------------------

    [Fact]
    public void OptionHintClauseGetsItsOwnLine()
    {
        // RECOMPILE, MAXDOP and the other hint names are non-reserved words that lex as
        // identifiers, so like NOLOCK and APPLY they keep whatever case the author used.
        Assert.Equal(
            "SELECT a FROM t\nOPTION (RECOMPILE);",
            Format("select a from t option (RECOMPILE);"));
    }

    [Fact]
    public void MaxdopHintKeepsItsKeyword()
    {
        // `MAXDOP 1` parses as a LiteralOptimizerHint whose token range covers only the `1`, so
        // rebuilding the clause from the hint nodes drops the word MAXDOP. Slicing the tokens is
        // the only emission that cannot.
        Assert.Equal(
            "SELECT a FROM t\nOPTION (RECOMPILE, MAXDOP 1);",
            Format("select a from t option (RECOMPILE, MAXDOP 1);"));
    }

    [Fact]
    public void HintClauseSurvivesOnInsertUpdateAndDelete()
    {
        // The same `OPTION (RECOMPILE)` guard was the leading cause of passthrough for INSERT,
        // UPDATE and SELECT alike — between them the top four ranks of the corpus histogram.
        Assert.Contains("OPTION (RECOMPILE)", Format("insert #t (a) select 1 from u option (RECOMPILE);"), StringComparison.Ordinal);
        Assert.Contains("OPTION (RECOMPILE)", Format("update t set a = 1 option (RECOMPILE);"), StringComparison.Ordinal);
        Assert.Contains("OPTION (RECOMPILE)", Format("delete from t where a = 1 option (RECOMPILE);"), StringComparison.Ordinal);
    }

    // --- CTEs on the other DML statements ---------------------------------------------

    [Fact]
    public void CteBeforeInsertFormatsBothParts()
    {
        // The statement's own range starts at the WITH, so the head slice that reconstructs
        // `INSERT` has to begin after the CTE list or it re-emits the whole prologue.
        Assert.Equal(
            """
            WITH c AS (
                SELECT 1 AS x
            )
            INSERT #t (a)
            SELECT x FROM c;
            """,
            Format("with c as (select 1 as x) insert #t (a) select x from c;"));
    }

    [Fact]
    public void CteBeforeUpdateFormatsBothParts()
    {
        Assert.StartsWith("WITH c AS (", Format("with c as (select 1 as x) update t set a = 1;"), StringComparison.Ordinal);
    }

    [Fact]
    public void CteBeforeDeleteFormatsBothParts()
    {
        Assert.StartsWith("WITH c AS (", Format("with c as (select 1 as x) delete from t;"), StringComparison.Ordinal);
    }

    [Fact]
    public void PivotFormats()
    {
        // Five of the six parts are nodes; `PIVOT (`, the aggregate's parentheses, `FOR`, `IN (` and the
        // two closing parentheses belong to none of them. The `)` that closes the aggregate is *inside*
        // the gap before `FOR`, and emitting one alongside the slice gave `SUM(x) ) FOR`.
        //
        // `sum` is recased because it is a single unquoted part, which is what proves it is not naming a
        // CLR user-defined aggregate — those must be schema-qualified. See PrintPivotAggregate, and
        // KeywordPositionTests for the qualified and bracketed cases that are still left alone.
        Assert.Equal(
            "SELECT * FROM t PIVOT (SUM(amount) FOR month IN ([Jan], [Feb])) AS p;",
            Format("select * from t pivot (sum(amount) for month in ([Jan], [Feb])) as p;"));
    }

    [Fact]
    public void PivotBreaksBeforeThePivotKeywordWhenItDoesNotFit()
    {
        Assert.Equal(
            """
            SELECT *
            FROM dbo.LedgerRollup AS src
            PIVOT (SUM(src.Amount) FOR src.Period IN ([Jan], [Feb])) AS p;
            """,
            Format(
                "select * from dbo.LedgerRollup as src pivot (SUM(src.Amount) for src.Period in ([Jan], [Feb])) as p;",
                maxWidth: 62));
    }

    [Fact]
    public void UnpivotFormatsWithOrWithoutAs()
    {
        Assert.Contains(
            "UNPIVOT (Revenue FOR OrderYear IN ([Y2025], [Y2026])) AS u",
            Format("select * from dbo.r as r unpivot (Revenue for OrderYear in ([Y2025], [Y2026])) as u;"),
            StringComparison.Ordinal);

        // The alias is mandatory but its `AS` is not, so the run before it is read rather than written.
        Assert.Contains(
            "([Y2025])) u",
            Format("select * from dbo.r as r unpivot (Revenue for OrderYear in ([Y2025])) u;"),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT SUM(x) OVER (ORDER BY b ROWS UNBOUNDED PRECEDING) FROM t;")]
    [InlineData("SELECT SUM(x) OVER (ORDER BY b ROWS BETWEEN 1 PRECEDING AND 2 FOLLOWING) FROM t;")]
    [InlineData("SELECT SUM(x) OVER (ORDER BY b RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) FROM t;")]
    [InlineData("SELECT SUM(x) OVER (PARTITION BY a ORDER BY b ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) FROM t;")]
    public void WindowFrameFormats(string sql)
    {
        // A running total is not exotica, and this was never once exercised by the fetched corpus. The
        // frame clause's range stops short of its own trailing `ROW` — `ROWS BETWEEN UNBOUNDED PRECEDING
        // AND CURRENT ROW` ends the range at `CURRENT` — so it is sliced to the clause's closing
        // parenthesis rather than Printed.
        Assert.Equal(sql, Format(sql, maxWidth: 200));
    }

    [Fact]
    public void WindowFrameKeywordsAreRecased()
    {
        Assert.Contains(
            "OVER (ORDER BY b ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)",
            Format("select sum(x) over (order by b rows between unbounded preceding and current row) from t;", maxWidth: 200),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT a FROM t WHERE a > ANY (SELECT b FROM u);", "> ANY (")]
    [InlineData("SELECT a FROM t WHERE a <> ALL (SELECT b FROM u);", "<> ALL (")]
    [InlineData("select a from t where a = some (select b from u);", "= SOME (")]
    public void SubqueryComparisonFormats(string sql, string expected)
    {
        // Both the operator and the quantifier are enums on the node, so the run between the operands is
        // provably grammar — which is what lets `some` be recased, since it lexes as an identifier.
        Assert.Contains(expected, Format(sql), StringComparison.Ordinal);
    }

    // --- deep nesting ---------------------------------------------------------------

    /// <summary>Stack the CLI gives its work; see <c>Program.WorkerStackBytes</c>.</summary>
    private const int WorkerStackBytes = 16 * 1024 * 1024;

    /// <summary>Runs <paramref name="body"/> with the stack the shipped binary runs on.</summary>
    /// <remarks>
    /// How deep ScriptDom can parse is a function of stack size, and a test host's thread stack is
    /// the host's choice: roomy on Linux, a fraction of that on macOS. That is not a hypothetical —
    /// the 400-deep case below passed on every platform but macOS, where CI aborted with a stack
    /// overflow inside the parser, before maxdop had an AST to be careful with.
    /// <para>A stack overflow is uncatchable in .NET, so a test cannot assert its way out of one. It
    /// has to ask for the stack the real program uses, which is what the CLI now does for exactly
    /// this reason — otherwise this asserts the host's generosity rather than maxdop's behaviour.</para>
    /// </remarks>
    private static void OnAWorkerStack(Action body)
    {
        ExceptionDispatchInfo? failure = null;

        var worker = new Thread(
            () =>
            {
                try
                {
                    body();
                }
                catch (Exception e)
                {
                    failure = ExceptionDispatchInfo.Capture(e);
                }
            },
            WorkerStackBytes);

        worker.Start();
        worker.Join();

        // Rethrown in place, so an assertion failure reads as itself rather than as a wrapper.
        failure?.Throw();
    }

    [Fact]
    public void VeryLongPredicateChainDoesNotCrash()
    {
        // Handler dispatch recurses over the AST, so a chain deep enough to overflow the stack
        // must degrade rather than abort the process — a crash cannot return the input untouched.
        OnAWorkerStack(() =>
        {
            var predicate = string.Join(" OR ", Enumerable.Range(0, 2_000).Select(i => $"a = {i}"));
            var result = SqlFormatter.Format($"SELECT x FROM t WHERE {predicate};");

            Assert.Equal(FormatStatus.Formatted, result.Status);
            Assert.Equal(2_000, result.Output.Split("a =").Length - 1);
        });
    }

    [Fact]
    public void DeeplyNestedSubqueriesDoNotCrash()
    {
        OnAWorkerStack(() =>
        {
            var sql = "SELECT 1";
            for (var i = 0; i < 400; i++)
            {
                sql = $"SELECT (SELECT x FROM ({sql}) AS d{i}) AS y";
            }

            var result = SqlFormatter.Format(sql + ";");

            // Whether it formats or bails, it must not crash and must not lose anything.
            Assert.NotEqual(FormatStatus.Refused, result.Status);
        });
    }

    // --- idempotency ----------------------------------------------------------------

    [Theory]
    [InlineData("select a, b from dbo.t where a = 1 and b = 2 order by a desc;")]
    [InlineData("select l.a from t as l inner join u on u.id = l.id left join v on v.id = l.id;")]
    [InlineData("with c as (select a from t) select a from c;")]
    [InlineData("select case when a < 0 then 'x' else 'y' end as s from t;")]
    [InlineData("select a from t union all select b from u;")]
    [InlineData("select a, -- note\n b from t;")]
    [InlineData("select COUNT(DISTINCT a) from t;")]
    [InlineData("select ISNULL(MAX(a), 0) from t where a in (1, 2, 3);")]
    public void FormattingIsIdempotent(string sql)
    {
        var once = Format(sql, maxWidth: 40);
        Assert.Equal(once, Format(once, maxWidth: 40));
    }

    [Fact]
    public void IdempotentAtManyWidths()
    {
        const string sql = """
            select l.LedgerId, l.Name, r.Total, case when r.Total < 0 then 'CREDIT' else 'DEBIT' end as Sign
            from dbo.Ledger as l
            inner join dbo.Rollup as r on r.LedgerId = l.LedgerId
            where l.Active = 1 and r.Total <> 0
            order by l.Name;
            """;

        for (var width = 20; width <= 140; width += 10)
        {
            var once = Format(sql, width);
            Assert.Equal(once, Format(once, width));
        }
    }
}
