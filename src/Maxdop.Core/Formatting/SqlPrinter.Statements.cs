using Maxdop.Core.Printing;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Formatting;

/// <summary>
/// The procedural statements that are essentially a keyword and an argument list —
/// <c>RAISERROR</c>, <c>PRINT</c>.
/// </summary>
/// <remarks>
/// Picked from corpus data, where <c>RaiseErrorStatement</c> reached rank 1 once the
/// OUTPUT and OPTION deferrals were cleared: 3,750 statements across 28 files. Error handling is
/// dense in the kind of operational T-SQL people actually have.
/// <para>These share a shape that the discipline elsewhere in this printer handles well and
/// ScriptDom handles badly: the interesting tokens — the parentheses, the commas, the
/// <c>WITH NOWAIT</c> tail — belong to no node at all, and the option flags are an enum with no
/// token range. So the arguments are printed as nodes and everything around them is read from the
/// token stream, with both ends verified.</para>
/// </remarks>
public sealed partial class SqlPrinter
{
    private Doc PrintRaiseError(RaiseErrorStatement statement)
    {
        // Sorted into token order rather than trusted to arrive that way: the message, severity and
        // state are three separate properties and the rest are a list, so a single wrong assumption
        // about ordering would silently reorder a caller's format arguments.
        var arguments = new[] { statement.FirstParameter, statement.SecondParameter, statement.ThirdParameter }
            .Concat(statement.OptionalParameters)
            .Where(p => p is not null && p.FirstTokenIndex >= 0)
            .OrderBy(p => p!.FirstTokenIndex)
            .Select(p => (TSqlFragment)p!)
            .ToList();

        if (arguments.Count == 0 || !SeparatedBy(arguments))
        {
            return Passthrough(statement);
        }

        var headEnd = arguments[0].FirstTokenIndex - 1;
        var tailStart = arguments[^1].LastTokenIndex + 1;
        var tailEnd = RangeEndBeforeTerminators(statement);

        // `RAISERROR(` or `RAISERROR (`, then `)` optionally followed by `WITH LOG, NOWAIT,
        // SETERROR`. Anything else is a shape this does not model — including the legacy
        // `RAISERROR 50001 'msg'` form, which ScriptDom gives its own node type anyway.
        //
        // NoCommentsIn on both: neither region is owned by a node the printer visits, so a comment
        // in either has nothing to attach to and CasedTokens would drop it. Proven by the OPTION
        // clause, which lost comments in four corpus files for exactly this reason.
        if (!Compact(SignificantTextBetween(statement.FirstTokenIndex, headEnd))
                .Equals("RAISERROR(", StringComparison.OrdinalIgnoreCase)
            || !SignificantTextBetween(tailStart, tailEnd).StartsWith(')'))
        {
            return Passthrough(statement);
        }

        return Doc.Group(Doc.Concat(
            Keyword("RAISERROR"),
            Doc.Text("("),
            Doc.Indent(Doc.Concat(
                Doc.SoftLine,
                Doc.Join(Doc.Concat(Doc.Text(","), Doc.Line), arguments.Select(Print)))),
            Doc.SoftLine,

            // `)` and the WITH options in one slice. LOG, NOWAIT and SETERROR lex as identifiers but
            // the grammar admits nothing else here, so this is a keyword position and they are recased
            // — `WITH nowait` becomes `WITH NOWAIT`.
            Keywords(tailStart, tailEnd)));
    }

    private Doc PrintPrint(PrintStatement statement)
    {
        if (statement.Expression is null
            || !SignificantTextBetween(statement.FirstTokenIndex, EffectiveFirstToken(statement.Expression) - 1)
                .Equals("PRINT", StringComparison.OrdinalIgnoreCase)
            || !NothingAfter(statement.Expression, statement))
        {
            return Passthrough(statement);
        }

        return Doc.Group(Doc.Concat(
            Keyword("PRINT"),
            SeparatorBefore(statement.Expression),
            Doc.Indent(Print(statement.Expression))));
    }

    /// <summary>Text with all whitespace removed, for comparing a keyword run against a literal.</summary>
    /// <remarks>
    /// <c>RAISERROR('x', 16, 1)</c> and <c>RAISERROR ('x', 16, 1)</c> are both idiomatic, and
    /// <see cref="SignificantTextBetween"/> preserves the space between them. Comparing without
    /// whitespace accepts both without needing two literals per check.
    /// </remarks>
    private static string Compact(string text) =>
        text.Replace(" ", string.Empty, StringComparison.Ordinal);
}
