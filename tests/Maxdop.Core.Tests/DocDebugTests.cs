using Maxdop.Core.Printing;

namespace Maxdop.Core.Tests;

public class DocDebugTests
{
    [Fact]
    public void Describe_RendersTheTreeStructure()
    {
        var doc = Doc.Group(Doc.Concat(
            Doc.Text("SELECT"),
            Doc.Indent(Doc.Concat(Doc.Line, Doc.Text("a")))));

        Assert.Equal(
            """
            group
              concat (2)
                text "SELECT" (w=6)
                indent
                  concat (2)
                    line
                    text "a" (w=1)

            """,
            DocDebug.Describe(doc));
    }

    [Fact]
    public void Describe_ShowsPropagatedBreakDecisions()
    {
        var doc = Doc.Group(Doc.Concat(Doc.Text("a"), Doc.HardLine, Doc.Text("b")));

        Assert.DoesNotContain("[break]", DocDebug.Describe(doc), StringComparison.Ordinal);
        Assert.Contains("group [break]", DocDebug.Describe(doc, propagateBreaks: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_NamesGroupsAndTheirReferences()
    {
        var id = new GroupId("select-list");
        var doc = Doc.Concat(
            Doc.Group(Doc.Text("x"), id: id),
            Doc.IfBreak(Doc.Text("b"), Doc.Text("f"), id),
            Doc.IndentIfBreak(Doc.Text("c"), id));

        var described = DocDebug.Describe(doc);

        Assert.Contains("group #select-list", described, StringComparison.Ordinal);
        Assert.Contains("ifBreak -> #select-list", described, StringComparison.Ordinal);
        Assert.Contains("indentIfBreak -> #select-list", described, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_EscapesNewlinesInText()
    {
        Assert.Contains(@"text ""a\nb""", DocDebug.Describe(Doc.Text("a\nb")), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_HandlesDeepTreesWithoutOverflowing()
    {
        var doc = Doc.Text("x");
        for (var i = 0; i < 20_000; i++)
        {
            doc = Doc.Group(Doc.Concat(Doc.Text("("), doc, Doc.Text(")")));
        }

        var described = DocDebug.Describe(doc);

        Assert.Contains("group", described, StringComparison.Ordinal);
        // Indentation is capped, so deep nodes are annotated with their depth instead.
        Assert.Contains("[41] ", described, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_CapsIndentationButKeepsDepthVisible()
    {
        var doc = Doc.Text("x");
        for (var i = 0; i < 50; i++)
        {
            doc = Doc.Group(doc);
        }

        var lines = DocDebug.Describe(doc).Split('\n');
        var deepest = lines.First(l => l.Contains("text", StringComparison.Ordinal));

        Assert.StartsWith(new string(' ', 80) + "[50] ", deepest, StringComparison.Ordinal);
    }
}
