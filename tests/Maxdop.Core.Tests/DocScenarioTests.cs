using Maxdop.Core.Printing;

namespace Maxdop.Core.Tests;

/// <summary>
/// SQL-shaped scenarios built by hand, standing in for the node handlers that do not exist
/// yet. These exist to prove the IR can express everything the config surface
/// promises, and that each one really is "just an IR-level parameter" rather than a special
/// case inside the layout engine.
/// </summary>
public class DocScenarioTests
{
    private static string Print(Doc doc, int maxWidth) =>
        DocPrinter.Print(doc, new PrintOptions { MaxWidth = maxWidth });

    /// <summary>
    /// The comma-position option, expressed exactly as §4 describes it: separator placement
    /// relative to a soft line. Neither the printer nor this builder branches on width.
    /// </summary>
    private static Doc SelectList(IReadOnlyList<string> columns, bool leadingCommas)
    {
        var separator = leadingCommas
            ? Doc.Concat(Doc.SoftLine, Doc.Text(", "))
            : Doc.Concat(Doc.Text(","), Doc.Line);

        return Doc.Group(Doc.Concat(
            Doc.Text("SELECT"),
            Doc.Indent(Doc.Concat(Doc.Line, Doc.Join(separator, columns.Select(Doc.Text))))));
    }

    [Fact]
    public void SelectList_FitsOnOneLine_IdenticalUnderBothCommaStyles()
    {
        string[] columns = ["LedgerId", "Amount", "PostedAt"];

        Assert.Equal(
            "SELECT LedgerId, Amount, PostedAt",
            Print(SelectList(columns, leadingCommas: false), maxWidth: 80));

        Assert.Equal(
            "SELECT LedgerId, Amount, PostedAt",
            Print(SelectList(columns, leadingCommas: true), maxWidth: 80));
    }

    [Fact]
    public void SelectList_TrailingCommaStyle_WhenBroken()
    {
        string[] columns = ["LedgerId", "Amount", "PostedAt"];

        Assert.Equal(
            """
            SELECT
                LedgerId,
                Amount,
                PostedAt
            """,
            Print(SelectList(columns, leadingCommas: false), maxWidth: 20));
    }

    [Fact]
    public void SelectList_LeadingCommaStyle_WhenBroken()
    {
        string[] columns = ["LedgerId", "Amount", "PostedAt"];

        Assert.Equal(
            """
            SELECT
                LedgerId
                , Amount
                , PostedAt
            """,
            Print(SelectList(columns, leadingCommas: true), maxWidth: 20));
    }

    [Theory]
    [InlineData(80, 1)]
    [InlineData(20, 4)]
    public void MaxWidth_IsTheOnlyThingDecidingLineCount(int maxWidth, int expectedLines)
    {
        string[] columns = ["LedgerId", "Amount", "PostedAt"];
        var result = Print(SelectList(columns, leadingCommas: false), maxWidth);
        Assert.Equal(expectedLines, result.Split('\n').Length);
    }

    [Fact]
    public void TrailingComment_StaysAtEndOfLine_AndForcesTheConstructToBreak()
    {
        // What the comment-attachment pass will emit for `Amount -- running total`.
        // The BreakParent is essential: without it the group would fit on one line and the
        // `--` would comment out everything after it.
        var doc = Doc.Group(Doc.Concat(
            Doc.Text("SELECT"),
            Doc.Indent(Doc.Concat(
                Doc.Line,
                Doc.Text("LedgerId"),
                Doc.Text(","),
                Doc.Line,
                Doc.Text("Amount"),
                Doc.LineSuffix(Doc.Text(" -- running total")),
                Doc.BreakParent,
                Doc.Text(","),
                Doc.Line,
                Doc.Text("PostedAt")))));

        Assert.Equal(
            """
            SELECT
                LedgerId,
                Amount, -- running total
                PostedAt
            """,
            Print(doc, maxWidth: 200));
    }

    [Fact]
    public void UnhandledConstruct_PassesThroughVerbatimInsideFormattedOutput()
    {
        // The graceful-passthrough invariant at IR level: a construct with no handler is
        // emitted exactly as it appeared, keeping its own indentation, while everything
        // around it still formats normally.
        var unhandled = "CREATE FULLTEXT INDEX ON dbo.Doc(Body)\n    KEY INDEX PK_Doc\n    WITH CHANGE_TRACKING AUTO";

        var doc = Doc.Concat(
            Doc.Text("SELECT 1;"),
            Doc.HardLine,
            Doc.Verbatim(unhandled),
            Doc.HardLine,
            Doc.Text("SELECT 2;"));

        Assert.Equal(
            $"SELECT 1;\n{unhandled}\nSELECT 2;",
            Print(doc, maxWidth: 20));
    }

    [Fact]
    public void BlankLineBetweenBatches_IsJustTwoHardLines()
    {
        var batch = (string text) => Doc.Concat(Doc.Text(text), Doc.HardLine, Doc.Text("GO"));

        var doc = Doc.Join(
            Doc.Concat(Doc.HardLine, Doc.HardLine),
            new[] { batch("SET ANSI_NULLS ON"), batch("SET QUOTED_IDENTIFIER ON") });

        Assert.Equal(
            """
            SET ANSI_NULLS ON
            GO

            SET QUOTED_IDENTIFIER ON
            GO
            """,
            Print(doc, maxWidth: 80));
    }

    [Fact]
    public void NestedSubquery_KeepsInnerSelectFlatWhenItFits()
    {
        // The outer list is too wide and breaks, but the subquery still fits its own line.
        var subquery = Doc.Group(Doc.Concat(
            Doc.Text("(SELECT"),
            Doc.Indent(Doc.Concat(Doc.Line, Doc.Text("MAX(Id)"), Doc.Line, Doc.Text("FROM dbo.T"))),
            Doc.SoftLine,
            Doc.Text(")")));

        var doc = Doc.Group(Doc.Concat(
            Doc.Text("SELECT"),
            Doc.Indent(Doc.Concat(
                Doc.Line,
                Doc.Join(
                    Doc.Concat(Doc.Text(","), Doc.Line),
                    new[] { Doc.Text("LedgerIdentifier"), Doc.Text("AmountInCents"), subquery })))));

        Assert.Equal(
            """
            SELECT
                LedgerIdentifier,
                AmountInCents,
                (SELECT MAX(Id) FROM dbo.T)
            """,
            Print(doc, maxWidth: 40));
    }

    [Fact]
    public void AsAlignment_IsExpressibleWithAlign()
    {
        // §4's "AS alignment" option: continuation lines line up under a specific column
        // rather than at an indent multiple.
        var doc = Doc.Concat(
            Doc.Text("SELECT "),
            Doc.Align(7, Doc.Join(
                Doc.Concat(Doc.Text(","), Doc.HardLine),
                new[] { Doc.Text("a AS Alpha"), Doc.Text("b AS Beta"), Doc.Text("c AS Gamma") })));

        Assert.Equal(
            """
            SELECT a AS Alpha,
                   b AS Beta,
                   c AS Gamma
            """,
            Print(doc, maxWidth: 80));
    }

    [Fact]
    public void CrlfInput_ProducesCrlfOutputThroughout()
    {
        // Encoding invariants (§3) start here: the terminator is a print option, so a CRLF
        // file never silently becomes LF.
        var doc = Doc.Group(Doc.Concat(
            Doc.Text("SELECT"),
            Doc.Indent(Doc.Concat(Doc.Line, Doc.Text("a"), Doc.Text(","), Doc.Line, Doc.Text("b")))));

        var result = DocPrinter.Print(doc, new PrintOptions { MaxWidth = 5, NewLine = "\r\n" });

        Assert.Equal("SELECT\r\n    a,\r\n    b", result);
        Assert.DoesNotContain("\n\n", result, StringComparison.Ordinal);
    }
}
