using Maxdop.Core.Printing;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Formatting;

/// <summary>
/// The module definitions other than procedures: <c>CREATE VIEW</c>, <c>CREATE FUNCTION</c> and
/// <c>CREATE TRIGGER</c>, each in its <c>CREATE</c>, <c>ALTER</c> and <c>CREATE OR ALTER</c> forms.
/// </summary>
/// <remarks>
/// <para>These were named in the MVP scope and had no handler, which the corpus
/// hid: the First Responder Kit and Ola Hallengren scripts are stored procedures almost exclusively,
/// so real-world coverage read 99.2% while a file containing a view and a function measured
/// <b>2.5%</b>. A database project is mostly views, functions and triggers, so that gap was the
/// difference between "formats procedures" and "formats a codebase".</para>
/// <para>All three share the layout <see cref="PrintProcedure"/> established — header, options on
/// their own line, <c>AS</c> on its own line, body below — and all three are dispatched on ScriptDom's
/// abstract base, so <c>ALTER</c> and <c>CREATE OR ALTER</c> come along without extra entries.</para>
/// <para>The recurring difficulty is that these headers interleave keyword runs with nodes:
/// <c>RETURNS &lt;type&gt;</c>, <c>WITH &lt;options&gt;</c>, <c>AFTER &lt;actions&gt;</c>,
/// <c>ON &lt;table&gt;</c>. The keyword runs are read from the token stream between the nodes, which
/// is the same strategy as everywhere else here and the reason none of them needs to enumerate the
/// option kinds.</para>
/// </remarks>
public sealed partial class SqlPrinter
{
    // --- CREATE VIEW ------------------------------------------------------------------

    private Doc PrintView(ViewStatementBody view)
    {
        var name = view.SchemaObjectName;
        var select = view.SelectStatement;

        if (name is null || select is null || !SeparatedBy(view.Columns))
        {
            return Passthrough(view);
        }

        // The `AS` introducing the body anchors the layout, exactly as it does for a procedure:
        // everything before it is header, everything after is the query.
        var asIndex = PrecedingKeyword(TSqlTokenType.As, EffectiveFirstToken(select));
        if (asIndex < 0)
        {
            return Passthrough(view);
        }

        var end = RangeEndBeforeTerminators(view);
        var docs = new List<Doc>
        {
            Keywords(view.FirstTokenIndex, name.FirstTokenIndex - 1),
            Doc.Text(" "),
            Print(name),
        };

        // `(a, b)` renames the view's output columns; dropping the list would change every name the
        // view exposes.
        var headerEnd = name.LastTokenIndex;
        if (view.Columns.Count > 0)
        {
            // The closing parenthesis is located rather than assumed to be the token before the
            // qualifiers, the same lesson as the table-valued function reference: `) WITH SCHEMABINDING`
            // sits in that gap, so measuring to the qualifiers would fail the check.
            var closeParen = FirstSignificantToken(view.Columns[^1].LastTokenIndex + 1, asIndex - 1);

            if (SignificantTextBetween(name.LastTokenIndex + 1, view.Columns[0].FirstTokenIndex - 1) != "("
                || closeParen < 0
                || SignificantTextBetween(closeParen, closeParen) != ")")
            {
                return Passthrough(view);
            }

            docs.Add(Doc.Group(Doc.Concat(
                Doc.Text(" ("),
                Doc.Indent(Doc.Concat(Doc.SoftLine, JoinList(view.Columns))),
                Doc.SoftLine,
                Doc.Text(")"))));

            headerEnd = closeParen;
        }

        // `WITH SCHEMABINDING, VIEW_METADATA` — options are nodes, but the `WITH` and the commas are
        // not, so the whole run is read as one slice rather than reassembled.
        if (!AppendQualifiers(docs, headerEnd + 1, asIndex - 1))
        {
            return Passthrough(view);
        }

        // A comment written above the AS documents the view, not the query it wraps. The same move
        // CREATE PROCEDURE and CREATE FUNCTION make, for the same reason; see HoistLeadingBefore.
        var hoistedAs = HoistLeadingBefore(select, asIndex, asIndex);

        docs.Add(Doc.HardLine);

        if (hoistedAs is not null)
        {
            docs.Add(hoistedAs);
        }

        docs.Add(Keyword("AS"));
        docs.Add(Doc.HardLine);
        docs.Add(Print(select));

        // `WITH CHECK OPTION` is a bool with no node, so it can only arrive through the tail.
        if (SignificantTextBetween(select.LastTokenIndex + 1, end).Length > 0)
        {
            docs.Add(Doc.HardLine);
            docs.Add(Keywords(select.LastTokenIndex + 1, end));
        }

        return Doc.Concat(docs);
    }

    // --- CREATE FUNCTION --------------------------------------------------------------

    private Doc PrintFunction(FunctionStatementBody function)
    {
        var name = function.Name;
        var returnType = function.ReturnType;

        // A CLR function (`AS EXTERNAL NAME asm.Class.Method`) has no T-SQL body, so the layout below
        // does not apply.
        if (name is null
            || returnType is null
            || function.MethodSpecifier is not null
            || function.OrderHint is not null
            || !SeparatedBy(function.Parameters))
        {
            return Passthrough(function);
        }

        var end = RangeEndBeforeTerminators(function);
        var docs = new List<Doc>
        {
            Keywords(function.FirstTokenIndex, name.FirstTokenIndex - 1),
            Doc.Text(" "),
            Print(name),
        };

        // A function's parameter list is always parenthesised, unlike a procedure's — including when it
        // is empty, which is why `dbo.f()` needs handling rather than falling into the RETURNS slice.
        var afterParameters = name.LastTokenIndex;
        if (function.Parameters.Count == 0)
        {
            var open = FirstSignificantToken(name.LastTokenIndex + 1, end);
            if (open >= 0 && SignificantTextBetween(open, open) == "(")
            {
                var close = FirstSignificantToken(open + 1, end);
                if (close < 0 || SignificantTextBetween(close, close) != ")")
                {
                    return Passthrough(function);
                }

                docs.Add(Doc.Text("()"));
                afterParameters = close;
            }
        }
        else
        {
            // The closing parenthesis is *located*, not assumed to be the token after the last
            // parameter: a parameter list written across lines puts whitespace there, and taking the
            // next index emitted the parenthesis twice — `))` — which is exactly the defect that five
            // corpus files caught. Third time this assumption has been wrong; nested parentheses in a
            // parameter's own type cannot confuse the search because they are inside its range.
            var closeParen = FirstSignificantToken(function.Parameters[^1].LastTokenIndex + 1, end);

            if (SignificantTextBetween(name.LastTokenIndex + 1, function.Parameters[0].FirstTokenIndex - 1) != "("
                || closeParen < 0
                || SignificantTextBetween(closeParen, closeParen) != ")")
            {
                return Passthrough(function);
            }

            docs.Add(Doc.Group(Doc.Concat(
                Doc.Text(" ("),
                Doc.Indent(Doc.Concat(Doc.SoftLine, JoinList(function.Parameters))),

                // A comment written in front of the `)` belongs to no node the printer dispatches —
                // the attacher hands it to the next one it can find, which is a WITH option past the
                // return type — so this is the only place that can emit it.
                ClaimCommentsIn(function.Parameters[^1].LastTokenIndex + 1, closeParen - 1),
                Doc.SoftLine,
                Doc.Text(")"))));

            afterParameters = closeParen;
        }

        // `RETURNS <type>` for a scalar function, `RETURNS @t TABLE (…)` for a multi-statement
        // table-valued one, `RETURNS TABLE AS RETURN` for an inline one — all read from the gap in
        // front of the return type, so this handler does not need to know which form it has.
        var returnsFrom = afterParameters + 1;
        var returnsTo = EffectiveFirstToken(returnType) - 1;

        if (SignificantTextBetween(returnsFrom, returnsTo).Length == 0)
        {
            return Passthrough(function);
        }

        docs.Add(Doc.HardLine);
        docs.Add(Keywords(returnsFrom, returnsTo));
        docs.Add(Doc.Text(" "));
        docs.Add(Print(returnType));

        // An inline table-valued function's body *is* its return type — `RETURNS TABLE AS RETURN
        // (SELECT …)` — so there is no statement list and nothing more to emit.
        var body = function.StatementList;
        if (body is null || body.Statements.Count == 0)
        {
            return SignificantTextBetween(returnType.LastTokenIndex + 1, end).Length == 0
                ? Doc.Concat(docs)
                : Passthrough(function);
        }

        var asIndex = PrecedingKeyword(TSqlTokenType.As, body.Statements[0].FirstTokenIndex);
        if (asIndex < 0 || !AppendQualifiers(docs, returnType.LastTokenIndex + 1, asIndex - 1))
        {
            return Passthrough(function);
        }

        // A comment written above the AS documents the *function* — `/*Returns a result set that
        // lists all the employees who report to given employee…*/` sits there in ScriptDom's own test
        // script — so it is emitted above the AS rather than below it, where it would read as a note
        // about the body's first statement. The CREATE PROCEDURE handler makes the same move for the
        // same reason; see HoistLeadingBefore. A comment written after the AS is past the keyword and
        // is left alone.
        var hoistedAs = HoistLeadingBefore(body, asIndex, asIndex);

        docs.Add(Doc.HardLine);

        if (hoistedAs is not null)
        {
            docs.Add(hoistedAs);
        }

        docs.Add(Keyword("AS"));
        docs.Add(Doc.HardLine);

        // Through Print, not PrintStatements: a commented-out qualifier between the return type and
        // AS attaches to this StatementList, and bypassing the dispatcher would drop it.
        docs.Add(Print(body));

        return SignificantTextBetween(body.Statements[^1].LastTokenIndex + 1, end).Length == 0
            ? Doc.Concat(docs)
            : Passthrough(function);
    }

    // --- CREATE TRIGGER ---------------------------------------------------------------

    private Doc PrintTrigger(TriggerStatementBody trigger)
    {
        var name = trigger.Name;
        var target = trigger.TriggerObject;
        var body = trigger.StatementList;

        if (name is null
            || target is null
            || body is null
            || body.Statements.Count == 0
            || trigger.MethodSpecifier is not null
            || trigger.TriggerActions.Count == 0
            || !SeparatedBy(trigger.TriggerActions))
        {
            return Passthrough(trigger);
        }

        var asIndex = PrecedingKeyword(TSqlTokenType.As, body.Statements[0].FirstTokenIndex);
        if (asIndex < 0)
        {
            return Passthrough(trigger);
        }

        var end = RangeEndBeforeTerminators(trigger);
        var docs = new List<Doc>
        {
            Keywords(trigger.FirstTokenIndex, name.FirstTokenIndex - 1),
            Doc.Text(" "),
            Print(name),

            // `ON dbo.t`, or `ON DATABASE` / `ON ALL SERVER` for a DDL trigger — the scope is an enum,
            // so the keyword run in front of the target is what distinguishes them.
            Doc.HardLine,
            Keywords(name.LastTokenIndex + 1, EffectiveFirstToken(target) - 1),
            Doc.Text(" "),
            Print(target),
        };

        // Everything between the target and the first action in one slice: any `WITH ENCRYPTION`
        // qualifier plus the `AFTER` / `INSTEAD OF` / `FOR` that introduces the actions. Emitted
        // together rather than split, because the trigger type is an enum with no token range and
        // splitting the run would mean locating where the options stop — needless when the whole run
        // reads correctly on one line.
        //
        // Grouped, so `AFTER INSERT, UPDATE` stays on one line rather than breaking at the comma.
        var actionsFrom = target.LastTokenIndex + 1;
        var actionsTo = EffectiveFirstToken(trigger.TriggerActions[0]) - 1;

        if (SignificantTextBetween(actionsFrom, actionsTo).Length == 0)
        {
            return Passthrough(trigger);
        }

        docs.Add(Doc.HardLine);
        docs.Add(Doc.Group(Doc.Concat(
            Keywords(actionsFrom, actionsTo),
            Doc.Text(" "),
            JoinList(trigger.TriggerActions))));

        // `NOT FOR REPLICATION` is a bool with no node and sits after the actions.
        if (!AppendQualifiers(docs, trigger.TriggerActions[^1].LastTokenIndex + 1, asIndex - 1))
        {
            return Passthrough(trigger);
        }

        // A comment written above the AS documents the trigger, not the body's first statement. The same move
        // CREATE PROCEDURE and CREATE FUNCTION make, for the same reason; see HoistLeadingBefore.
        var hoistedAs = HoistLeadingBefore(body, asIndex, asIndex);

        docs.Add(Doc.HardLine);

        if (hoistedAs is not null)
        {
            docs.Add(hoistedAs);
        }

        docs.Add(Keyword("AS"));
        docs.Add(Doc.HardLine);
        docs.Add(Print(body));

        return SignificantTextBetween(body.Statements[^1].LastTokenIndex + 1, end).Length == 0
            ? Doc.Concat(docs)
            : Passthrough(trigger);
    }

    // --- function return types and trigger actions -------------------------------------

    /// <summary><c>RETURNS DECIMAL(18, 2)</c> — the type of a scalar function.</summary>
    /// <remarks>
    /// A transparent wrapper whose range is exactly its data type's, so forwarding it is what lets a
    /// built-in return type be recased along with every other one.
    /// </remarks>
    private Doc PrintScalarFunctionReturnType(ScalarFunctionReturnType type) =>
        PrintCallTarget(type, type.DataType);

    /// <summary><c>RETURNS @r TABLE (a INT)</c> — a multi-statement table-valued function.</summary>
    /// <remarks>
    /// The body of this is a <c>DeclareTableVariableBody</c>, the same node <c>DECLARE @t TABLE</c>
    /// uses, so the table gets the identical layout for free.
    /// </remarks>
    private Doc PrintTableValuedFunctionReturnType(TableValuedFunctionReturnType type) =>
        PrintCallTarget(type, type.DeclareTableVariableBody);

    /// <summary><c>RETURNS TABLE AS RETURN (SELECT …)</c> — an inline table-valued function.</summary>
    /// <remarks>
    /// The whole body of the function, which without a handler came out verbatim — the query inside an
    /// inline table-valued function is exactly the part worth formatting.
    /// </remarks>
    /// <remarks>
    /// Whether the parentheses belong to the query or to this node depends on the query. A plain
    /// <c>RETURN (SELECT …)</c> parses the parenthesised query as a <c>QueryParenthesisExpression</c>,
    /// so the ranges coincide and forwarding is right. But
    /// <c>RETURN (WITH XMLNAMESPACES (…) SELECT …)</c> puts the statement's own range at the
    /// <c>WITH</c>, <em>inside</em> the parentheses — so forwarding dropped them and the output stopped
    /// parsing. Both shapes are handled rather than assumed.
    /// </remarks>
    private Doc PrintSelectFunctionReturnType(SelectFunctionReturnType type)
    {
        var select = type.SelectStatement;
        if (select is null)
        {
            return Passthrough(type);
        }

        if (EffectiveFirstToken(select) == type.FirstTokenIndex && select.LastTokenIndex == type.LastTokenIndex)
        {
            return Print(select);
        }

        return IsPlainParenthesised(type, select)
            ? Doc.Group(Doc.Concat(
                Doc.Text("("),
                Doc.Indent(Doc.Concat(Doc.HardLine, Print(select))),
                Doc.HardLine,
                Doc.Text(")")))
            : Passthrough(type);
    }

    /// <summary>
    /// A trigger's action: <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c> for a DML trigger, or an event
    /// type or group — <c>CREATE_TABLE</c>, <c>DDL_LOGIN_EVENTS</c> — for a DDL one.
    /// </summary>
    /// <remarks>
    /// Every form is a keyword position. A DML action is the <c>TriggerActionType</c> enum; a DDL one is
    /// an <c>EventTypeContainer</c> or <c>EventGroupContainer</c> holding the <c>EventType</c> or
    /// <c>EventGroup</c> enum. The parser has resolved the word to an enum member in all four cases, so
    /// there is no way for an object name to reach here — which is what makes recasing the whole range
    /// provable rather than a guess about spelling.
    /// <para>The event containers are deliberately not dispatched to: the action's range *is* the
    /// container's range, so slicing it covers them.</para>
    /// </remarks>
    private Doc PrintTriggerAction(TriggerAction action) =>
        Keywords(action.FirstTokenIndex, action.LastTokenIndex);

    /// <summary>
    /// What a trigger is attached to: <c>ON dbo.t</c>, or <c>ON DATABASE</c> / <c>ON ALL SERVER</c>.
    /// </summary>
    /// <remarks>
    /// The two cases are distinguished by <c>Name</c>, not by inspecting text. When it is absent the
    /// range holds the scope keywords, and <c>TriggerScope</c> has already told the parser which they
    /// are — so <c>on database</c> becomes <c>ON DATABASE</c>. When it is present the range is the
    /// object name and is printed, never recased.
    /// </remarks>
    private Doc PrintTriggerObject(TriggerObject target)
    {
        if (target.Name is null)
        {
            return Keywords(target.FirstTokenIndex, target.LastTokenIndex);
        }

        return target.Name.FirstTokenIndex == target.FirstTokenIndex
            && target.Name.LastTokenIndex == target.LastTokenIndex
                ? Print(target.Name)
                : Passthrough(target);
    }

    /// <summary>
    /// Appends a header qualifier run — <c>WITH SCHEMABINDING</c>, <c>WITH ENCRYPTION</c>,
    /// <c>NOT FOR REPLICATION</c> — on its own line, or nothing when the region is empty.
    /// </summary>
    /// <remarks>
    /// Read as one slice rather than reassembled from the option nodes, for the reason the procedure
    /// handler learned the hard way: <c>ExecuteAsProcedureOption</c>'s token range does not cover its
    /// principal, so rebuilding <c>WITH EXECUTE AS CALLER</c> from nodes produced
    /// <c>WITH EXECUTE AS</c> and output that no longer parsed. Anchoring on tokens removes that whole
    /// class of mistake — there is no option kind a slice can fail to reproduce.
    /// <para>A keyword position despite appearing to name something: T-SQL spells a module's
    /// <c>EXECUTE AS</c> principal as <c>CALLER</c>, <c>SELF</c>, <c>OWNER</c> or a <em>string
    /// literal</em>, never a bare identifier — and <see cref="Keywords"/> does not touch literals. So
    /// <c>with schemabinding</c> becomes <c>WITH SCHEMABINDING</c> while
    /// <c>EXECUTE AS 'domain\user'</c> keeps its exact text.</para>
    /// </remarks>
    private bool AppendQualifiers(List<Doc> docs, int fromIndex, int toIndex)
    {
        if (SignificantTextBetween(fromIndex, toIndex).Length == 0)
        {
            return true;
        }

        docs.Add(Doc.HardLine);
        docs.Add(Keywords(fromIndex, toIndex));
        return true;
    }

}
