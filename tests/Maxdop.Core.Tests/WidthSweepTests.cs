using Maxdop.Core.Formatting;
using Maxdop.Core.Printing;

namespace Maxdop.Core.Tests;

/// <summary>
/// Formats the committed construct corpus at every width in a range and asserts the result both
/// survives its own safety gates and is a fixed point.
/// </summary>
/// <remarks>
/// Width is <em>the</em> input to a Wadler layout engine: every <see cref="Doc.Group"/> decision is
/// a measurement against the remaining columns, so a width is not one setting among nine — it is the
/// variable the whole printer is a function of. Almost everything else was tested at the 120-column
/// default, which exercises one point on that curve.
/// <para>The interesting failures live at the ends. A narrow width forces every group to break, so
/// constructs that are only ever seen flat get laid out for the first time; a wide one flattens
/// groups that have only ever been seen broken, which is where a missing
/// <see cref="Doc.BreakParent"/> stops being invisible. Both directions produce output no fixture
/// covers.</para>
/// <para>The corpus files are the same ones the comment fuzzer uses, so this surface grows
/// automatically when a construct is added — no fixture of its own to keep in step.</para>
/// </remarks>
public class WidthSweepTests
{
    /// <summary>
    /// Narrowest width swept. Below roughly this, output is dominated by tokens that cannot fit on
    /// any line whatever the printer does, and the sweep stops saying anything about layout.
    /// </summary>
    private const int NarrowestWidth = 40;

    /// <summary>
    /// Widest width swept. Past this every corpus construct fits flat, so further widths re-test one
    /// layout at increasing cost.
    /// </summary>
    private const int WidestWidth = 200;

    /// <summary>
    /// (file, width) pairs where the formatter refuses its own output.
    /// </summary>
    /// <remarks>
    /// A refusal is safe — the input is returned untouched — but it means the file cannot be
    /// formatted at that width at all, which is a defect rather than a policy. Ratcheted rather than
    /// asserted at zero so the number can only fall.
    /// </remarks>
    private const int KnownRefusalWidths = 0;

    /// <summary>
    /// (file, width) pairs where formatting is not a fixed point: formatting the output again at the
    /// same width changes it.
    /// </summary>
    /// <remarks>
    /// Invisible to every safety gate. A second pass that moves something still round-trips, still
    /// preserves its comments and still re-parses, so nothing refuses — but <c>--check</c> then
    /// disagrees with <c>--write</c>, which is the one contradiction a CI-facing formatter cannot
    /// have.
    /// </remarks>
    private const int KnownDriftWidths = 0;

    private static IEnumerable<string> CorpusFileNames() =>
        Directory.EnumerateFiles(
                Path.Combine(AppContext.BaseDirectory, "corpus"),
                "*.sql",
                SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(Path.GetFileName)!;

    [Fact]
    public void FormattingSurvivesAndSettlesAtEveryWidth()
    {
        var refused = 0;
        var drifted = 0;
        var examples = new List<string>();

        foreach (var name in CorpusFileNames())
        {
            var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "corpus", name));

            for (var width = NarrowestWidth; width <= WidestWidth; width++)
            {
                var once = Format(sql, width);

                if (once.Status == FormatStatus.Refused)
                {
                    refused++;
                    Record(examples, $"{name} @ {width}: refused — {(once.Diagnostics.Count > 0 ? once.Diagnostics[0] : "no diagnostic")}");
                    continue;
                }

                // A construct that falls to the passthrough path is a coverage gap, not a width
                // defect, and it cannot lose or move anything. Same exclusion the comment fuzzer makes.
                if (once.Status != FormatStatus.Formatted)
                {
                    continue;
                }

                var twice = Format(once.Output, width);
                if (twice.Status != FormatStatus.Formatted || !string.Equals(once.Output, twice.Output, StringComparison.Ordinal))
                {
                    drifted++;
                    Record(examples, $"{name} @ {width}: not a fixed point ({FirstDifference(once.Output, twice.Output)})");
                }
            }
        }

        Assert.True(
            refused <= KnownRefusalWidths && drifted <= KnownDriftWidths,
            $"width sweep {NarrowestWidth}..{WidestWidth}: {refused} refusal(s) (known {KnownRefusalWidths}), "
                + $"{drifted} drift(s) (known {KnownDriftWidths}).\n  "
                + string.Join("\n  ", examples.Take(12)));

        Assert.True(
            refused == KnownRefusalWidths && drifted == KnownDriftWidths,
            $"width sweep improved: {refused} refusals and {drifted} drifts, against known "
                + $"{KnownRefusalWidths} and {KnownDriftWidths}. Lower the constants so the ratchet holds.");
    }

    /// <summary>
    /// How often a formatted line runs past the requested width.
    /// </summary>
    /// <remarks>
    /// Not a bug on its own: the printer honours the width where it has a break point, and there are
    /// places it deliberately has none. Comments are emitted through <see cref="Doc.Verbatim"/> and
    /// never re-flowed — reformatting the inside of someone's comment is not the formatter's
    /// business — and a keyword run emitted as one slice, <c>DECLARE c CURSOR LOCAL FORWARD_ONLY
    /// KEYSET SCROLL_LOCKS FOR</c>, has nowhere to break by construction.
    /// <para>Comment-only lines are excluded for that reason. What is left is the interesting
    /// population: code the printer laid out and could not fit. Each one is either a construct with
    /// no modelled break point or a group that should have broken and did not, and the two are only
    /// distinguishable by looking. Ratcheted so the number can only fall, and so adding a handler
    /// that introduces a break point shows up as progress rather than as nothing.</para>
    /// </remarks>
    private const int KnownOverlongLines = 118;

    [Fact]
    public void OverlongLinesOnlyOccurWhereThePrinterHasNoBreakPoint()
    {
        var overlong = 0;
        var examples = new List<string>();

        foreach (var name in CorpusFileNames())
        {
            var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "corpus", name));

            foreach (var width in new[] { NarrowestWidth, 80, 120, WidestWidth })
            {
                var result = Format(sql, width);
                if (result.Status != FormatStatus.Formatted)
                {
                    continue;
                }

                var lines = result.Output.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].TrimEnd('\r');
                    if (line.Length <= width || IsCommentLine(line) || LongestToken(line) >= width)
                    {
                        continue;
                    }

                    overlong++;
                    Record(examples, $"{name} @ {width}: line {i + 1} is {line.Length} cols — {Trim(line)}");
                }
            }
        }

        Assert.True(
            overlong <= KnownOverlongLines,
            $"{overlong} line(s) exceed the requested width (known {KnownOverlongLines}):\n  "
                + string.Join("\n  ", examples.Take(12)));

        Assert.True(
            overlong == KnownOverlongLines,
            $"width adherence improved: {overlong} overlong line(s) against known "
                + $"{KnownOverlongLines}. Lower the constant so the ratchet holds.");
    }

    /// <summary>
    /// A line the printer emitted verbatim rather than laid out. Approximate on purpose: the interior
    /// of a block comment has no syntax of its own to test, and treating a stray <c>*</c> as a comment
    /// only ever excludes a line from the count, never adds one.
    /// </summary>
    private static bool IsCommentLine(string line)
    {
        var text = line.TrimStart();
        return text.StartsWith("--", StringComparison.Ordinal)
            || text.StartsWith("/*", StringComparison.Ordinal)
            || text.StartsWith('*');
    }

    private static FormatResult Format(string sql, int width) =>
        SqlFormatter.Format(sql, FormatOptions.Default with
        {
            Print = PrintOptions.Default with { MaxWidth = width },
        });

    private static void Record(List<string> examples, string entry)
    {
        if (examples.Count < 64)
        {
            examples.Add(entry);
        }
    }

    private static int Indentation(string line) => line.Length - line.TrimStart(' ').Length;

    /// <summary>
    /// Longest run of non-space characters on the line — the widest thing the printer could not have
    /// broken however it laid the line out.
    /// </summary>
    private static int LongestToken(string line)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in line)
        {
            run = c == ' ' ? 0 : run + 1;
            longest = Math.Max(longest, run);
        }

        return longest;
    }

    private static string FirstDifference(string first, string second)
    {
        var lines = first.Split('\n');
        var others = second.Split('\n');
        for (var i = 0; i < Math.Min(lines.Length, others.Length); i++)
        {
            if (!string.Equals(lines[i], others[i], StringComparison.Ordinal))
            {
                return $"line {i + 1}: {Trim(lines[i])} → {Trim(others[i])}";
            }
        }

        return $"{lines.Length} lines → {others.Length} lines";
    }

    private static string Trim(string value)
    {
        var text = value.Trim();
        return text.Length > 60 ? text[..60] + "…" : text;
    }
}
