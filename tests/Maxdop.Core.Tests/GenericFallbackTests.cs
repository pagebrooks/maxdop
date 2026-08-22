using Maxdop.Core.Formatting;
using Maxdop.Core.Printing;

namespace Maxdop.Core.Tests;

/// <summary>
/// The generic fallback: constructs with no hand-written handler, formatted by discovering their
/// immediate children at runtime.
/// </summary>
/// <remarks>
/// T-SQL's administrative surface is several hundred statement types shaped alike, and hand-writing a
/// handler apiece is neither feasible nor useful when the layout decision is the same every time. The
/// fallback took combined corpus coverage from 90.1% to <b>94.6%</b> and the per-file median from 10.1%
/// to <b>85.7%</b>, with the three safety invariants unchanged: every token still ends up either inside a
/// printed child or inside a slice.
/// <para>What it buys is normalised spacing and <em>descent</em> — an expression or query embedded in an
/// administrative statement now gets formatted. What it deliberately does not do is apply keyword
/// casing; see <see cref="GenericConstructsKeepTheirCasing"/>.</para>
/// </remarks>
public class GenericFallbackTests
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

    [Theory]
    [InlineData("GRANT SELECT ON dbo.t TO [role];")]
    [InlineData("REVOKE SELECT ON dbo.t FROM [role];")]
    [InlineData("ALTER INDEX ix ON dbo.t REBUILD;")]
    [InlineData("ALTER INDEX ALL ON dbo.t DISABLE;")]
    [InlineData("BACKUP DATABASE d TO DISK = 'x.bak' WITH COMPRESSION;")]
    [InlineData("RESTORE DATABASE d FROM DISK = 'x.bak' WITH REPLACE;")]
    [InlineData("CREATE DATABASE MyDb;")]
    [InlineData("ALTER DATABASE MyDb SET RECOVERY SIMPLE;")]
    [InlineData("ALTER TABLE dbo.t SET (LOCK_ESCALATION = AUTO);")]
    [InlineData("ALTER TABLE dbo.t NOCHECK CONSTRAINT ALL;")]
    [InlineData("DROP INDEX ix ON dbo.t;")]
    [InlineData("USE MyDb;")]
    [InlineData("CREATE STATISTICS st ON dbo.t (a, b) WITH FULLSCAN;")]
    public void AdministrativeStatementsSurviveIntactAndAreStable(string sql)
    {
        // Written the way a script writes them, so the expected output is the input: the fallback
        // normalises spacing but invents nothing. The real assertion is that these no longer go through
        // passthrough — which the round-trip and idempotency checks inside Format already prove.
        var once = Format(sql);
        Assert.Equal(once, Format(once));
        Assert.Contains(sql.Split(' ')[0], once, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericConstructsKeepTheirCasing()
    {
        // The one thing the fallback will not do. Recasing only the *reserved* words in a statement whose
        // vocabulary is unknown produced `GRANT select on dbo.t TO [role]` — half-applied casing that
        // reads as a bug. Claiming the non-reserved words as keywords instead would be unsafe: this
        // codebase has already established that absence of a child node does not imply absence of a name
        // (see KeywordPositionTests), and a generic emitter cannot prove the difference. So a construct
        // the printer does not model keeps the author's casing exactly.
        Assert.Equal("grant select on dbo.t to [role];", Format("grant select on dbo.t to [role];"));
        Assert.Equal("GRANT SELECT ON dbo.t TO [role];", Format("GRANT SELECT ON dbo.t TO [role];"));
    }

    [Fact]
    public void SpacingIsNormalisedWithinAGenericStatement()
    {
        // Normalising the *whole* statement needs the fallback to reach the option and clause nodes under
        // it, not only the statement itself. Limited to statements, `DROP INDEX ix   ON   dbo.t` kept its
        // original spacing from the DropIndexClause onward — verbatim in the middle of a formatted line.
        Assert.Equal("DROP INDEX ix ON dbo.t;", Format("DROP    INDEX   ix   ON   dbo.t;"));
    }

    [Fact]
    public void EmbeddedQueriesAndExpressionsAreStillFormatted()
    {
        // The point of the fallback over passthrough: it descends. A filtered index's predicate is a real
        // expression and gets the expression handlers.
        Assert.Contains(
            "WHERE a > 0",
            Format("create index ix on dbo.t (a) where a>0;"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ChildrenWithIdenticalRangesAreNotEmittedTwice()
    {
        // A single-identifier SchemaObjectName has exactly its Identifier's range, and `Contains` is
        // strictly-contains by design, so both survived the maximal-range filter: `ALTER INDEX ALL ON t1`
        // came out as `ON t1 t1` and sixty-nine corpus files stopped parsing. Nodes sharing a range are
        // now collapsed to the outermost.
        Assert.Equal("ALTER INDEX ALL ON t1 REBUILD;", Format("ALTER INDEX ALL ON t1 REBUILD;"));
        Assert.DoesNotContain("t1 t1", Format("ALTER INDEX ALL ON t1 REBUILD;"), StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralEndOfLineCommentsDoNotCollapseOntoOneLine()
    {
        // Each `--` comment is deferred through LineSuffix, so putting four of them on one output line
        // meant the first swallowed the other three: five comments in, two out. A line-suffix boundary at
        // each part gap forces a break when, and only when, a suffix is pending.
        var result = Format(
            "alter table t1 drop\ncs3, -- first\ncolumn c2, -- second\nconstraint cs1, -- third\ncolumn c4; -- fourth");

        foreach (var expected in new[] { "first", "second", "third", "fourth" })
        {
            Assert.Contains(expected, result, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CommentInAGenericTailStillDeclines()
    {
        // A comment after the last child has no following node to attach to, and slices strip comments,
        // so it would be lost. Passthrough keeps it.
        const string sql = "ALTER INDEX ix ON dbo.t REBUILD /* why */;";
        Assert.Contains("why", Format(sql), StringComparison.Ordinal);
    }

    [Fact]
    public void ChildListedOutsideItsParentsRangeIsNotEmittedTwice()
    {
        // `FileGroupDefinition` covers `FILEGROUP g(…)` but its FileDeclarations list also holds the
        // `, (…)` that follows, which its own range stops short of. So the statement legitimately sees
        // that declaration as a sibling — it is not contained in the filegroup's range — and unclamped,
        // both emitted it. Children are now clamped to the parent's own range.
        var result = Format(
            "CREATE DATABASE Sales ON PRIMARY(NAME = a, FILENAME = 'x'),"
            + " FILEGROUP g1(NAME = c, FILENAME = 'z'), (NAME = d, FILENAME = 'w');");

        Assert.Equal(1, result.Split("NAME = d", StringSplitOptions.None).Length - 1);
        Assert.Contains("FILEGROUP g1", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AddressesWhoseSpacingChangesHowTheyLexAreLeftAlone()
    {
        // An IPv4 address may be written `1.2 .3 . 4`, which lexes as Numeric("1.2"), Numeric(".3"),
        // Dot, Integer("4") — the token boundaries depend on the spaces. Re-emitting the parts with one
        // space each regroups the characters and the text lexes into different tokens: the verifier
        // reported `expected Dot ".", got Numeric ".4"`. Same family as the rule that keeps `- -1` from
        // becoming `--1`, a line comment.
        const string sql = "create endpoint e1 state = stopped as tcp(listener_ip = (1.2 .3 . 4)) for tsql()";
        Assert.Equal(sql, Format(sql));
    }

    [Fact]
    public void TableTypeUsesTheSameLayoutAsATable()
    {
        Assert.Equal(
            """
            CREATE TYPE dbo.tt AS TABLE (
                a INT NOT NULL,
                b NVARCHAR(50)
            );
            """,
            Format("create type dbo.tt as table (a int not null, b nvarchar(50));"));
    }
}
