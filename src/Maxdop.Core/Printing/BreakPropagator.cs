namespace Maxdop.Core.Printing;

/// <summary>
/// Marks every group that contains something unflattenable — a hard line, a literal line, or
/// an explicit <see cref="Doc.BreakParent"/> — as broken, cascading outward to ancestors.
/// </summary>
/// <remarks>
/// Running this first is what lets <see cref="DocPrinter"/>'s fit test stay linear: the test
/// never has to scan a whole subtree looking for a hard line, because any group containing one
/// already says so. A group explicitly constructed with <c>shouldBreak: true</c> also
/// propagates, so callers can force a construct multi-line and have its parents follow.
/// <para>
/// Iterative by design. Doc depth tracks SQL expression nesting, and left-leaning
/// <c>OR</c>/<c>AND</c> chains in legacy T-SQL run thousands deep; a recursive walk would
/// overflow the stack, and under NativeAOT that is an unrecoverable process abort — which
/// would break the "never destroy files" invariant.
/// </para>
/// </remarks>
internal static class BreakPropagator
{
    internal static void Propagate(Doc root)
    {
        // (doc, isExit): a group is pushed twice, and on the second visit its accumulated
        // ShouldBreak is propagated to its own parent. Since children are visited before the
        // exit marker, propagation cascades innermost-outward in one pass.
        var stack = new List<(Doc Doc, bool IsExit)> { (root, false) };
        var groups = new List<DocGroup>();

        while (stack.Count > 0)
        {
            var (doc, isExit) = stack[^1];
            stack.RemoveAt(stack.Count - 1);

            if (isExit)
            {
                var exited = (DocGroup)doc;
                groups.RemoveAt(groups.Count - 1);
                if (exited.ShouldBreak)
                {
                    BreakNearestEnclosingGroup(groups);
                }

                continue;
            }

            switch (doc)
            {
                case DocLine { IsHard: true }:
                case DocBreakParent:
                    BreakNearestEnclosingGroup(groups);
                    break;

                case DocGroup group:
                    groups.Add(group);
                    stack.Add((group, true));
                    stack.Add((group.Contents, false));
                    break;

                case DocConcat concat:
                    for (var i = concat.Parts.Count - 1; i >= 0; i--)
                    {
                        stack.Add((concat.Parts[i], false));
                    }

                    break;

                case DocIndent indent:
                    stack.Add((indent.Contents, false));
                    break;

                case DocAlign align:
                    stack.Add((align.Contents, false));
                    break;

                case DocIndentIfBreak indentIfBreak:
                    stack.Add((indentIfBreak.Contents, false));
                    break;

                // Both branches are walked even though only one will print. A hard line in
                // either arm therefore forces the break. That matches Prettier, and is the
                // conservative direction: forcing a break can only cost vertical space,
                // whereas missing one can produce output that no longer parses.
                case DocIfBreak ifBreak:
                    stack.Add((ifBreak.WhenBroken, false));
                    stack.Add((ifBreak.WhenFlat, false));
                    break;

                // Deferred content still propagates: a trailing comment's BreakParent is
                // precisely how the construct it trails gets forced multi-line.
                case DocLineSuffix lineSuffix:
                    stack.Add((lineSuffix.Contents, false));
                    break;

                // Transparent to propagation even though the printer ignores its width: a
                // multi-line comment inside still forces its enclosing groups to break.
                case DocUnmeasured unmeasured:
                    stack.Add((unmeasured.Contents, false));
                    break;

                default:
                    // Text, soft/space lines, line-suffix boundaries and trims cannot force breaks.
                    break;
            }
        }
    }

    private static void BreakNearestEnclosingGroup(List<DocGroup> groups)
    {
        if (groups.Count > 0)
        {
            groups[^1].ShouldBreak = true;
        }
    }
}
