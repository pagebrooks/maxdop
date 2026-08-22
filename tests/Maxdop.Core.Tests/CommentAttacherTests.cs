using Maxdop.Core.Comments;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Tests;

public class CommentAttacherTests
{
    private static (TSqlFragment Root, CommentMap Map) Parse(string sql)
    {
        var parser = new TSql180Parser(initialQuotedIdentifiers: false);
        using var reader = new StringReader(sql);
        var root = parser.Parse(reader, out var errors);
        Assert.Empty(errors);
        return (root, CommentAttacher.Attach(root));
    }

    private static CommentMap MapOf(string sql) => Parse(sql).Map;

    // --- the invariant that matters most ---------------------------------------------

    [Theory]
    [InlineData("-- only a comment on a statement\nSELECT 1;")]
    [InlineData("SELECT 1; -- trailing")]
    [InlineData("/* lead */ SELECT 1;")]
    [InlineData("SELECT /* mid */ 1;")]
    [InlineData("SELECT 1;\n-- after everything")]
    [InlineData("-- a\n-- b\nSELECT 1;\n-- c\n-- d")]
    [InlineData("SELECT a, b -- why\nFROM t;")]
    [InlineData("BEGIN\n-- not empty\nSELECT 1;\nEND")]
    [InlineData("SELECT 1\n/* multi\n   line\n   block */\n;")]
    [InlineData("-- header\nGO\n-- second\nSELECT 1;\nGO\n-- tail")]
    public void EveryCommentIsAttachedSomewhere(string sql)
    {
        var map = MapOf(sql);

        Assert.NotEmpty(map.All);
        Assert.Empty(map.Unattached);
    }

    [Fact]
    public void NoCommentIsAttachedTwice()
    {
        const string sql = """
            -- header
            CREATE PROCEDURE dbo.p AS
            BEGIN
                SELECT a  -- one
                     , b  /* two */
                FROM t;   -- three
            END
            GO
            /* tail */
            """;

        var (root, map) = Parse(sql);
        var seen = new List<int>();

        var collector = new AllFragments();
        root.Accept(collector);
        foreach (var node in collector.Fragments)
        {
            seen.AddRange(map.Leading(node).Select(c => c.TokenIndex));
            seen.AddRange(map.Trailing(node).Select(c => c.TokenIndex));
            seen.AddRange(map.Dangling(node).Select(c => c.TokenIndex));
        }

        Assert.Equal(map.All.Count, seen.Count);
        Assert.Equal(map.All.Count, seen.Distinct().Count());
        Assert.Equal(map.All.Select(c => c.TokenIndex).OrderBy(i => i), seen.OrderBy(i => i));
    }

    [Fact]
    public void EmptyInputProducesEmptyMap()
    {
        Assert.True(MapOf("").IsEmpty);
        Assert.True(MapOf("SELECT 1;").IsEmpty);
    }

    [Fact]
    public void AttachRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => CommentAttacher.Attach(null!));
    }

    // --- placement classification ---------------------------------------------------

    [Fact]
    public void CommentAloneOnItsLineIsOwnLine()
    {
        var map = MapOf("-- describes what follows\nSELECT 1;");
        Assert.Equal(CommentPlacement.OwnLine, map.All[0].Placement);
    }

    [Fact]
    public void CommentAfterCodeIsEndOfLine()
    {
        var map = MapOf("SELECT 1; -- annotates the select");
        Assert.Equal(CommentPlacement.EndOfLine, map.All[0].Placement);
    }

    [Fact]
    public void BlockCommentWithCodeOnBothSidesIsRemaining()
    {
        var map = MapOf("SELECT 1 /* between */ + 2;");
        Assert.Equal(CommentPlacement.Remaining, map.All[0].Placement);
    }

    [Fact]
    public void BlockCommentAtLineStartIsOwnLine_EvenWithCodeAfterIt()
    {
        // Nothing precedes it on its line, so it introduces what follows.
        var map = MapOf("/* introduces */ SELECT 1;");
        Assert.Equal(CommentPlacement.OwnLine, map.All[0].Placement);
    }

    [Fact]
    public void AloneOnLineDistinguishesTwoDifferentOwnLineCases()
    {
        // Both are OwnLine — neither has code to its left — but only the first may be followed
        // by a line break when re-emitted. Conflating them moves code onto the comment's line
        // or the comment onto its own, either of which reads as a moved comment.
        Assert.True(MapOf("/* alone */\nSELECT 1;").All[0].AloneOnLine);
        Assert.False(MapOf("/* introduces */ SELECT 1;").All[0].AloneOnLine);
    }

    [Fact]
    public void AloneOnLineIsFalseForAnnotatingComments()
    {
        Assert.False(MapOf("SELECT 1; -- annotates").All[0].AloneOnLine);
        Assert.False(MapOf("SELECT 1 /* between */ + 2;").All[0].AloneOnLine);
    }

    [Fact]
    public void LineCommentOnItsOwnLineIsAloneOnLine()
    {
        Assert.True(MapOf("-- alone\nSELECT 1;").All[0].AloneOnLine);
    }

    [Fact]
    public void LineCommentIsNeverRemaining()
    {
        // A `--` runs to end of line by definition, so nothing can share the line after it.
        var map = MapOf("SELECT 1 -- trailing\n, 2 -- another\nFROM t;");
        Assert.All(map.All, c => Assert.NotEqual(CommentPlacement.Remaining, c.Placement));
    }

    [Fact]
    public void CommentAfterMultiLineBlockCommentOnSameLineIsNotOwnLine()
    {
        // Regression guard: naively scanning back for "a whitespace token containing a newline"
        // finds the newline *inside* the block comment and wrongly reports own-line.
        var map = MapOf("SELECT 1 /* a\n   b */ -- tail\n, 2 FROM t;");

        var tail = map.All.Single(c => c.Text.Contains("tail", StringComparison.Ordinal));
        Assert.Equal(CommentPlacement.EndOfLine, tail.Placement);
    }

    // --- comment body ---------------------------------------------------------------

    [Fact]
    public void CommentTextIncludesItsDelimiters()
    {
        var map = MapOf("-- hello\nSELECT 1;");
        Assert.StartsWith("--", map.All[0].Text, StringComparison.Ordinal);
        Assert.Contains("hello", map.All[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockAndLineCommentsAreDistinguished()
    {
        var map = MapOf("/* block */\n-- line\nSELECT 1;");
        Assert.True(map.All[0].IsBlockComment);
        Assert.False(map.All[1].IsBlockComment);
    }

    [Fact]
    public void SwallowsRestOfLine_IsTrueForLineCommentsAndMultiLineBlocks()
    {
        var map = MapOf("SELECT 1 /* one line */ + 2; -- line\n/* multi\nline */\nSELECT 2;");

        var oneLineBlock = map.All.Single(c => c.Text.Contains("one line", StringComparison.Ordinal));
        var lineComment = map.All.Single(c => c.Text.Contains("-- line", StringComparison.Ordinal));
        var multiLineBlock = map.All.Single(c => c.Text.Contains("multi", StringComparison.Ordinal));

        Assert.False(oneLineBlock.SwallowsRestOfLine);
        Assert.True(lineComment.SwallowsRestOfLine);
        Assert.True(multiLineBlock.SwallowsRestOfLine);
    }

    [Fact]
    public void LineAndColumnAreReported()
    {
        var map = MapOf("SELECT 1;\n\n    -- indented on line 3\nSELECT 2;");
        Assert.Equal(3, map.All[0].Line);
        Assert.Equal(5, map.All[0].Column);
    }

    // --- blank lines ----------------------------------------------------------------

    [Fact]
    public void BlankLineBeforeIsDetected()
    {
        var map = MapOf("SELECT 1;\n\n-- separated\nSELECT 2;");
        Assert.True(map.All[0].BlankLineBefore);
    }

    [Fact]
    public void AdjacentLineMeansNoBlankLineBefore()
    {
        var map = MapOf("SELECT 1;\n-- adjacent\nSELECT 2;");
        Assert.False(map.All[0].BlankLineBefore);
    }

    [Fact]
    public void BlankLineAfterIsDetected()
    {
        var map = MapOf("SELECT 1;\n-- then a gap\n\nSELECT 2;");
        Assert.True(map.All[0].BlankLineAfter);
        Assert.False(MapOf("SELECT 1;\n-- no gap\nSELECT 2;").All[0].BlankLineAfter);
    }

    [Fact]
    public void StartAndEndOfFileAreNotBlankLines()
    {
        // Nothing above the first comment, so there is nothing to be separated from.
        Assert.False(MapOf("-- first thing\nSELECT 1;").All[0].BlankLineBefore);
        Assert.False(MapOf("SELECT 1;\n-- last thing").All[0].BlankLineAfter);
    }

    // --- attachment decisions -------------------------------------------------------

    [Fact]
    public void OwnLineCommentLeadsTheFollowingStatement()
    {
        const string sql = "SELECT 1;\n-- introduces the second\nSELECT 2;";
        var (root, map) = Parse(sql);
        var batch = ((TSqlScript)root).Batches[0];

        var second = batch.Statements[1];
        Assert.Single(map.Leading(second));
        Assert.Contains("introduces", map.Leading(second)[0].Text, StringComparison.Ordinal);
        Assert.Empty(map.Trailing(batch.Statements[0]));
    }

    [Fact]
    public void EndOfLineCommentTrailsThePrecedingStatement()
    {
        const string sql = "SELECT 1; -- annotates the first\nSELECT 2;";
        var (root, map) = Parse(sql);
        var batch = ((TSqlScript)root).Batches[0];

        Assert.Single(map.Trailing(batch.Statements[0]));
        Assert.Empty(map.Leading(batch.Statements[1]));
    }

    [Fact]
    public void FileHeaderCommentLeadsTheFirstBatch()
    {
        // The spike found these sit outside every batch's token range. They resolve because
        // TSqlScript's own range spans the whole file, making it the enclosing node.
        const string sql = "-- file header\n-- second line\nSET ANSI_NULLS ON\nGO\nSELECT 1;";
        var (root, map) = Parse(sql);
        var script = (TSqlScript)root;

        var leading = map.Leading(script.Batches[0]);
        Assert.Equal(2, leading.Count);
        Assert.Empty(map.Unattached);
    }

    [Fact]
    public void CommentOnTheSameLineAsGoTrailsTheBatchItTerminates()
    {
        // Without extending a batch's range over its GO, this comment would look like it
        // preceded the next batch and get relocated below the separator.
        const string sql = "SELECT 1;\nGO -- end of first batch\nSELECT 2;\nGO";
        var (root, map) = Parse(sql);
        var script = (TSqlScript)root;

        Assert.Single(map.Trailing(script.Batches[0]));
        Assert.Empty(map.Leading(script.Batches[1]));
    }

    [Fact]
    public void CommentOnItsOwnLineAfterGoLeadsTheNextBatch()
    {
        const string sql = "SELECT 1;\nGO\n-- describes the second batch\nSELECT 2;\nGO";
        var (root, map) = Parse(sql);
        var script = (TSqlScript)root;

        Assert.Empty(map.Trailing(script.Batches[0]));
        Assert.Single(map.Leading(script.Batches[1]));
    }

    [Fact]
    public void TrailingFileCommentAfterFinalGoAttachesToTheLastBatch()
    {
        const string sql = "SELECT 1;\nGO\n/* very last thing */";
        var (root, map) = Parse(sql);
        var script = (TSqlScript)root;

        Assert.Single(map.Trailing(script.Batches[^1]));
        Assert.Empty(map.Unattached);
    }

    [Fact]
    public void EmptyBlockIsNotValidTSql()
    {
        // Pinned as a fact about the language rather than left as an assumption: T-SQL has no
        // empty BEGIN…END, so that intuitive "comment with no sibling" case never arises.
        var parser = new TSql180Parser(initialQuotedIdentifiers: false);
        using var reader = new StringReader("BEGIN\n    -- deliberately empty\nEND");
        parser.Parse(reader, out var errors);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void CommentOnlyFileAttachesItsCommentsToTheScriptAsDangling()
    {
        // This is the real dangling case: the script parses cleanly to zero batches, so its
        // range spans the comments but it has no children to position them against. Whoever
        // renders TSqlScript must emit these, or a comment-only file would format to nothing.
        var (root, map) = Parse("-- just a note\n/* and another */");

        Assert.Equal(2, map.All.Count);
        Assert.Empty(map.Unattached);
        Assert.Empty(((TSqlScript)root).Batches);

        var dangling = map.Dangling(root);
        Assert.Equal(2, dangling.Count);
        Assert.Equal(["-- just a note", "/* and another */"], dangling.Select(c => c.Text.Trim()));
    }

    [Fact]
    public void NormalNodesHaveNoDanglingComments()
    {
        const string sql = "-- lead\nSELECT 1; -- trail\n";
        var (root, map) = Parse(sql);

        var collector = new AllFragments();
        root.Accept(collector);

        Assert.All(collector.Fragments, f => Assert.Empty(map.Dangling(f)));
        Assert.Empty(map.Unattached);
    }

    [Fact]
    public void CommentBetweenProcedureParametersAttachesToTheSecond()
    {
        const string sql = """
            CREATE PROCEDURE dbo.p
                @a INT,   -- first
                /* about the second */
                @b INT
            AS
            BEGIN
                SELECT 1;
            END
            """;

        var (root, map) = Parse(sql);
        var proc = (CreateProcedureStatement)((TSqlScript)root).Batches[0].Statements[0];

        Assert.Single(map.Trailing(proc.Parameters[0]));
        Assert.Contains("first", map.Trailing(proc.Parameters[0])[0].Text, StringComparison.Ordinal);

        Assert.Single(map.Leading(proc.Parameters[1]));
        Assert.Contains("second", map.Leading(proc.Parameters[1])[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CommentsOnSelectColumnsAttachPerColumn()
    {
        const string sql = """
            SELECT a  -- alpha
                 , b  -- beta
                 , c
            FROM t;
            """;

        var (root, map) = Parse(sql);
        var select = (SelectStatement)((TSqlScript)root).Batches[0].Statements[0];
        var query = (QuerySpecification)select.QueryExpression;

        Assert.Single(map.Trailing(query.SelectElements[0]));
        Assert.Single(map.Trailing(query.SelectElements[1]));
        Assert.Empty(map.Trailing(query.SelectElements[2]));
    }

    [Fact]
    public void MultipleCommentsOnOneNodeStayInSourceOrder()
    {
        const string sql = "-- one\n-- two\n-- three\nSELECT 1;";
        var (root, map) = Parse(sql);

        // At a batch boundary the comment attaches to the TSqlBatch, not to its first
        // statement: a batch and its sole statement share a token range, and the enclosing
        // node is TSqlScript, whose children are batches. Output is identical either way, so
        // long as the batch handler emits its leading comments.
        var batch = ((TSqlScript)root).Batches[0];
        var leading = map.Leading(batch);
        Assert.Equal(3, leading.Count);
        Assert.Equal(["-- one", "-- two", "-- three"], leading.Select(c => c.Text.Trim()));
        Assert.True(leading[0].TokenIndex < leading[1].TokenIndex);
        Assert.True(leading[1].TokenIndex < leading[2].TokenIndex);
    }

    // --- the real fixture -----------------------------------------------------------

    [Fact]
    public void GnarlyFixture_AttachesAllTwentyComments()
    {
        var sql = File.ReadAllText(TestFiles.Path("gnarly.sql"));
        var map = MapOf(sql);

        Assert.Equal(20, map.All.Count);
        Assert.Empty(map.Unattached);

        Assert.Contains(map.All, c => c.Placement == CommentPlacement.OwnLine);
        Assert.Contains(map.All, c => c.Placement == CommentPlacement.EndOfLine);

        // The fixture has no Remaining comment — every block comment in it is followed by a
        // newline. Remaining is covered by BlockCommentWithCodeOnBothSidesIsRemaining instead.
        Assert.DoesNotContain(map.All, c => c.Placement == CommentPlacement.Remaining);
    }

    [Fact]
    public void GnarlyFixture_EveryCommentAppearsExactlyOnce()
    {
        var sql = File.ReadAllText(TestFiles.Path("gnarly.sql"));
        var (root, map) = Parse(sql);

        var collector = new AllFragments();
        root.Accept(collector);

        var attached = collector.Fragments
            .SelectMany(f => map.Leading(f).Concat(map.Trailing(f)).Concat(map.Dangling(f)))
            .Select(c => c.TokenIndex)
            .ToList();

        Assert.Equal(20, attached.Count);
        Assert.Equal(20, attached.Distinct().Count());
    }

    private sealed class AllFragments : TSqlFragmentVisitor
    {
        internal List<TSqlFragment> Fragments { get; } = [];

        public override void Visit(TSqlFragment fragment) => Fragments.Add(fragment);
    }
}
