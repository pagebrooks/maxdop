using System.Text;

namespace Maxdop.Core.Printing;

/// <summary>
/// Renders a <see cref="Doc"/> tree as indented text. Diagnostics only — never part of output.
/// </summary>
/// <remarks>
/// When a node handler produces wrong output the question is almost always "did the handler
/// build the wrong structure, or did the engine lay out the right structure badly?" Printing
/// the IR separates those two immediately, so this is the first tool to reach for rather than
/// something to write later under pressure.
/// </remarks>
public static class DocDebug
{
    /// <param name="propagateBreaks">
    /// Run break propagation first, so groups display the break decisions the printer will
    /// actually see. Mutates group state exactly as <see cref="DocPrinter.Print"/> would.
    /// </param>
    /// <summary>
    /// Indentation stops growing past this depth, and deeper nodes are prefixed with their
    /// actual depth instead. Without a cap, describing a doc thousands of levels deep would
    /// emit indentation quadratic in depth and exhaust memory before printing anything useful.
    /// </summary>
    private const int MaxIndentDepth = 40;

    public static string Describe(Doc doc, bool propagateBreaks = false)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (propagateBreaks)
        {
            BreakPropagator.Propagate(doc);
        }

        var output = new StringBuilder();

        // Iterative for the same reason as the printer: doc depth is unbounded.
        var stack = new List<(Doc Doc, int Depth)> { (doc, 0) };

        while (stack.Count > 0)
        {
            var (current, depth) = stack[^1];
            stack.RemoveAt(stack.Count - 1);

            output.Append(' ', Math.Min(depth, MaxIndentDepth) * 2);
            if (depth > MaxIndentDepth)
            {
                output.Append('[').Append(depth).Append("] ");
            }

            output.Append(Label(current)).Append('\n');

            foreach (var child in Children(current).Reverse())
            {
                stack.Add((child, depth + 1));
            }
        }

        return output.ToString();
    }

    private static string Label(Doc doc) => doc switch
    {
        DocText text => text.Value.Length == 0 ? "empty" : $"text {Quote(text.Value)} (w={text.Width})",
        DocLine line => line.Kind switch
        {
            LineKind.Soft => "softline",
            LineKind.Space => "line",
            LineKind.Hard => "hardline",
            LineKind.Literal => "literalline",
            _ => "line?",
        },
        DocConcat concat => $"concat ({concat.Parts.Count})",
        DocGroup group => "group"
            + (group.ShouldBreak ? " [break]" : string.Empty)
            + (group.Id is null ? string.Empty : $" #{group.Id}"),
        DocIndent => "indent",
        DocAlign align => $"align {align.Width}",
        DocIfBreak ifBreak => "ifBreak" + (ifBreak.GroupId is null ? string.Empty : $" -> #{ifBreak.GroupId}"),
        DocIndentIfBreak indentIfBreak => $"indentIfBreak -> #{indentIfBreak.GroupId}",
        DocLineSuffix => "lineSuffix",
        DocUnmeasured => "unmeasured",
        DocLineSuffixBoundary => "lineSuffixBoundary",
        DocBreakParent => "breakParent",
        DocTrim => "trim",
        _ => doc.GetType().Name,
    };

    private static IEnumerable<Doc> Children(Doc doc) => doc switch
    {
        DocConcat concat => concat.Parts,
        DocGroup group => [group.Contents],
        DocIndent indent => [indent.Contents],
        DocAlign align => [align.Contents],
        DocIfBreak ifBreak => [ifBreak.WhenBroken, ifBreak.WhenFlat],
        DocIndentIfBreak indentIfBreak => [indentIfBreak.Contents],
        DocLineSuffix lineSuffix => [lineSuffix.Contents],
        DocUnmeasured unmeasured => [unmeasured.Contents],
        _ => [],
    };

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal)
                    .Replace("\n", "\\n", StringComparison.Ordinal)
                    .Replace("\t", "\\t", StringComparison.Ordinal) + "\"";
}
