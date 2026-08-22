using Maxdop.Core.Comments;
using Maxdop.Core.Printing;
using Maxdop.Core.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Tests;

/// <summary>
/// Comment pass meets printer. These are the first tests that exercise the whole path —
/// parse, attach, build IR, lay out — and they pin the safety rule that makes trailing
/// comments survive: nothing may be emitted after an end-of-line comment on the same line.
/// </summary>
public class CommentDocsTests
{
    private static (TSqlFragment Root, CommentMap Map) Parse(string sql)
    {
        var parser = new TSql180Parser(initialQuotedIdentifiers: false);
        using var reader = new StringReader(sql);
        var root = parser.Parse(reader, out var errors);
        Assert.Empty(errors);
        return (root, CommentAttacher.Attach(root));
    }

    private static Comment SingleComment(string sql) => Parse(sql).Map.All.Single();

    private static string Print(Doc doc, int maxWidth = 80) =>
        DocPrinter.Print(doc, new PrintOptions { MaxWidth = maxWidth });

    // --- leading -------------------------------------------------------------------

    [Fact]
    public void LeadingLineComment_IsFollowedByAForcedBreak()
    {
        var comment = SingleComment("-- describes it\nSELECT 1;");
        var doc = Doc.Concat(CommentDocs.Leading(comment), Doc.Text("SELECT 1;"));

        Assert.Equal("-- describes it\nSELECT 1;", Print(doc, maxWidth: 1000));
    }

    [Fact]
    public void LeadingInlineBlockComment_StaysOnTheSameLine()
    {
        var comment = SingleComment("SELECT 1 /* why */ + 2;");
        var doc = Doc.Concat(CommentDocs.Leading(comment), Doc.Text("+ 2"));

        Assert.Equal("/* why */ + 2", Print(doc, maxWidth: 1000));
    }

    [Fact]
    public void LeadingMultiLineBlockComment_KeepsItsOwnShapeAndBreaksAfter()
    {
        var comment = SingleComment("/* one\n   two */\nSELECT 1;");
        var doc = Doc.Indent(Doc.Concat(Doc.HardLine, CommentDocs.Leading(comment), Doc.Text("SELECT 1;")));

        // The comment's interior alignment is preserved verbatim — re-flowing the inside of a
        // comment is not the formatter's business.
        Assert.Equal("\n    /* one\n   two */\n    SELECT 1;", Print(doc));
    }

    // --- trailing ------------------------------------------------------------------

    [Fact]
    public void TrailingLineComment_ForcesEnclosingGroupToBreak()
    {
        var comment = SingleComment("SELECT 1; -- note");
        var doc = Doc.Group(Doc.Concat(
            Doc.Text("a"),
            CommentDocs.Trailing(comment),
            Doc.Line,
            Doc.Text("b")));

        // Would fit on one line, but must not: `a -- note b` would comment out the `b`.
        Assert.Equal("a -- note\nb", Print(doc, maxWidth: 1000));
    }

    [Fact]
    public void TrailingInlineBlockComment_DoesNotForceABreak()
    {
        var comment = SingleComment("SELECT 1 /* fine */ + 2;");
        var doc = Doc.Group(Doc.Concat(
            Doc.Text("a"),
            CommentDocs.Trailing(comment),
            Doc.Line,
            Doc.Text("b")));

        // A block comment cannot swallow anything, so the group is free to stay flat.
        Assert.Equal("a /* fine */ b", Print(doc, maxWidth: 1000));
    }

    // THE safety property. A trailing comment is queued before the comma but must print after
    // it — otherwise the comma is inside the comment and the statement no longer parses.
    [Fact]
    public void TrailingLineComment_LetsPunctuationOvertakeIt()
    {
        var comment = SingleComment("SELECT 1 -- note\n, 2 FROM t;");
        var doc = Doc.Concat(
            Doc.Text("a"),
            CommentDocs.Trailing(comment),
            Doc.Text(","),
            Doc.HardLine,
            Doc.Text("b"));

        Assert.Equal("a, -- note\nb", Print(doc));
    }

    [Fact]
    public void MultipleTrailingComments_AllLandOnTheSameLine()
    {
        var (_, map) = Parse("SELECT 1 /* first */ /* second */ + 2;");
        var comments = map.All;
        Assert.Equal(2, comments.Count);

        var doc = Doc.Concat(Doc.Text("a"), CommentDocs.AllTrailing(comments), Doc.HardLine, Doc.Text("b"));
        Assert.Equal("a /* first */ /* second */\nb", Print(doc));
    }

    [Fact]
    public void AllLeading_EmitsInSourceOrder()
    {
        var (_, map) = Parse("-- one\n-- two\nSELECT 1;");
        var doc = Doc.Concat(CommentDocs.AllLeading(map.All), Doc.Text("SELECT 1;"));

        Assert.Equal("-- one\n-- two\nSELECT 1;", Print(doc));
    }

    [Fact]
    public void Dangling_EmitsEachOnItsOwnLine()
    {
        var (_, map) = Parse("-- only\n/* comments */");
        Assert.Equal("-- only\n/* comments */", Print(CommentDocs.Dangling(map.All)));
    }

    [Fact]
    public void EmptyCollectionsProduceNothing()
    {
        Assert.Equal("", Print(CommentDocs.AllLeading([])));
        Assert.Equal("", Print(CommentDocs.AllTrailing([])));
        Assert.Equal("", Print(CommentDocs.Dangling([])));
    }

    // --- the integration case ------------------------------------------------------

    /// <summary>
    /// A hand-rolled stand-in for the SELECT handler, wired to the real comment map. This is
    /// what the whole pipeline will do; doing it here proves the pieces fit before there are
    /// any node handlers to hide behind.
    /// </summary>
    private static string FormatSelectList(string sql, int maxWidth, bool leadingCommas = false)
    {
        var (root, map) = Parse(sql);
        var tokens = root.ScriptTokenStream;
        var select = (SelectStatement)((TSqlScript)root).Batches[0].Statements[0];
        var query = (QuerySpecification)select.QueryExpression;

        var items = query.SelectElements.Select(element => Doc.Concat(
            CommentDocs.AllLeading(map.Leading(element)),
            Doc.Text(SqlText.Slice(tokens, element.FirstTokenIndex, element.LastTokenIndex)),
            CommentDocs.AllTrailing(map.Trailing(element))));

        var separator = leadingCommas
            ? Doc.Concat(Doc.SoftLine, Doc.Text(", "))
            : Doc.Concat(Doc.Text(","), Doc.Line);

        var doc = Doc.Group(Doc.Concat(
            Doc.Text("SELECT"),
            Doc.Indent(Doc.Concat(Doc.Line, Doc.Join(separator, items)))));

        return Print(doc, maxWidth);
    }

    [Fact]
    public void SelectList_TrailingCommentKeepsCommaBeforeItAndForcesMultiLine()
    {
        // The exact behaviour Poor Man's T-SQL Formatter gets wrong. Note the input would fit
        // on one line at width 200 — the comment is what forces the break.
        var result = FormatSelectList(
            """
            SELECT a  -- alpha
                 , b
            FROM t;
            """,
            maxWidth: 200);

        Assert.Equal(
            """
            SELECT
                a, -- alpha
                b
            """,
            result);
    }

    [Fact]
    public void SelectList_CommentsOnEveryColumnSurviveIntact()
    {
        var result = FormatSelectList(
            """
            SELECT LedgerId   -- the key
                 , Amount     -- in cents
                 , PostedAt
            FROM t;
            """,
            maxWidth: 200);

        Assert.Equal(
            """
            SELECT
                LedgerId, -- the key
                Amount, -- in cents
                PostedAt
            """,
            result);
    }

    [Fact]
    public void SelectList_LeadingCommaStyleAlsoKeepsCommentsAtEndOfLine()
    {
        var result = FormatSelectList(
            """
            SELECT a  -- alpha
                 , b  -- beta
            FROM t;
            """,
            maxWidth: 200,
            leadingCommas: true);

        Assert.Equal(
            """
            SELECT
                a -- alpha
                , b -- beta
            """,
            result);
    }

    [Fact]
    public void SelectList_OwnLineCommentIntroducesTheColumnItPrecedes()
    {
        var result = FormatSelectList(
            """
            SELECT a
                 -- explains b
                 , b
            FROM t;
            """,
            maxWidth: 200);

        Assert.Equal(
            """
            SELECT
                a,
                -- explains b
                b
            """,
            result);
    }

    [Fact]
    public void SelectList_InlineBlockCommentStaysInlineAndAllowsOneLine()
    {
        var result = FormatSelectList("SELECT a /* hint */ + 1, b FROM t;", maxWidth: 200);

        Assert.Equal("SELECT a /* hint */ + 1, b", result);
    }

    // --- the passthrough primitive -------------------------------------------------

    [Fact]
    public void SqlText_RecoversAFragmentVerbatim()
    {
        const string sql = "SELECT   a  ,   b   FROM   dbo.t;";
        var (root, _) = Parse(sql);
        var statement = ((TSqlScript)root).Batches[0].Statements[0];

        // Original spacing intact, which is the point: passthrough must not normalise.
        // Note the range includes the terminating semicolon — relevant to the semicolon-policy
        // option, which cannot simply append one without checking whether it is already there.
        Assert.Equal("SELECT   a  ,   b   FROM   dbo.t;", SqlText.Of(statement));
    }

    [Fact]
    public void SqlText_ToleratesUnsetRanges()
    {
        var (root, _) = Parse("BEGIN SELECT 1; END");
        var tokens = root.ScriptTokenStream;

        Assert.Equal(string.Empty, SqlText.Slice(tokens, -1, -1));
        Assert.Equal(string.Empty, SqlText.Slice(tokens, 5, 2));
        Assert.Equal(string.Empty, SqlText.Slice(tokens, 99_999, 100_000));
    }

    [Fact]
    public void SqlText_OfWholeScriptReproducesTheInputExactly()
    {
        // The lossless property the spike measured, now asserted in the test suite so a
        // ScriptDom upgrade cannot regress it silently.
        var sql = TestFiles.Read("gnarly.sql");
        var (root, _) = Parse(sql);

        Assert.Equal(sql, SqlText.Slice(root.ScriptTokenStream, 0, root.ScriptTokenStream.Count - 1));
    }
}
