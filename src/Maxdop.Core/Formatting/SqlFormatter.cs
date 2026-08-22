using Maxdop.Core.Comments;
using Maxdop.Core.Printing;
using Maxdop.Core.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Formatting;

/// <summary>
/// Formats T-SQL. The one entry point everything else — CLI, LSP, tests — goes through.
/// </summary>
public static class SqlFormatter
{
    public static FormatResult Format(string sql, FormatOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sql);
        options ??= FormatOptions.Default;

        if (sql.Length == 0)
        {
            return FormatResult.Success(sql, sql);
        }

        var root = Parse(sql, options, out var errors);

        if (errors.Count > 0)
        {
            // A file that does not parse as a whole may still be mostly formattable: one bad batch
            // in a migration should not cost the other nine their layout. Only reached from here,
            // so a file that parses can never be affected by batch splitting.
            if (BatchFormatter.TryFormat(sql, options) is { } byBatch)
            {
                return byBatch;
            }

            // Graceful passthrough: a file that does not parse is returned untouched, with a
            // distinct status so CI can tell "syntax error" from "maxdop broke".
            return FormatResult.ParseError(
                sql,
                [.. errors.Select(e => $"{e.Line}:{e.Column}: {e.Message} (error {e.Number})")]);
        }

        var comments = CommentAttacher.Attach(root);
        if (comments.Unattached.Count > 0)
        {
            return FormatResult.Refuse(
                sql,
                $"{comments.Unattached.Count} comment(s) could not be placed; refusing to format rather than lose them.");
        }

        // Line terminators are preserved rather than chosen: a CRLF file stays CRLF, an
        // encoding invariant. SSMS writes CRLF and mixing terminators shows up as a
        // whole-file diff, which would make maxdop unusable in a Windows-authored repo.
        var printOptions = options.Print with { NewLine = DetectNewLine(sql) };

        var printer = new SqlPrinter(root, comments, options);
        var formatted = DocPrinter.Print(printer.Print(root), printOptions);
        formatted = MatchTrailingNewLine(formatted, sql, printOptions.NewLine);

        // --- verification -----------------------------------------------------------
        //
        // Everything below is the safety net, and it is deliberately not
        // optional: there is no flag to turn it off. The cost is one extra parse — around 1.2ms
        // for a 2.8KB procedure, measured during the spike — which buys the only claim that
        // makes an automated formatter safe to point at a production codebase.
        var reparsed = Parse(formatted, options, out var reparseErrors);
        if (reparseErrors.Count > 0)
        {
            return FormatResult.Refuse(
                sql,
                $"formatted output no longer parses ({reparseErrors[0].Line}:{reparseErrors[0].Column}: {reparseErrors[0].Message}). "
                + "This is a maxdop bug; the input was left unchanged.",
                formatted);
        }

        // Invariant #1: the output means what the input meant.
        // The printer's keyword-position claims travel with the verification, not as a global setting:
        // it may only relax the comparison for tokens it actually recased.
        if (!RoundTripVerifier.Verify(root, reparsed, out var roundTripDiagnostic, printer.KeywordCasedTokens))
        {
            return FormatResult.Refuse(sql, roundTripDiagnostic, formatted);
        }

        // Comments are trivia, so the round-trip check cannot see them at all. Since mishandling
        // comments is the specific failure that makes existing T-SQL formatters untrustworthy,
        // it gets its own check rather than being assumed.
        if (!CommentsSurvived(comments, reparsed, out var commentDiagnostic))
        {
            return FormatResult.Refuse(sql, commentDiagnostic, formatted);
        }

        return FormatResult.Success(formatted, sql);
    }

    private static TSqlFragment Parse(string sql, FormatOptions options, out IList<ParseError> errors)
    {
        var parser = ParserFactory.Create(options);
        using var reader = new StringReader(sql);
        return parser.Parse(reader, out errors);
    }

    /// <summary>
    /// Checks the output's comments match the input's, in the same order.
    /// </summary>
    private static bool CommentsSurvived(CommentMap original, TSqlFragment root, out string diagnostic)
    {
        var before = original.All.Select(c => c.Text.Trim()).ToList();
        var after = new List<string>();
        foreach (var token in root.ScriptTokenStream ?? [])
        {
            if (token.IsComment())
            {
                after.Add((token.Text ?? string.Empty).Trim());
            }
        }

        if (before.Count != after.Count)
        {
            // Naming the first comment that went missing, not just the count. A bare count says a
            // comment was dropped somewhere in a ten-thousand-line script, which leaves grepping the
            // file as the only way forward; the comment's own text points straight at the construct
            // whose handler dropped it.
            diagnostic = $"comment count changed from {before.Count} to {after.Count}"
                + (FirstDivergence(before, after) is { } index
                    ? $"; first missing near comment {index + 1}: {Quote(before[index])}."
                    : ".");
            return false;
        }

        for (var i = 0; i < before.Count; i++)
        {
            if (!string.Equals(before[i], after[i], StringComparison.Ordinal))
            {
                diagnostic = $"comment {i + 1} changed or moved: expected {Quote(before[i])}, found {Quote(after[i])}.";
                return false;
            }
        }

        diagnostic = string.Empty;
        return true;
    }

    /// <summary>
    /// Index of the first position where the two comment sequences differ, or null when one is
    /// simply a prefix of the other.
    /// </summary>
    private static int? FirstDivergence(List<string> before, List<string> after)
    {
        for (var i = 0; i < Math.Min(before.Count, after.Count); i++)
        {
            if (!string.Equals(before[i], after[i], StringComparison.Ordinal))
            {
                return i;
            }
        }

        return before.Count > after.Count ? after.Count : null;
    }

    private static string Quote(string value) =>
        "\"" + (value.Length > 60 ? value[..60] + "…" : value).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// The file's dominant line terminator. CRLF wins if present at all, since a file with any
    /// CRLF was authored on Windows and rewriting it to LF would be a whole-file diff.
    /// </summary>
    private static string DetectNewLine(string sql) =>
        sql.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>
    /// Makes the output end the same way the input did. Adding or removing a final newline is a
    /// one-line diff on every file in a repo, and diff noise is how a formatter loses trust.
    /// </summary>
    private static string MatchTrailingNewLine(string formatted, string input, string newLine)
    {
        var inputEndsWithNewLine = input.EndsWith('\n');
        var outputEndsWithNewLine = formatted.EndsWith('\n');

        if (inputEndsWithNewLine == outputEndsWithNewLine)
        {
            return formatted;
        }

        return inputEndsWithNewLine
            ? formatted + newLine
            : formatted.TrimEnd('\n').TrimEnd('\r');
    }
}
