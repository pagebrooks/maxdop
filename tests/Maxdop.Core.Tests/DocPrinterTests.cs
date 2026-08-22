using Maxdop.Core.Printing;

namespace Maxdop.Core.Tests;

/// <summary>
/// Unit tests for the generic layout engine. Nothing here mentions SQL — these pin the
/// Wadler/Prettier semantics the node handlers will be written against.
/// </summary>
public class DocPrinterTests
{
    private static string Print(Doc doc, int maxWidth = 80) =>
        DocPrinter.Print(doc, new PrintOptions { MaxWidth = maxWidth });

    [Fact]
    public void Text_IsEmittedVerbatim()
    {
        Assert.Equal("SELECT", Print(Doc.Text("SELECT")));
    }

    [Fact]
    public void Empty_EmitsNothing()
    {
        Assert.Equal("", Print(Doc.Empty));
        Assert.Equal("", Print(Doc.Text("")));
    }

    [Fact]
    public void Concat_JoinsPartsInOrder()
    {
        Assert.Equal("abc", Print(Doc.Concat(Doc.Text("a"), Doc.Text("b"), Doc.Text("c"))));
    }

    [Fact]
    public void Join_InterposesSeparator()
    {
        var doc = Doc.Join(Doc.Text("."), new[] { Doc.Text("dbo"), Doc.Text("Table") });
        Assert.Equal("dbo.Table", Print(doc));
    }

    // --- group: the one place a layout decision happens -------------------------------

    [Fact]
    public void Group_ThatFits_CollapsesLineToSpace()
    {
        var doc = Doc.Group(Doc.Concat(Doc.Text("a"), Doc.Line, Doc.Text("b")));
        Assert.Equal("a b", Print(doc));
    }

    [Fact]
    public void Group_ThatFits_CollapsesSoftLineToNothing()
    {
        var doc = Doc.Group(Doc.Concat(Doc.Text("a"), Doc.SoftLine, Doc.Text("b")));
        Assert.Equal("ab", Print(doc));
    }

    [Fact]
    public void Group_ThatDoesNotFit_TurnsEveryLineIntoNewline()
    {
        var doc = Doc.Group(Doc.Concat(Doc.Text("aaa"), Doc.Line, Doc.Text("bbb")));
        Assert.Equal("aaa\nbbb", Print(doc, maxWidth: 5));
    }

    [Fact]
    public void Group_WithShouldBreak_BreaksWithoutMeasuring()
    {
        var doc = Doc.Group(Doc.Concat(Doc.Text("a"), Doc.Line, Doc.Text("b")), shouldBreak: true);
        Assert.Equal("a\nb", Print(doc));
    }

    // The defining property of the algorithm: breaking is decided per group, so an inner
    // group that fits stays on one line even when its parent had to break.
    [Fact]
    public void InnerGroupStaysFlat_WhenOuterGroupBreaks()
    {
        var inner = Doc.Group(Doc.Concat(Doc.Text("i"), Doc.Line, Doc.Text("j")));
        var doc = Doc.Group(Doc.Concat(Doc.Text("outer"), Doc.HardLine, inner));
        Assert.Equal("outer\ni j", Print(doc));
    }

    // --- indentation -----------------------------------------------------------------

    [Fact]
    public void Indent_AppliesToNewlinesInside()
    {
        var doc = Doc.Group(Doc.Concat(
            Doc.Text("("),
            Doc.Indent(Doc.Concat(Doc.SoftLine, Doc.Text("x"))),
            Doc.SoftLine,
            Doc.Text(")")));

        Assert.Equal("(\n    x\n)", Print(doc, maxWidth: 2));
    }

    [Fact]
    public void Indent_CostsNothingWhenGroupIsFlat()
    {
        var doc = Doc.Group(Doc.Concat(
            Doc.Text("("),
            Doc.Indent(Doc.Concat(Doc.SoftLine, Doc.Text("x"))),
            Doc.SoftLine,
            Doc.Text(")")));

        Assert.Equal("(x)", Print(doc));
    }

    [Fact]
    public void Indent_Nests()
    {
        var doc = Doc.Indent(Doc.Concat(
            Doc.HardLine,
            Doc.Text("one"),
            Doc.Indent(Doc.Concat(Doc.HardLine, Doc.Text("two")))));

        Assert.Equal("\n    one\n        two", Print(doc));
    }

    [Fact]
    public void Align_AddsExactColumnCount()
    {
        var doc = Doc.Align(2, Doc.Concat(Doc.HardLine, Doc.Text("x")));
        Assert.Equal("\n  x", Print(doc));
    }

    [Fact]
    public void Align_WithZeroWidth_IsIdentity()
    {
        var doc = Doc.Align(0, Doc.Concat(Doc.HardLine, Doc.Text("x")));
        Assert.Equal("\nx", Print(doc));
    }

    // --- hard lines and break propagation --------------------------------------------

    [Fact]
    public void HardLine_ForcesEnclosingGroupToBreak_EvenWhenItWouldFit()
    {
        var doc = Doc.Group(Doc.Concat(Doc.Text("a"), Doc.HardLine, Doc.Text("b")));
        Assert.Equal("a\nb", Print(doc, maxWidth: 1000));
    }

    [Fact]
    public void HardLine_PropagatesThroughEveryEnclosingGroup()
    {
        // The hard line is three groups deep; all three must break, so the Line in the
        // outermost group becomes a newline rather than a space.
        var doc = Doc.Group(Doc.Concat(
            Doc.Text("L1"),
            Doc.Line,
            Doc.Group(Doc.Concat(
                Doc.Text("L2"),
                Doc.Line,
                Doc.Group(Doc.Concat(Doc.Text("L3"), Doc.HardLine, Doc.Text("L4")))))));

        Assert.Equal("L1\nL2\nL3\nL4", Print(doc, maxWidth: 1000));
    }

    [Fact]
    public void BreakParent_ForcesBreakWithoutEmittingAnything()
    {
        var doc = Doc.Group(Doc.Concat(Doc.Text("a"), Doc.Line, Doc.Text("b"), Doc.BreakParent));
        Assert.Equal("a\nb", Print(doc, maxWidth: 1000));
    }

    [Fact]
    public void BreakParent_StopsAtNearestGroup_LeavingOuterGroupFree()
    {
        // Only the inner group is forced; the outer one is still measured and fits.
        var inner = Doc.Group(Doc.Concat(Doc.Text("i"), Doc.Line, Doc.Text("j"), Doc.BreakParent));
        var doc = Doc.Concat(Doc.Text("head "), inner);
        Assert.Equal("head i\nj", Print(doc, maxWidth: 1000));
    }

    // --- ifBreak / indentIfBreak ------------------------------------------------------

    [Fact]
    public void IfBreak_WithoutGroupId_FollowsNearestEnclosingGroup()
    {
        var doc = Doc.Group(Doc.Concat(
            Doc.Text("aaa"),
            Doc.Line,
            Doc.Text("bbb"),
            Doc.IfBreak(Doc.Text("<broke>"), Doc.Text("<flat>"))));

        Assert.Equal("aaa bbb<flat>", Print(doc, maxWidth: 1000));
        Assert.Equal("aaa\nbbb<broke>", Print(doc, maxWidth: 5));
    }

    [Fact]
    public void IfBreak_WithGroupId_FollowsThatGroupNotItsOwn()
    {
        var head = new GroupId("head");
        var doc = Doc.Concat(
            Doc.Group(Doc.Concat(Doc.Text("aaaa"), Doc.Line, Doc.Text("bbbb")), id: head),
            Doc.Text("|"),
            Doc.Group(Doc.IfBreak(Doc.Text("BROKE"), Doc.Text("FLAT"), head)));

        Assert.Equal("aaaa bbbb|FLAT", Print(doc, maxWidth: 1000));
        Assert.Equal("aaaa\nbbbb|BROKE", Print(doc, maxWidth: 5));
    }

    [Fact]
    public void IfBreak_OmittedFlatBranch_DefaultsToEmpty()
    {
        var doc = Doc.Group(Doc.Concat(Doc.Text("statement"), Doc.Line, Doc.IfBreak(Doc.Text(";"))));

        // Flat: the semicolon vanishes, but the Line still contributes its space.
        Assert.Equal("statement ", Print(doc, maxWidth: 1000));
        Assert.Equal("statement\n;", Print(doc, maxWidth: 5));
    }

    [Fact]
    public void IndentIfBreak_IndentsOnlyWhenReferencedGroupBroke()
    {
        var head = new GroupId("head");
        var doc = Doc.Concat(
            Doc.Group(Doc.Concat(Doc.Text("head"), Doc.Line, Doc.Text("tail")), id: head),
            Doc.IndentIfBreak(Doc.Concat(Doc.HardLine, Doc.Text("cont")), head));

        Assert.Equal("head tail\ncont", Print(doc, maxWidth: 1000));
        Assert.Equal("head\ntail\n    cont", Print(doc, maxWidth: 4));
    }

    // --- line suffix: the mechanism trailing comments depend on -----------------------

    [Fact]
    public void LineSuffix_DefersContentToEndOfLine()
    {
        var doc = Doc.Concat(
            Doc.Text("SELECT 1"),
            Doc.LineSuffix(Doc.Text(" -- note")),
            Doc.HardLine,
            Doc.Text("GO"));

        Assert.Equal("SELECT 1 -- note\nGO", Print(doc));
    }

    [Fact]
    public void LineSuffix_JumpsOverContentPrintedAfterIt()
    {
        // This is the whole point: the comment was queued before the comma, but must still
        // land after it, because "-- note," would comment out the comma.
        var doc = Doc.Concat(
            Doc.Text("a"),
            Doc.LineSuffix(Doc.Text(" -- note")),
            Doc.Text(","),
            Doc.HardLine,
            Doc.Text("b"));

        Assert.Equal("a, -- note\nb", Print(doc));
    }

    [Fact]
    public void LineSuffix_IsFlushedAtEndOfDocument()
    {
        var doc = Doc.Concat(Doc.Text("x"), Doc.LineSuffix(Doc.Text(" -- trailing")));
        Assert.Equal("x -- trailing", Print(doc));
    }

    [Fact]
    public void LineSuffixBoundary_ForcesNewlineWhenSuffixPending()
    {
        var doc = Doc.Concat(
            Doc.Text("a"),
            Doc.LineSuffix(Doc.Text(" --c")),
            Doc.LineSuffixBoundary,
            Doc.Text("b"));

        Assert.Equal("a --c\nb", Print(doc));
    }

    [Fact]
    public void LineSuffixBoundary_DoesNothingWhenNoSuffixPending()
    {
        var doc = Doc.Concat(Doc.Text("a"), Doc.LineSuffixBoundary, Doc.Text("b"));
        Assert.Equal("ab", Print(doc));
    }

    // --- verbatim / literal lines: the passthrough invariant --------------------------

    [Fact]
    public void LiteralLine_EmitsNewlineWithNoIndentation()
    {
        var doc = Doc.Indent(Doc.Concat(
            Doc.HardLine, Doc.Text("x"),
            Doc.LiteralLine, Doc.Text("y")));

        Assert.Equal("\n    x\ny", Print(doc));
    }

    [Fact]
    public void Verbatim_PreservesOriginalShapeEvenWhenIndented()
    {
        var doc = Doc.Indent(Doc.Concat(Doc.HardLine, Doc.Verbatim("line1\n  line2\nline3")));
        Assert.Equal("\n    line1\n  line2\nline3", Print(doc));
    }

    [Fact]
    public void Verbatim_NormalisesCrlfInputToConfiguredNewline()
    {
        var doc = Doc.Verbatim("a\r\nb");
        Assert.Equal("a\nb", DocPrinter.Print(doc, new PrintOptions { NewLine = "\n" }));
        Assert.Equal("a\r\nb", DocPrinter.Print(doc, new PrintOptions { NewLine = "\r\n" }));
    }

    [Fact]
    public void Verbatim_SingleLineIsPlainText()
    {
        Assert.Equal("just text", Print(Doc.Verbatim("just text")));
    }

    // --- whitespace hygiene ----------------------------------------------------------

    [Fact]
    public void TrailingBlanks_AreTrimmedBeforeANewline()
    {
        var doc = Doc.Concat(Doc.Text("a"), Doc.Text("   "), Doc.HardLine, Doc.Text("b"));
        Assert.Equal("a\nb", Print(doc));
    }

    [Fact]
    public void Trim_RemovesTrailingBlanksOnDemand()
    {
        var doc = Doc.Concat(Doc.Text("a   "), Doc.Trim, Doc.Text("b"));
        Assert.Equal("ab", Print(doc));
    }

    [Fact]
    public void BrokenLine_IsNotPaddedWithIndentWhenNothingFollows()
    {
        // An indent must not leave trailing spaces on an otherwise-empty line.
        var doc = Doc.Indent(Doc.Concat(Doc.Text("a"), Doc.HardLine, Doc.HardLine, Doc.Text("b")));
        Assert.Equal("a\n\n    b", Print(doc));
    }

    // --- options -------------------------------------------------------------------

    [Fact]
    public void NewLineOption_ControlsTerminator()
    {
        var doc = Doc.Concat(Doc.Text("a"), Doc.HardLine, Doc.Text("b"));
        Assert.Equal("a\r\nb", DocPrinter.Print(doc, new PrintOptions { NewLine = "\r\n" }));
    }

    [Fact]
    public void UseTabsOption_IndentsWithTabs()
    {
        var doc = Doc.Indent(Doc.Concat(Doc.HardLine, Doc.Text("x")));
        Assert.Equal("\n\tx", DocPrinter.Print(doc, new PrintOptions { UseTabs = true }));
    }

    [Theory]
    [InlineData(2, "\n  x")]
    [InlineData(4, "\n    x")]
    [InlineData(8, "\n        x")]
    public void IndentSizeOption_ControlsWidth(int indentSize, string expected)
    {
        var doc = Doc.Indent(Doc.Concat(Doc.HardLine, Doc.Text("x")));
        Assert.Equal(expected, DocPrinter.Print(doc, new PrintOptions { IndentSize = indentSize }));
    }

    [Fact]
    public void TabWidthOption_AffectsFitArithmetic()
    {
        // With a tab counted as 8 columns, "abcd" no longer fits inside width 10.
        var doc = Doc.Indent(Doc.Concat(
            Doc.HardLine,
            Doc.Group(Doc.Concat(Doc.Text("ab"), Doc.Line, Doc.Text("cd")))));

        var narrowTabs = new PrintOptions { UseTabs = true, TabWidth = 8, MaxWidth = 10 };
        var wideBudget = new PrintOptions { UseTabs = true, TabWidth = 1, MaxWidth = 10 };

        Assert.Equal("\n\tab\n\tcd", DocPrinter.Print(doc, narrowTabs));
        Assert.Equal("\n\tab cd", DocPrinter.Print(doc, wideBudget));
    }

    // --- fit measurement -----------------------------------------------------------

    [Fact]
    public void Fits_AccountsForContentQueuedAfterTheGroup()
    {
        // "ab cd" is 5 columns and fits in 10 on its own, but the trailing text shares the
        // line and pushes it to 12, so the group must break.
        var doc = Doc.Concat(
            Doc.Group(Doc.Concat(Doc.Text("ab"), Doc.Line, Doc.Text("cd"))),
            Doc.Text("XXXXXXX"));

        Assert.Equal("ab\ncdXXXXXXX", Print(doc, maxWidth: 10));
    }

    [Fact]
    public void Fits_StopsMeasuringAtTheNextForcedNewline()
    {
        // The very long tail is on a later line, so it must not influence this group.
        var doc = Doc.Concat(
            Doc.Group(Doc.Concat(Doc.Text("ab"), Doc.Line, Doc.Text("cd"))),
            Doc.HardLine,
            Doc.Text(new string('X', 500)));

        Assert.StartsWith("ab cd\n", Print(doc, maxWidth: 10), StringComparison.Ordinal);
    }

    [Fact]
    public void Group_ExactlyAtMaxWidth_StaysFlat()
    {
        var doc = Doc.Group(Doc.Concat(Doc.Text("abc"), Doc.Line, Doc.Text("def")));
        Assert.Equal("abc def", Print(doc, maxWidth: 7));
        Assert.Equal("abc\ndef", Print(doc, maxWidth: 6));
    }

    // --- robustness ----------------------------------------------------------------

    // Guards the iterative design. Legacy T-SQL really does contain OR-chains and nested
    // CASE expressions thousands deep, and ScriptDom builds those as deep left-leaning
    // trees. A recursive printer would overflow the stack, which under NativeAOT is an
    // unrecoverable abort - and a crash cannot honour "return the input untouched".
    [Fact]
    public void DeeplyNestedDoc_DoesNotOverflowTheStack()
    {
        // No Indent here on purpose: indenting every level would make output size quadratic
        // in depth (level n carries n*IndentSize columns of leading whitespace), which
        // exhausts memory long before it would exhaust the stack and so tests the wrong
        // thing. Nested indentation growth is covered by Indent_Nests.
        const int depth = 20_000;
        var doc = Doc.Text("x");
        for (var i = 0; i < depth; i++)
        {
            doc = Doc.Group(Doc.Concat(Doc.Text("("), Doc.SoftLine, doc, Doc.SoftLine, Doc.Text(")")));
        }

        var result = Print(doc, maxWidth: 100);

        Assert.Equal(depth, result.Count(c => c == '('));
        Assert.Equal(depth, result.Count(c => c == ')'));
        Assert.Contains('x', result);
    }

    [Fact]
    public void DeeplyIndentedDoc_AccumulatesIndentWithoutOverflowing()
    {
        // Depth kept modest because indentation growth is inherently quadratic in output size.
        const int depth = 500;
        var doc = Doc.Text("x");
        for (var i = 0; i < depth; i++)
        {
            doc = Doc.Concat(Doc.Text("("), Doc.Indent(Doc.Concat(Doc.HardLine, doc)), Doc.Text(")"));
        }

        var result = Print(doc, maxWidth: 100);
        var lines = result.Split('\n');

        Assert.Equal(depth + 1, lines.Length);
        // Deepest line carries one indent level per wrapper.
        Assert.Equal(depth * 4, lines[^1].Length - lines[^1].TrimStart().Length);
    }

    [Fact]
    public void DeeplyChainedDoc_DoesNotOverflowTheStack()
    {
        // The other shape: a long flat chain rather than deep nesting.
        const int terms = 50_000;
        var parts = new List<Doc>(terms * 2);
        for (var i = 0; i < terms; i++)
        {
            if (i > 0)
            {
                parts.Add(Doc.Line);
                parts.Add(Doc.Text("OR"));
                parts.Add(Doc.Line);
            }

            parts.Add(Doc.Text("a=1"));
        }

        var doc = Doc.Group(Doc.Concat(parts));
        var result = Print(doc, maxWidth: 100);

        Assert.Equal(terms - 1, result.Split("OR").Length - 1);
    }

    // Break propagation mutates group state, so printing the same doc twice must not drift.
    [Fact]
    public void Print_IsRepeatable_ForTheSameDocInstance()
    {
        var doc = Doc.Group(Doc.Concat(
            Doc.Text("a"),
            Doc.Line,
            Doc.Group(Doc.Concat(Doc.Text("b"), Doc.HardLine, Doc.Text("c")))));

        var first = Print(doc);
        var second = Print(doc);
        var third = Print(doc);

        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    [Fact]
    public void Print_RejectsNullDoc()
    {
        Assert.Throws<ArgumentNullException>(() => DocPrinter.Print(null!));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("aéb", 3)]
    [InlineData("a\U0001F600b", 3)]
    public void StringWidth_CountsCodePointsNotUtf16Units(string value, int expected)
    {
        Assert.Equal(expected, DocPrinter.StringWidth(value));
    }
}
