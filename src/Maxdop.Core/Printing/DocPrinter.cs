using System.Text;

namespace Maxdop.Core.Printing;

/// <summary>
/// The layout engine. Walks a <see cref="Doc"/> tree once and decides, per group, whether it
/// fits in the remaining width; if not, every line inside that group becomes a newline.
/// </summary>
/// <remarks>
/// This is the whole of maxdop's formatting policy engine, and it is deliberately generic —
/// it knows nothing about SQL. Node handlers describe structure, this decides geometry.
/// <para>
/// Linear time, no backtracking: each group is measured at most once, and the fit test stops
/// as soon as it exceeds the remaining width or reaches a newline. Iterative for the same
/// reason as <see cref="BreakPropagator"/> — unbounded SQL nesting must not overflow the stack.
/// </para>
/// </remarks>
public static class DocPrinter
{
    public static string Print(Doc doc, PrintOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        options ??= PrintOptions.Default;

        BreakPropagator.Propagate(doc);

        var output = new StringBuilder();
        var groupModes = new Dictionary<GroupId, PrintMode>();
        var lineSuffixes = new List<Cmd>();
        var fitsScratch = new List<Cmd>();

        // Root is in break mode: the document as a whole is allowed to span lines.
        var cmds = new List<Cmd> { new(Indentation.Root, PrintMode.Break, doc) };

        // Column of the next character to be written.
        var pos = 0;

        // Set when a hard line is met while flat: the next group must be re-measured rather
        // than inheriting the stale flat decision.
        var shouldRemeasure = false;

        while (true)
        {
            if (cmds.Count == 0)
            {
                if (lineSuffixes.Count == 0)
                {
                    break;
                }

                // End of document with trailing comments still pending — flush them.
                FlushLineSuffixes(cmds, lineSuffixes);
                continue;
            }

            var cmd = cmds[^1];
            cmds.RemoveAt(cmds.Count - 1);
            var (indent, mode, current) = cmd;

            switch (current)
            {
                case DocText text:
                    output.Append(text.Value);
                    pos += text.Width;
                    break;

                case DocConcat concat:
                    for (var i = concat.Parts.Count - 1; i >= 0; i--)
                    {
                        cmds.Add(new Cmd(indent, mode, concat.Parts[i]));
                    }

                    break;

                case DocIndent doc2:
                    cmds.Add(new Cmd(indent.Indent(options), mode, doc2.Contents));
                    break;

                case DocAlign align:
                    cmds.Add(new Cmd(indent.Align(align.Width), mode, align.Contents));
                    break;

                case DocTrim:
                    pos -= TrimTrailingBlanks(output);
                    break;

                case DocGroup group:
                {
                    if (mode == PrintMode.Flat && !shouldRemeasure)
                    {
                        // Already inside a line that fits; no new decision to make.
                        cmds.Add(new Cmd(indent, group.ShouldBreak ? PrintMode.Break : PrintMode.Flat, group.Contents));
                    }
                    else
                    {
                        shouldRemeasure = false;
                        var flat = new Cmd(indent, PrintMode.Flat, group.Contents);
                        var fitsFlat = !group.ShouldBreak
                            && Fits(flat, cmds, options.MaxWidth - pos, lineSuffixes.Count > 0, groupModes, options, fitsScratch);
                        cmds.Add(fitsFlat ? flat : new Cmd(indent, PrintMode.Break, group.Contents));
                    }

                    if (group.Id is not null)
                    {
                        // Record the decision so IfBreak/IndentIfBreak elsewhere can key off it.
                        groupModes[group.Id] = cmds[^1].Mode;
                    }

                    break;
                }

                case DocIfBreak ifBreak:
                {
                    var reference = ifBreak.GroupId is null
                        ? mode
                        : groupModes.GetValueOrDefault(ifBreak.GroupId, PrintMode.Flat);
                    var chosen = reference == PrintMode.Break ? ifBreak.WhenBroken : ifBreak.WhenFlat;
                    cmds.Add(new Cmd(indent, mode, chosen));
                    break;
                }

                case DocIndentIfBreak indentIfBreak:
                {
                    var reference = groupModes.GetValueOrDefault(indentIfBreak.GroupId, PrintMode.Flat);
                    var effective = reference == PrintMode.Break ? indent.Indent(options) : indent;
                    cmds.Add(new Cmd(effective, mode, indentIfBreak.Contents));
                    break;
                }

                case DocLineSuffix lineSuffix:
                    lineSuffixes.Add(new Cmd(indent, mode, lineSuffix.Contents));
                    break;

                case DocUnmeasured unmeasured:
                    cmds.Add(new Cmd(indent, mode, unmeasured.Contents));
                    break;

                case DocLineSuffixBoundary:
                    if (lineSuffixes.Count > 0)
                    {
                        // Force a newline so pending trailing content cannot swallow what follows.
                        cmds.Add(new Cmd(indent, mode, Doc.HardLine));
                    }

                    break;

                case DocBreakParent:
                    // Consumed by BreakPropagator; nothing to emit.
                    break;

                case DocLine line:
                {
                    if (mode == PrintMode.Flat && !line.IsHard)
                    {
                        if (line.Kind == LineKind.Space)
                        {
                            output.Append(' ');
                            pos++;
                        }

                        break;
                    }

                    if (mode == PrintMode.Flat)
                    {
                        // A hard line inside what we thought was a flat run. Honour the newline
                        // and force the next group to measure again.
                        shouldRemeasure = true;
                    }

                    if (lineSuffixes.Count > 0)
                    {
                        // Trailing comments belong before this newline. Re-queue the line behind them.
                        cmds.Add(cmd);
                        FlushLineSuffixes(cmds, lineSuffixes);
                        break;
                    }

                    if (line.Kind == LineKind.Literal)
                    {
                        output.Append(options.NewLine);
                        pos = 0;
                    }
                    else
                    {
                        pos -= TrimTrailingBlanks(output);
                        output.Append(options.NewLine).Append(indent.Value);
                        pos = indent.Length;
                    }

                    break;
                }

                default:
                    throw new InvalidOperationException($"Unhandled doc type {current.GetType().Name}.");
            }
        }

        return output.ToString();
    }

    /// <summary>
    /// Measures whether <paramref name="next"/> — plus whatever already-queued content would
    /// share its line — stays within <paramref name="width"/> columns. Stops at the first
    /// newline, so cost is proportional to one line's worth of docs, not the subtree.
    /// </summary>
    private static bool Fits(
        Cmd next,
        List<Cmd> restCmds,
        int width,
        bool hasLineSuffix,
        Dictionary<GroupId, PrintMode> groupModes,
        PrintOptions options,
        List<Cmd> scratch)
    {
        if (width < 0)
        {
            return false;
        }

        // Content after the group matters: `SELECT a, b) AS x` only fits if the trailing `) AS x`
        // fits too. Walk the enclosing continuation from the top of the real stack downward.
        var restIdx = restCmds.Count;

        scratch.Clear();
        scratch.Add(next);

        while (width >= 0)
        {
            if (scratch.Count == 0)
            {
                if (restIdx == 0)
                {
                    return true;
                }

                scratch.Add(restCmds[--restIdx]);
                continue;
            }

            var (indent, mode, current) = scratch[^1];
            scratch.RemoveAt(scratch.Count - 1);

            switch (current)
            {
                case DocText text:
                    width -= text.Width;
                    break;

                case DocConcat concat:
                    for (var i = concat.Parts.Count - 1; i >= 0; i--)
                    {
                        scratch.Add(new Cmd(indent, mode, concat.Parts[i]));
                    }

                    break;

                case DocIndent doc2:
                    scratch.Add(new Cmd(indent.Indent(options), mode, doc2.Contents));
                    break;

                case DocAlign align:
                    scratch.Add(new Cmd(indent.Align(align.Width), mode, align.Contents));
                    break;

                case DocGroup group:
                    // A group already known to break stays broken while measuring, so its own
                    // hard lines terminate the measurement rather than being flattened away.
                    scratch.Add(new Cmd(indent, group.ShouldBreak ? PrintMode.Break : mode, group.Contents));
                    break;

                case DocIfBreak ifBreak:
                {
                    var reference = ifBreak.GroupId is null
                        ? mode
                        : groupModes.GetValueOrDefault(ifBreak.GroupId, PrintMode.Flat);
                    scratch.Add(new Cmd(indent, mode, reference == PrintMode.Break ? ifBreak.WhenBroken : ifBreak.WhenFlat));
                    break;
                }

                case DocIndentIfBreak indentIfBreak:
                    scratch.Add(new Cmd(indent, mode, indentIfBreak.Contents));
                    break;

                case DocLine line:
                    // Reaching a newline means the rest of the line is settled: it fits.
                    if (mode == PrintMode.Break || line.IsHard)
                    {
                        return true;
                    }

                    if (line.Kind == LineKind.Space)
                    {
                        width--;
                    }

                    break;

                case DocLineSuffix:
                    hasLineSuffix = true;
                    break;

                // Contributes no width by design, so its contents are not walked at all.
                case DocUnmeasured:
                    break;

                case DocLineSuffixBoundary:
                    if (hasLineSuffix)
                    {
                        return true;
                    }

                    break;

                default:
                    // BreakParent and Trim contribute no width. Trim is approximated as zero
                    // here, since the measurement has no output buffer to inspect; it can only
                    // ever make the real line shorter, so a fit decision stays sound.
                    break;
            }
        }

        return false;
    }

    private static void FlushLineSuffixes(List<Cmd> cmds, List<Cmd> lineSuffixes)
    {
        for (var i = lineSuffixes.Count - 1; i >= 0; i--)
        {
            cmds.Add(lineSuffixes[i]);
        }

        lineSuffixes.Clear();
    }

    private static int TrimTrailingBlanks(StringBuilder output)
    {
        var removed = 0;
        while (output.Length > 0 && (output[^1] == ' ' || output[^1] == '\t'))
        {
            output.Length--;
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// Display width of <paramref name="value"/> in columns. Counts code points rather than
    /// UTF-16 units so an astral-plane character (emoji in a comment) counts once.
    /// </summary>
    /// <remarks>
    /// Does not account for East Asian wide characters, which would make a CJK comment measure
    /// narrower than it renders. Width is a cosmetic decision only — never a correctness one —
    /// and full width tables need ICU, which <c>InvariantGlobalization</c> excludes.
    /// </remarks>
    internal static int StringWidth(string value)
    {
        var width = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsLowSurrogate(value[i]))
            {
                width++;
            }
        }

        return width;
    }

    private enum PrintMode
    {
        /// <summary>Lines inside collapse to spaces or nothing.</summary>
        Flat,

        /// <summary>Lines inside become newlines.</summary>
        Break,
    }

    /// <summary>One unit of pending work: what to print, at what indent, in which mode.</summary>
    private readonly record struct Cmd(Indentation Indent, PrintMode Mode, Doc Doc);

    /// <summary>
    /// The prefix written after a newline, carried as both text and column count so tabs can
    /// count as more than one column.
    /// </summary>
    private readonly record struct Indentation(string Value, int Length)
    {
        internal static readonly Indentation Root = new(string.Empty, 0);

        internal Indentation Indent(PrintOptions options) => options.UseTabs
            ? new Indentation(Value + '\t', Length + options.TabWidth)
            : new Indentation(Value + new string(' ', options.IndentSize), Length + options.IndentSize);

        // Alignment is always spaces, even under UseTabs: it exists to line content up with a
        // specific column, which a tab cannot express.
        internal Indentation Align(int width) => width <= 0
            ? this
            : new Indentation(Value + new string(' ', width), Length + width);
    }
}
