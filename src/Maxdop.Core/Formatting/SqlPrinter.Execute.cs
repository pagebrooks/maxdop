using Maxdop.Core.Printing;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Formatting;

/// <summary>
/// <c>EXEC</c> / <c>EXECUTE</c>, in its procedure-call and dynamic-string forms.
/// </summary>
/// <remarks>
/// The largest single gap left in real-world scripts when this was written — 23% of all remaining
/// verbatim text, across 31 of 36 files — because operational T-SQL is largely one procedure calling
/// another and building dynamic SQL.
/// <para>Nearly every interesting token belongs to no node: the <c>EXEC</c> keyword itself, the <c>=</c>
/// of a return-value assignment, the parentheses around a dynamic string, the <c>=</c> of a named
/// parameter, the <c>OUTPUT</c> after one, and the <c>WITH</c> of <c>WITH RECOMPILE</c>. So this reads
/// the whole scaffolding from the token stream and prints only the pieces that are nodes — the same
/// strategy as column and parameter declarations, and for the same reason.</para>
/// </remarks>
public sealed partial class SqlPrinter
{
    private Doc PrintExecute(ExecuteStatement statement) =>
        // The statement's range runs past the specification's, over any `WITH RECOMPILE`, so the end is
        // taken from the statement while the head is the same either way.
        PrintExecuteSpecification(statement.ExecuteSpecification, statement, RangeEndBeforeTerminators(statement));

    /// <summary>
    /// The same <c>EXEC …</c> reached without a statement around it, as the source of an
    /// <c>INSERT … EXEC</c>.
    /// </summary>
    private Doc PrintExecuteSpecification(ExecuteSpecification specification) =>
        PrintExecuteSpecification(specification, specification, specification.LastTokenIndex);

    private Doc PrintExecuteSpecification(ExecuteSpecification? specification, TSqlFragment node, int end)
    {
        var entity = specification?.ExecutableEntity;

        // `AS LOGIN = 'sa'` is still its own shape; `AT linkedserver` is handled below.
        if (specification is null || entity is null || specification.ExecuteContext is not null)
        {
            return Passthrough(node);
        }

        var headEnd = EffectiveFirstToken(entity) - 1;

        // `EXEC ('…') AT DataSource` — the linked server is an *identifier*, so it cannot go through the
        // keyword slice that carries the rest of the tail: recasing it would rename the server. The tail
        // is cut short of it and the name Printed instead.
        var linkedServer = specification.LinkedServer;
        var tailEnd = linkedServer is null ? end : EffectiveFirstToken(linkedServer) - 1;

        // `EXEC`, `EXECUTE`, `EXEC @rc =`, and for a dynamic string the opening parenthesis too. The
        // head must exist — an EXEC with nothing before its entity is not a shape this understands.
        var headText = SignificantTextBetween(node.FirstTokenIndex, headEnd);
        var tailText = SignificantTextBetween(entity.LastTokenIndex + 1, tailEnd);

        if (headText.Length == 0
            || !NoCommentsIn(entity.LastTokenIndex + 1, end))
        {
            return Passthrough(node);
        }

        // The tail is emitted as a pure-keyword slice, so it must contain no *nodes* — a node in that
        // range is something with a name in it, and the slice would recase it.
        //
        // `WITH RESULT SETS ((Dobidoo BIGINT NOT NULL))` is exactly that: column names, a four-part
        // object name after AS OBJECT, a type name after AS TYPE. `server1.db.dbo.t1` came out as
        // `SERVER1.DB.DBO.T1`, and *silently* — the slice claims those positions, so the verifier
        // relaxes case precisely where it must not and the round trip passes. Found by formatting the
        // corpus twice, once per keyword case, and looking for words that changed that were not
        // keywords.
        //
        // Only the *definitions* form carries names: `WITH RESULT SETS NONE` and `UNDEFINED` have
        // none, and `WITH RECOMPILE` is a bare option, so both keep their layout. Declining on any
        // node in the tail range was the first attempt and cost `WITH RECOMPILE` its formatting for
        // no safety gained. Same hazard the linked server above already dodges, one clause along.
        if (node is ExecuteStatement execute
            && execute.Options.Any(option => option is ResultSetsExecuteOption { Definitions.Count: > 0 }))
        {
            return Passthrough(node);
        }

        var docs = new List<Doc>
        {
            Keywords(node.FirstTokenIndex, headEnd),

            // No space after an opening parenthesis, so `EXEC (@sql)` does not become `EXEC ( @sql )`.
            headText.EndsWith('(') ? Doc.Empty : Doc.Text(" "),
            Print(entity),
        };

        if (tailText.Length > 0)
        {
            // Likewise no space before the closing parenthesis, while `WITH RECOMPILE` does want one.
            docs.Add(tailText.StartsWith(')') ? Doc.Empty : Doc.Text(" "));
            docs.Add(Keywords(entity.LastTokenIndex + 1, tailEnd));
        }

        if (linkedServer is not null)
        {
            docs.Add(Doc.Text(" "));
            docs.Add(Print(linkedServer));

            if (SignificantTextBetween(linkedServer.LastTokenIndex + 1, end).Length > 0)
            {
                return Passthrough(node);
            }
        }

        return Doc.Concat(docs);
    }

    /// <summary><c>dbo.DoThing @a = 1, @b OUTPUT</c></summary>
    private Doc PrintExecutableProcedureReference(ExecutableProcedureReference reference)
    {
        var procedure = reference.ProcedureReference;
        if (procedure is null || reference.AdHocDataSource is not null || !SeparatedBy(reference.Parameters))
        {
            return Passthrough(reference);
        }

        if (reference.Parameters.Count == 0)
        {
            // No parameters means the reference is nothing but its name — checked rather than assumed,
            // since anything else in the range would be dropped.
            return SignificantTextBetween(procedure.LastTokenIndex + 1, reference.LastTokenIndex).Length == 0
                ? Print(procedure)
                : Passthrough(reference);
        }

        // Grouped so a long argument list breaks one per line under the procedure name, the same shape
        // as a SET list or a DECLARE.
        return Doc.Group(Doc.Concat(
            Print(procedure),
            Doc.Indent(Doc.Concat(
                Doc.Line,
                JoinList(reference.Parameters)))));
    }

    /// <summary>
    /// The dynamic-SQL form's contents: <c>@sql</c>, <c>EXEC (@a, @b)</c>, or the concatenated form
    /// <c>EXEC (N'SELECT ' + @cols + N' FROM t')</c>.
    /// </summary>
    /// <remarks>
    /// ScriptDom puts both separators in the same list, so the separator is read per pair from the
    /// tokens rather than assumed. Assuming a comma made this the seventh-largest gap in real-world
    /// scripts: <b>concatenation is how dynamic SQL is actually written</b>, and the comma form —
    /// which pastes the fragments together with no operator at all — is the rare one.
    /// <para>A comma-separated list is laid out as a list; a concatenation is laid out like any other
    /// binary expression, operator at the end of the line, so a long build-up of SQL breaks the same
    /// way it would outside an <c>EXEC</c>.</para>
    /// <para><b><c>Parameters</c> has to be emitted too.</b> In <c>EXEC (@sql, @p1, @p2)</c> only the
    /// first item is a "string"; everything after the first comma is a positional parameter in a
    /// separate list. Printing the strings alone dropped them silently — a token loss that never
    /// appeared in the corpus and was found by writing the case out by hand. Both lists are merged into
    /// token order and the range is checked end to end, so nothing can go missing again.</para>
    /// </remarks>
    private Doc PrintExecutableStringList(ExecutableStringList list)
    {
        var children = list.Strings
            .Select(s => (TSqlFragment)s)
            .Concat(list.Parameters)
            .Where(c => c.FirstTokenIndex >= 0)
            .OrderBy(c => c.FirstTokenIndex)
            .ToList();

        // The children must account for the whole construct: nothing before the first, nothing after
        // the last. Without this the handler cannot know it has seen everything ScriptDom parsed.
        if (children.Count == 0
            || EffectiveFirstToken(children[0]) != list.FirstTokenIndex
            || children[^1].LastTokenIndex != list.LastTokenIndex)
        {
            return Passthrough(list);
        }

        var parts = new List<Doc> { Print(children[0]) };

        for (var i = 1; i < children.Count; i++)
        {
            switch (TextBetween(children[i - 1], children[i]))
            {
                case ",":
                    parts.Add(ListSeparator());
                    break;

                case "+":
                    parts.Add(Doc.Text(" +"));
                    parts.Add(Doc.Line);
                    break;

                default:
                    return Passthrough(list);
            }

            parts.Add(Print(children[i]));
        }

        return Doc.Group(Doc.Concat(parts));
    }

    /// <summary>
    /// One argument: <c>@a</c>, <c>@a = 1</c>, <c>@a = 1 OUTPUT</c>.
    /// </summary>
    /// <remarks>
    /// Through the shared parts helper because the shape is identical to a variable declaration — a
    /// name, an optional value with <c>=</c> between them, and <c>OUTPUT</c> as a flag with no node of
    /// its own. Whether a bare <c>@sql</c> lands in <c>Variable</c> or <c>ParameterValue</c> does not
    /// matter to it: the parts are sorted by token position, not by which property they came from.
    /// </remarks>
    private Doc PrintExecuteParameter(ExecuteParameter parameter) =>
        PrintKeywordParts(parameter, parameter.Variable, parameter.ParameterValue);
}
