using Maxdop.Core.Printing;
using Maxdop.Core.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Formatting;

/// <summary>
/// The constructs that <em>look</em> like function calls but each have their own ScriptDom node
/// type — <c>NULLIF</c>, <c>COALESCE</c>, <c>LEFT</c>, <c>RIGHT</c>, <c>IIF</c> — plus the call
/// targets in front of a method call.
/// </summary>
/// <remarks>
/// Picked from corpus data: together these were 25% of the text still left verbatim in
/// real-world scripts, and they are one shape wearing five names. ScriptDom gives a distinct type to
/// every construct the grammar spells out, so <c>COALESCE(a, b)</c> is not a <c>FunctionCall</c> and
/// gets no benefit from that handler — but all of them are a keyword, an open parenthesis, a
/// comma-separated list and a close parenthesis, which is exactly one helper.
/// <para>The keyword is read from the tokens rather than written out, because the family is split
/// down the middle: <c>NULLIF</c>, <c>COALESCE</c>, <c>LEFT</c> and <c>RIGHT</c> have token types of
/// their own while <c>IIF</c> lexes as an identifier and must keep the author's casing.</para>
/// </remarks>
public sealed partial class SqlPrinter
{
    // --- keyword calls ----------------------------------------------------------------

    private Doc PrintNullIf(NullIfExpression expression) =>
        PrintKeywordCall(expression, [expression.FirstExpression, expression.SecondExpression], "NULLIF");

    private Doc PrintCoalesce(CoalesceExpression expression) =>
        PrintKeywordCall(expression, [.. expression.Expressions], "COALESCE");

    private Doc PrintLeftFunction(LeftFunctionCall call) =>
        PrintKeywordCall(call, [.. call.Parameters], "LEFT");

    private Doc PrintRightFunction(RightFunctionCall call) =>
        PrintKeywordCall(call, [.. call.Parameters], "RIGHT");

    private Doc PrintIif(IIfCall call) =>
        PrintKeywordCall(call, [call.Predicate, call.ThenExpression, call.ElseExpression], "IIF");

    /// <summary>
    /// The name of an unqualified call — <c>GETDATE</c> in <c>getdate()</c> — recased when it is a
    /// built-in and <see cref="FormatOptions.RecaseBuiltInFunctions"/> is on.
    /// </summary>
    /// <remarks>
    /// The only keyword position in the printer whose proof is a vocabulary rather than the parse
    /// tree, because ScriptDom gives <c>GETDATE()</c> and <c>dbo.MyFunc()</c> the same node with the
    /// same <c>Identifier</c> inside it. See <see cref="SqlBuiltInFunctions"/> for why the vocabulary
    /// is nonetheless close to a proof, and <see cref="FormatOptions.RecaseBuiltInFunctions"/> for
    /// why the residue is a switch instead of a refusal.
    /// <para>Two conditions beyond membership, and both are load-bearing. The caller supplies the
    /// first — only a call with <em>no call target</em> reaches here, so <c>dbo.Len(x)</c> keeps the
    /// author's casing. The second is the quoting: <c>[len](x)</c> is left alone, because a delimited
    /// name is the one way an author can say "this identifier is spelled exactly like this", and
    /// <see cref="Slice"/> would decline to recase the token anyway.</para>
    /// </remarks>
    private Doc PrintFunctionName(Identifier name) =>
        _options.RecaseBuiltInFunctions
        && name.QuoteType == QuoteType.NotQuoted
        && SqlBuiltInFunctions.Contains(name.Value)
            ? WithComments(name, Keywords(name.FirstTokenIndex, name.LastTokenIndex))
            : Print(name);

    /// <summary>
    /// A system global variable used as an expression: <c>@@ROWCOUNT</c>, <c>@@FETCH_STATUS</c>.
    /// </summary>
    /// <remarks>
    /// The one handler written for a node that <see cref="IsVerbatimByDesign"/> calls verbatim, and it
    /// exists for casing alone — there is no layout inside a single token. Grouped with the built-in
    /// functions because it is the same kind of decision and rides the same switch: Microsoft
    /// documents these as built-in functions, and the proof available for them is a vocabulary rather
    /// than the parse tree.
    /// <para><b>The <c>@@</c> is not what licenses the recase.</b> <c>DECLARE @@MyVar INT</c> is legal
    /// T-SQL, and ScriptDom hands back a <c>GlobalVariableExpression</c> for a later <c>@@MyVar</c> in
    /// an expression position exactly as it does for <c>@@ROWCOUNT</c> — it resolves by spelling, not
    /// by scope. So the name is checked against <see cref="SqlGlobalVariables"/>, and a variable that
    /// is merely spelled like a system one keeps every character the author wrote.</para>
    /// </remarks>
    private Doc PrintGlobalVariable(GlobalVariableExpression global) =>
        _options.RecaseBuiltInFunctions && SqlGlobalVariables.Contains(global.Name)
            ? Keywords(global.FirstTokenIndex, global.LastTokenIndex)
            : Passthrough(global);

    /// <summary>
    /// <c>KEYWORD(a, b, …)</c> — the keyword and parentheses read from the token stream, the
    /// arguments laid out like any other argument list.
    /// </summary>
    /// <remarks>
    /// Both ends are verified rather than assumed, the same discipline as everywhere else here: the
    /// head must be exactly the expected keyword and an open parenthesis, the tail exactly a close
    /// parenthesis, and the arguments comma-separated. Anything else means a spelling this does not
    /// model — and since <see cref="SeparatedBy"/> checks the gaps <em>between</em> arguments too,
    /// a keyword separator like <c>TRIM('x' FROM y)</c> cannot be silently rewritten as a comma.
    /// <para>Close cousin of <c>PrintRaiseError</c>, deliberately not merged with it: a
    /// <c>RAISERROR</c> tail can continue past the parenthesis into <c>WITH NOWAIT</c>, and folding
    /// that possibility in here would make both harder to read than the six shared lines are worth.
    /// </para>
    /// </remarks>
    private Doc PrintKeywordCall(TSqlFragment node, TSqlFragment?[] arguments, string keyword)
    {
        if (Array.Exists(arguments, a => a is null))
        {
            return Passthrough(node);
        }

        var present = Array.ConvertAll(arguments, a => a!);
        if (present.Length == 0 || !SeparatedBy(present))
        {
            return Passthrough(node);
        }

        var headEnd = EffectiveFirstToken(present[0]) - 1;
        var tailStart = present[^1].LastTokenIndex + 1;

        if (!Compact(SignificantTextBetween(node.FirstTokenIndex, headEnd))
                .Equals(keyword + "(", StringComparison.OrdinalIgnoreCase)
            || SignificantTextBetween(tailStart, node.LastTokenIndex) != ")")
        {
            return Passthrough(node);
        }

        return Doc.Group(Doc.Concat(
            // `IIF(` lexes as an identifier while `COALESCE(` and `NULLIF(` do not; the region is the
            // call's own keyword and its parenthesis either way, so both are recased.
            Keywords(node.FirstTokenIndex, headEnd),
            Doc.Indent(Doc.Concat(
                Doc.SoftLine,
                Doc.Join(Doc.Concat(Doc.Text(","), Doc.Line), present.Select(Print)))),
            Doc.SoftLine,
            Doc.Text(")")));
    }

    // --- call targets -----------------------------------------------------------------

    /// <summary>
    /// The thing to the left of the dot in a method call: <c>col.value(…)</c>,
    /// <c>CONVERT(XML, d).value(…)</c>.
    /// </summary>
    /// <remarks>
    /// A transparent wrapper — its range coincides exactly with the child's, and the separating dot
    /// lives in the gap that <see cref="PrintFunctionCall"/> already reads. Worth a handler anyway,
    /// and by some distance the cheapest one in the corpus: <c>ExpressionCallTarget</c> was the
    /// single largest remaining gap in real-world scripts at 14% of all verbatim text, because
    /// passthrough is subtree-scoped and XML shredding puts a whole <c>CONVERT(XML, …)</c> — or a
    /// subquery — in the target position. Nineteen thousand tokens were frozen behind a node that
    /// does nothing but hold another node.
    /// <para>The range check is what makes it safe: if the wrapper ever covered more than its child,
    /// forwarding would drop whatever the difference was.</para>
    /// </remarks>
    private Doc PrintCallTarget(TSqlFragment target, TSqlFragment? child) =>
        child is not null
        && EffectiveFirstToken(child) == target.FirstTokenIndex
        && child.LastTokenIndex == target.LastTokenIndex
            ? Print(child)
            : Passthrough(target);

    // --- ordered-set aggregates ----------------------------------------------------------

    /// <summary>
    /// <c>WITHIN GROUP (ORDER BY a DESC)</c> — the ordering an ordered-set aggregate is computed over.
    /// </summary>
    /// <remarks>
    /// Written for casing rather than for layout. The clause used to reach the output through the
    /// slice that <see cref="PrintFunctionCall"/> takes after the closing parenthesis, and that slice
    /// recases identifiers nowhere, because a <c>COLLATE Latin1_General_BIN</c> can sit in the same
    /// region and its collation name is a name. So <c>GROUP</c> recased — it is reserved and lexes as
    /// its own token — while <c>WITHIN</c>, which is not reserved and lexes as an identifier, did not:
    /// <c>within GROUP (ORDER BY b)</c>, one clause in two cases.
    /// <para>The fix is the usual one here, and it is structural rather than a spelling rule:
    /// <c>WithinGroupClause</c> is a node, so its head is a region the grammar guarantees holds no
    /// name and <see cref="Keywords"/> may have it. The <c>COLLATE</c> that shares the old region is
    /// not a node in the same way and keeps exactly the treatment it had.</para>
    /// <para>The graph form — <c>WITHIN GROUP (GRAPH PATH)</c>, which carries no <c>ORDER BY</c> — is
    /// declined rather than modelled. It is a different construct wearing the same two words.</para>
    /// </remarks>
    private Doc PrintWithinGroup(WithinGroupClause clause)
    {
        // A comment anywhere inside sends the whole clause back as written. The words `WITHIN GROUP (`
        // are emitted as one run, and a comment written between two of them has nowhere to go but the
        // end of it — which is a move, and the comment fuzzer counts moves. Passthrough keeps the
        // comment exactly where the author put it, at the cost of leaving this one clause's casing
        // alone, which is the right trade at this frequency.
        if (clause.HasGraphPath
            || clause.OrderByClause is null
            || !NoCommentsIn(clause.FirstTokenIndex, clause.LastTokenIndex))
        {
            return Passthrough(clause);
        }

        var orderBy = clause.OrderByClause;
        var headEnd = EffectiveFirstToken(orderBy) - 1;

        // Both ends verified, as everywhere else: the head must be exactly the two words and the
        // parenthesis, the tail exactly the closing one.
        if (!Compact(SignificantTextBetween(clause.FirstTokenIndex, headEnd))
                .Equals("WITHINGROUP(", StringComparison.OrdinalIgnoreCase)
            || SignificantTextBetween(orderBy.LastTokenIndex + 1, clause.LastTokenIndex) != ")")
        {
            return Passthrough(clause);
        }

        return Doc.Group(Doc.Concat(
            Keywords(clause.FirstTokenIndex, headEnd),
            Doc.Indent(Doc.Concat(Doc.SoftLine, Print(orderBy))),
            Doc.SoftLine,
            Doc.Text(")")));
    }

    // --- window functions --------------------------------------------------------------

    /// <summary>
    /// <c>OVER (PARTITION BY a, b ORDER BY c DESC ROWS UNBOUNDED PRECEDING)</c>
    /// </summary>
    /// <remarks>
    /// The clause that makes a window function a window function, and the reason
    /// <see cref="PrintFunctionCall"/> declined every one of them until now. Its own <c>OVER</c>, the
    /// parentheses, and the words <c>PARTITION BY</c> all belong to no node, so the opening run is read
    /// as a slice up to the first thing that <em>is</em> one; the ordering and framing clauses print
    /// themselves.
    /// <para><c>OVER w</c>, the named-window form, has a different shape and is deferred.</para>
    /// </remarks>
    private Doc PrintOverClause(OverClause over)
    {
        if (over.WindowName is not null || !SeparatedBy(over.Partitions))
        {
            return Passthrough(over);
        }

        // Partitions first, then whichever of the ordering and framing clauses are present, in the
        // order they were written.
        var clauses = new TSqlFragment?[] { over.OrderByClause, over.WindowFrameClause }
            .Where(c => c is not null && c.FirstTokenIndex >= 0)
            .Select(c => c!)
            .OrderBy(c => c.FirstTokenIndex)
            .ToList();

        // `OVER ()` — an empty window, which is legal and means "the whole result set".
        if (over.Partitions.Count == 0 && clauses.Count == 0)
        {
            return Compact(SignificantTextBetween(over.FirstTokenIndex, over.LastTokenIndex))
                .Equals("OVER()", StringComparison.OrdinalIgnoreCase)
                ? Keywords(over.FirstTokenIndex, over.LastTokenIndex)
                : Passthrough(over);
        }

        var first = over.Partitions.Count > 0 ? (TSqlFragment)over.Partitions[0] : clauses[0];
        var last = clauses.Count > 0 ? clauses[^1] : over.Partitions[^1];
        var headEnd = EffectiveFirstToken(first) - 1;

        // The head must introduce the clause and the tail must close it; without both checks a
        // construct this does not model could sit at either end and be dropped.
        // Measured from the clause's own end, except for a framing clause last, whose range stops short
        // of its trailing `ROW` — there the only thing that must remain is the closing parenthesis
        // itself. Without that distinction every windowed running total was declined.
        var tailFrom = ReferenceEquals(last, over.WindowFrameClause)
            ? over.LastTokenIndex
            : last.LastTokenIndex + 1;

        if (!SignificantTextBetween(over.FirstTokenIndex, headEnd)
                .StartsWith("OVER", StringComparison.OrdinalIgnoreCase)
            || SignificantTextBetween(tailFrom, over.LastTokenIndex) != ")")
        {
            return Passthrough(over);
        }

        // The window's own parenthesis, so the clauses inside it can be laid out as a list. Without
        // it the head would be the single slice `OVER (PARTITION BY`, which cannot be broken between
        // the paren and the keyword.
        var open = FirstTokenOfType(TSqlTokenType.LeftParenthesis, over.FirstTokenIndex, headEnd);
        if (open < 0)
        {
            return Passthrough(over);
        }

        // One entry per clause — `PARTITION BY …`, `ORDER BY …`, the frame — joined by a line so the
        // window breaks at its own clause boundaries.
        //
        // This is the layout decision, and it is about *where the pressure goes*. Joined by hard
        // spaces the window had no break of its own, so an overlong line could only be relieved by
        // the innermost group that did have one — the ORDER BY list — which broke after `ORDER BY`
        // and left the frame dangling on the next line. Breaking the outer construct first and
        // leaving the inner ones flat is the right order, and it is what a group of `Doc.Line`s
        // buys.
        var parts = new List<Doc>();

        if (over.Partitions.Count > 0)
        {
            // Its own group, and the same shape as ORDER BY and GROUP BY: without the group the
            // list's separators sit directly inside the window's group and break whenever it does,
            // so a two-column PARTITION BY split across lines merely because the frame was long.
            parts.Add(Doc.Group(Doc.Concat(
                Keywords(open + 1, headEnd),
                Doc.Indent(Doc.Concat(
                    Doc.Line,
                    JoinList(over.Partitions))))));
        }

        foreach (var clause in clauses)
        {
            // The framing clause is emitted as a slice rather than Printed, because its range stops
            // short of tokens it owns: `ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW` ends the range
            // at `CURRENT`, leaving the `ROW` outside it — and the delimiter nodes inside do the same.
            // Slicing to just before the clause's closing parenthesis covers all of it.
            //
            // It stays atomic by choice as well as by necessity: a frame is a fixed idiom read as one
            // unit, and splitting `BETWEEN … AND …` across lines makes the AND look like a boolean
            // operator joining predicates.
            //
            // Recased, and provably so: every word here is fixed by WindowFrameType and
            // WindowDelimiterType, and an offset is a numeric literal, which Keywords leaves alone. No
            // object name can appear in a window frame.
            // …and because it is sliced rather than Printed, nothing else would emit the comments
            // attached to it. The slice therefore starts at the first of them rather than at the
            // clause, which brings them inside its range and lets it carry them — otherwise a comment
            // written above `ROWS BETWEEN …` was dropped outright and the file refused.
            parts.Add(ReferenceEquals(clause, over.WindowFrameClause)
                ? Keywords(SliceStartCoveringComments(clause), over.LastTokenIndex - 1)
                : Print(clause));
        }

        return Doc.Group(Doc.Concat(
            Keywords(over.FirstTokenIndex, open),
            Doc.Indent(Doc.Concat(Doc.SoftLine, Doc.Join(Doc.Line, parts))),
            Doc.SoftLine,
            Doc.Text(")")));
    }

    // --- table-valued functions --------------------------------------------------------

    /// <summary><c>FROM dbo.fn(1, 2) AS f (c1, c2)</c></summary>
    /// <remarks>
    /// A table-valued function in a FROM clause. Same shape as a scalar call plus the alias and
    /// optional column-rename list a derived table can carry — and the column list is the part worth
    /// being careful about, since dropping it silently renames nothing and changes every reference.
    /// </remarks>
    private Doc PrintSchemaObjectFunctionTable(SchemaObjectFunctionTableReference table)
    {
        var name = table.SchemaObject;

        return name is null
            ? Passthrough(table)
            : PrintCallableTable(
                table,
                Print(name),
                name.LastTokenIndex,
                [.. table.Parameters],
                [.. table.Columns]);
    }

    /// <summary>
    /// A table source that is a call: <c>dbo.fn(1) AS f</c>, <c>STRING_SPLIT(@s, ',')</c>,
    /// <c>::fn_helpcollations()</c>, <c>@x.nodes('/p') AS n(c)</c>.
    /// </summary>
    /// <remarks>
    /// ScriptDom gives each of these its own node type — <c>SchemaObjectFunctionTableReference</c>,
    /// <c>GlobalFunctionTableReference</c>, <c>BuiltInFunctionTableReference</c>,
    /// <c>VariableMethodCallTableReference</c> — with no common base beyond
    /// <c>TableReferenceWithAlias</c>, because what precedes the parenthesis differs in each. Everything
    /// after it does not: an argument list, an optional alias with an optional <c>AS</c>, an optional
    /// column list. So the callee is printed by the caller, which knows what it is, and the rest is
    /// shared.
    /// </remarks>
    private Doc PrintCallableTable(
        TableReferenceWithAlias table,
        Doc callee,
        int calleeEnd,
        IList<TSqlFragment> parameters,
        IList<TSqlFragment> columns)
    {
        if (!SeparatedBy(parameters) || !SeparatedBy(columns))
        {
            return Passthrough(table);
        }

        var argsFrom = calleeEnd + 1;

        // The call's closing parenthesis has to be *located*, not assumed to be the last token before
        // the alias: `dbo.fn(1) AS f` puts `) AS` in that gap, so measuring to the alias made every
        // aliased table-valued function fail its own separator check. Nested parentheses cannot
        // confuse the search because they live inside a parameter's range.
        var afterArguments = parameters.Count > 0
            ? parameters[^1].LastTokenIndex + 1
            : FirstSignificantToken(argsFrom, table.LastTokenIndex) + 1;

        var callEnd = FirstSignificantToken(afterArguments, table.LastTokenIndex);
        if (callEnd < 0 || SignificantTextBetween(callEnd, callEnd) != ")")
        {
            return Passthrough(table);
        }

        var docs = new List<Doc> { callee };

        if (parameters.Count == 0)
        {
            if (Compact(SignificantTextBetween(argsFrom, callEnd)) != "()")
            {
                return Passthrough(table);
            }

            docs.Add(Doc.Text("()"));
        }
        else
        {
            if (SignificantTextBetween(argsFrom, EffectiveFirstToken(parameters[0]) - 1) != "(")
            {
                return Passthrough(table);
            }

            docs.Add(Doc.Group(Doc.Concat(
                Doc.Text("("),
                Doc.Indent(Doc.Concat(
                    Doc.SoftLine,
                    Doc.Join(Doc.Concat(Doc.Text(","), Doc.Line), parameters.Select(Print)))),
                Doc.SoftLine,
                Doc.Text(")"))));
        }

        if (table.Alias is not null)
        {
            docs.Add(Doc.Text(" "));

            // `AS` is optional; cased per token because a hint or an alias must keep its own casing.
            if (SignificantTextBetween(callEnd + 1, table.Alias.FirstTokenIndex - 1).Length > 0)
            {
                docs.Add(CasedTokens(callEnd + 1, table.Alias.FirstTokenIndex - 1));
                docs.Add(Doc.Text(" "));
            }

            docs.Add(Print(table.Alias));
        }

        if (columns.Count > 0)
        {
            var columnsFrom = (table.Alias?.LastTokenIndex ?? callEnd) + 1;
            if (SignificantTextBetween(columnsFrom, columns[0].FirstTokenIndex - 1) != "("
                || SignificantTextBetween(columns[^1].LastTokenIndex + 1, table.LastTokenIndex) != ")")
            {
                return Passthrough(table);
            }

            docs.Add(Doc.Group(Doc.Concat(
                Doc.Text(" ("),
                Doc.Indent(Doc.Concat(Doc.SoftLine, JoinList(columns))),
                Doc.SoftLine,
                Doc.Text(")"))));
        }
        else if (SignificantTextBetween((table.Alias?.LastTokenIndex ?? callEnd) + 1, table.LastTokenIndex).Length > 0)
        {
            // Anything else after the alias — a hint, a temporal clause — is not modelled here.
            return Passthrough(table);
        }

        return Doc.Concat(docs);
    }

    /// <summary>
    /// <c>&lt;source&gt; PIVOT (SUM(x) FOR y IN ([a], [b])) AS p</c>.
    /// </summary>
    /// <remarks>
    /// Deferred since the SELECT work and reached now because the hand-written construct corpus put it
    /// at the top of the plain-SQL gap list. Five of the six parts are nodes; everything between them —
    /// <c>PIVOT (</c>, the aggregate's own parentheses, <c>FOR</c>, <c>IN (</c>, the two closing
    /// parentheses — belongs to no node, so each gap is verified against the grammar and then emitted as
    /// a slice.
    /// <para>The aggregate name is Printed, not recased: <c>AggregateFunctionIdentifier</c> is a
    /// multi-part identifier, so it can name a CLR user-defined aggregate rather than <c>SUM</c>.</para>
    /// </remarks>
    private Doc PrintPivotedTable(PivotedTableReference table)
    {
        var source = table.TableReference;
        var aggregate = table.AggregateFunctionIdentifier;
        var pivotColumn = table.PivotColumn;

        if (source is null
            || aggregate is null
            || pivotColumn is null
            || table.ValueColumns.Count == 0
            || table.InColumns.Count == 0
            || !SeparatedBy(table.ValueColumns)
            || !SeparatedBy(table.InColumns))
        {
            return Passthrough(table);
        }

        var beforeAggregate = SignificantTextBetween(source.LastTokenIndex + 1, EffectiveFirstToken(aggregate) - 1);
        var beforeValues = SignificantTextBetween(aggregate.LastTokenIndex + 1, EffectiveFirstToken(table.ValueColumns[0]) - 1);
        var beforePivot = SignificantTextBetween(table.ValueColumns[^1].LastTokenIndex + 1, EffectiveFirstToken(pivotColumn) - 1);
        var beforeIn = SignificantTextBetween(pivotColumn.LastTokenIndex + 1, EffectiveFirstToken(table.InColumns[0]) - 1);
        var afterIn = table.InColumns[^1].LastTokenIndex + 1;

        if (!Compact(beforeAggregate).Equals("PIVOT(", StringComparison.OrdinalIgnoreCase)
            || beforeValues != "("
            || !Compact(beforePivot).Equals(")FOR", StringComparison.OrdinalIgnoreCase)
            || !Compact(beforeIn).Equals("IN(", StringComparison.OrdinalIgnoreCase))
        {
            return Passthrough(table);
        }

        var docs = new List<Doc>
        {
            Print(source),
            Doc.Line,
            Keywords(source.LastTokenIndex + 1, EffectiveFirstToken(aggregate) - 1),
            Print(aggregate),
            Doc.Text("("),
            JoinList(table.ValueColumns),

            // The slice is `) FOR` — the parenthesis closing the aggregate is *in the gap*, so emitting
            // one here as well produced `SUM(x) ) FOR` and the output stopped parsing.
            Keywords(table.ValueColumns[^1].LastTokenIndex + 1, EffectiveFirstToken(pivotColumn) - 1),
            Doc.Text(" "),
            Print(pivotColumn),
            Doc.Text(" "),
            Keywords(pivotColumn.LastTokenIndex + 1, EffectiveFirstToken(table.InColumns[0]) - 1),
            Doc.Group(Doc.Concat(
                Doc.Indent(Doc.Concat(Doc.SoftLine, JoinList(table.InColumns))),
                Doc.SoftLine)),
        };

        return TryAppendPivotTail(docs, table, afterIn, "))") ? Doc.Group(Doc.Concat(docs)) : Passthrough(table);
    }

    /// <summary>
    /// <c>&lt;source&gt; UNPIVOT (value FOR column IN ([a], [b])) AS u</c>.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="PrintPivotedTable"/> with one operand fewer: there is no aggregate,
    /// so the value column stands alone and the run reads <c>UNPIVOT (value FOR column IN (…))</c>.
    /// </remarks>
    private Doc PrintUnpivotedTable(UnpivotedTableReference table)
    {
        var source = table.TableReference;
        var valueColumn = table.ValueColumn;
        var pivotColumn = table.PivotColumn;

        if (source is null
            || valueColumn is null
            || pivotColumn is null
            || table.InColumns.Count == 0
            || !SeparatedBy(table.InColumns))
        {
            return Passthrough(table);
        }

        var beforeValue = SignificantTextBetween(source.LastTokenIndex + 1, EffectiveFirstToken(valueColumn) - 1);
        var beforePivot = SignificantTextBetween(valueColumn.LastTokenIndex + 1, EffectiveFirstToken(pivotColumn) - 1);
        var beforeIn = SignificantTextBetween(pivotColumn.LastTokenIndex + 1, EffectiveFirstToken(table.InColumns[0]) - 1);

        if (!Compact(beforeValue).Equals("UNPIVOT(", StringComparison.OrdinalIgnoreCase)
            || !beforePivot.Equals("FOR", StringComparison.OrdinalIgnoreCase)
            || !Compact(beforeIn).Equals("IN(", StringComparison.OrdinalIgnoreCase))
        {
            return Passthrough(table);
        }

        var docs = new List<Doc>
        {
            Print(source),
            Doc.Line,
            Keywords(source.LastTokenIndex + 1, EffectiveFirstToken(valueColumn) - 1),
            Print(valueColumn),
            Doc.Text(" "),
            Keywords(valueColumn.LastTokenIndex + 1, EffectiveFirstToken(pivotColumn) - 1),
            Doc.Text(" "),
            Print(pivotColumn),
            Doc.Text(" "),
            Keywords(pivotColumn.LastTokenIndex + 1, EffectiveFirstToken(table.InColumns[0]) - 1),
            Doc.Group(Doc.Concat(
                Doc.Indent(Doc.Concat(Doc.SoftLine, JoinList(table.InColumns))),
                Doc.SoftLine)),
        };

        return TryAppendPivotTail(docs, table, table.InColumns[^1].LastTokenIndex + 1, "))")
            ? Doc.Group(Doc.Concat(docs))
            : Passthrough(table);
    }

    /// <summary>
    /// Appends the closing parentheses and the alias shared by <c>PIVOT</c> and <c>UNPIVOT</c>, and
    /// reports whether everything in the node's range was accounted for.
    /// </summary>
    /// <remarks>
    /// The alias is mandatory in both, and the <c>AS</c> in front of it optional — so the run between the
    /// parentheses and the alias is read rather than written out.
    /// </remarks>
    private bool TryAppendPivotTail(List<Doc> docs, TableReferenceWithAlias table, int from, string closing)
    {
        var alias = table.Alias;

        if (alias is null)
        {
            return false;
        }

        // Compared case-insensitively. `as p` is as valid as `AS p`, and comparing a keyword slice with
        // `!=` against an upper-case literal is a mistake this codebase has now made four times — each
        // time silently sending a construct to passthrough rather than breaking anything visibly.
        var beforeAlias = Compact(SignificantTextBetween(from, alias.FirstTokenIndex - 1));
        var hasAs = beforeAlias.Equals(closing + "AS", StringComparison.OrdinalIgnoreCase);

        if (!hasAs && !beforeAlias.Equals(closing, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        docs.Add(Doc.Text(closing));
        docs.Add(Doc.Text(" "));

        if (hasAs)
        {
            docs.Add(Keyword("AS"));
            docs.Add(Doc.Text(" "));
        }

        docs.Add(Print(alias));

        return SignificantTextBetween(alias.LastTokenIndex + 1, table.LastTokenIndex).Length == 0;
    }

    /// <summary>A built-in table-valued function: <c>STRING_SPLIT(@s, ',')</c>, <c>OPENQUERY(…)</c>.</summary>
    /// <remarks>
    /// Recasing the name here needs no vocabulary: <c>GlobalFunctionTableReference</c> means the
    /// parser matched a built-in, so the identifier cannot be a user object. It is still gated on
    /// <see cref="FormatOptions.RecaseBuiltInFunctions"/>, for consistency rather than for safety —
    /// <c>string_split(…)</c> coming out upper case while <c>row_number()</c> beside it did not was
    /// the reason this name was left alone before the option existed, and switching the option off
    /// has to restore that whole behaviour, not most of it.
    /// </remarks>
    private Doc PrintGlobalFunctionTable(GlobalFunctionTableReference table) =>
        table.Name is null
            ? Passthrough(table)
            : PrintCallableTable(
                table,
                CasedOrKeyword(table.Name.FirstTokenIndex, table.Name.LastTokenIndex),
                table.Name.LastTokenIndex,
                [.. table.Parameters],
                []);

    /// <summary>The <c>::</c> form: <c>::fn_helpcollations()</c>.</summary>
    /// <remarks>
    /// The node's range starts at the <c>::</c>, which belongs to no child, so the callee is sliced from
    /// the node's own start rather than from the name. Recased on the same terms as
    /// <see cref="PrintGlobalFunctionTable"/>: the parser has already matched a built-in, and the
    /// option decides only whether that proof is acted on.
    /// </remarks>
    private Doc PrintBuiltInFunctionTable(BuiltInFunctionTableReference table) =>
        table.Name is null
            ? Passthrough(table)
            : PrintCallableTable(
                table,
                CasedOrKeyword(table.FirstTokenIndex, table.Name.LastTokenIndex),
                table.Name.LastTokenIndex,
                [.. table.Parameters],
                []);

    /// <summary>An XML method used as a table source: <c>@x.nodes('/p') AS n(c)</c>.</summary>
    /// <remarks>
    /// Cased per token rather than recased. The method name is one of a small built-in set, but the
    /// variable beside it is not, and one slice covers both — so nothing here is claimed as a keyword.
    /// </remarks>
    private Doc PrintVariableMethodCallTable(VariableMethodCallTableReference table) =>
        table.Variable is null || table.MethodName is null
            ? Passthrough(table)
            : PrintCallableTable(
                table,
                CasedTokens(table.FirstTokenIndex, SliceEndCoveringComments(table.MethodName)),
                SliceEndCoveringComments(table.MethodName),
                [.. table.Parameters],
                [.. table.Columns]);

    /// <summary>
    /// <c>OPENJSON(@json, '$.path') WITH (a INT '$.a', b NVARCHAR(50) '$.b') AS j</c>.
    /// </summary>
    /// <remarks>
    /// Not shareable with the callable-table helper: the argument list is two fixed properties rather
    /// than a list, and the <c>WITH</c> schema declaration between the call and the alias has no
    /// equivalent anywhere else. The declaration items are laid out one per line like a table
    /// definition, because that is what they are.
    /// </remarks>
    private Doc PrintOpenJsonTable(OpenJsonTableReference table)
    {
        var arguments = new List<TSqlFragment>();
        if (table.Variable is not null) { arguments.Add(table.Variable); }
        if (table.RowPattern is not null) { arguments.Add(table.RowPattern); }

        if (arguments.Count == 0 || !SeparatedBy(arguments))
        {
            return Passthrough(table);
        }

        var head = SignificantTextBetween(table.FirstTokenIndex, EffectiveFirstToken(arguments[0]) - 1);
        var closeParen = FirstSignificantToken(arguments[^1].LastTokenIndex + 1, table.LastTokenIndex);

        if (!Compact(head).Equals("OPENJSON(", StringComparison.OrdinalIgnoreCase)
            || closeParen < 0
            || SignificantTextBetween(closeParen, closeParen) != ")")
        {
            return Passthrough(table);
        }

        var docs = new List<Doc>
        {
            Doc.Group(Doc.Concat(
                Keywords(table.FirstTokenIndex, EffectiveFirstToken(arguments[0]) - 1),
                Doc.Indent(Doc.Concat(
                    Doc.SoftLine,
                    Doc.Join(Doc.Concat(Doc.Text(","), Doc.Line), arguments.Select(Print)))),
                Doc.SoftLine,
                Doc.Text(")"))),
        };

        var afterCall = closeParen;

        if (table.SchemaDeclarationItems.Count > 0)
        {
            var items = table.SchemaDeclarationItems.Cast<TSqlFragment>().ToList();
            var withEnd = EffectiveFirstToken(items[0]) - 1;

            // The declaration's closing parenthesis is found by token type, not by taking the next
            // significant token after the last item: `AS JSON` is a flag with no node and sits *outside*
            // the item's range, so the naive search landed on `AS` and declined every OPENJSON that used
            // it. Nothing between the items and this parenthesis can itself contain one.
            var itemsClose = FirstTokenOfType(
                TSqlTokenType.RightParenthesis, items[^1].LastTokenIndex + 1, table.LastTokenIndex);

            if (!Compact(SignificantTextBetween(closeParen + 1, withEnd)).Equals("WITH(", StringComparison.OrdinalIgnoreCase)
                || itemsClose < 0
                || !TryPrintDeclarationItems(items, itemsClose, out var itemDocs))
            {
                return Passthrough(table);
            }

            docs.Add(Doc.Text(" "));
            docs.Add(Keywords(closeParen + 1, withEnd));
            docs.Add(Doc.Indent(Doc.Concat(Doc.HardLine, itemDocs)));
            docs.Add(Doc.HardLine);
            docs.Add(Doc.Text(")"));

            afterCall = itemsClose;
        }

        if (table.Alias is not null)
        {
            docs.Add(Doc.Text(" "));

            if (SignificantTextBetween(afterCall + 1, table.Alias.FirstTokenIndex - 1).Length > 0)
            {
                docs.Add(CasedTokens(afterCall + 1, table.Alias.FirstTokenIndex - 1));
                docs.Add(Doc.Text(" "));
            }

            docs.Add(Print(table.Alias));
            afterCall = table.Alias.LastTokenIndex;
        }

        return SignificantTextBetween(afterCall + 1, table.LastTokenIndex).Length == 0
            ? Doc.Concat(docs)
            : Passthrough(table);
    }

    /// <summary>
    /// The columns of an <c>OPENJSON … WITH</c> declaration, one per line, each followed by any
    /// <c>AS JSON</c> flag that belongs to it.
    /// </summary>
    /// <remarks>
    /// <c>AS JSON</c> is a bool on the item with no node and, unusually, it falls <em>outside</em> the
    /// item's own token range — so it cannot be emitted by the item's handler and has to be picked up
    /// here, from the gap between one item and the next comma. Reading each item's tail explicitly is
    /// also what lets the flag appear on any column rather than only the last: a plain separator check
    /// saw `AS JSON ,` between two items and declined the lot.
    /// </remarks>
    private bool TryPrintDeclarationItems(List<TSqlFragment> items, int itemsClose, out Doc docs)
    {
        docs = Doc.Empty;
        var parts = new List<Doc>();

        for (var i = 0; i < items.Count; i++)
        {
            int tailEnd;

            if (i + 1 < items.Count)
            {
                // The separator is the last significant token before the next item, and must be a comma.
                var comma = PreviousSignificantToken(EffectiveFirstToken(items[i + 1]) - 1);
                if (comma < 0 || _tokens[comma].TokenType != TSqlTokenType.Comma)
                {
                    return false;
                }

                tailEnd = comma - 1;
            }
            else
            {
                tailEnd = itemsClose - 1;
            }

            if (i > 0)
            {
                parts.Add(Doc.Text(","));
                parts.Add(Doc.HardLine);
            }

            parts.Add(Print(items[i]));

            // `AS JSON` is pure grammar — a bool on the node — so the tail is recased.
            if (SignificantTextBetween(items[i].LastTokenIndex + 1, tailEnd).Length > 0)
            {
                parts.Add(Doc.Text(" "));
                parts.Add(Keywords(items[i].LastTokenIndex + 1, tailEnd));
            }
        }

        docs = Doc.Concat(parts);
        return true;
    }

    /// <summary>One column of an <c>OPENJSON … WITH</c> declaration: <c>a INT '$.a'</c>.</summary>
    /// <remarks>
    /// The column definition is a node and the JSON path is a node; the gap between them is empty, and
    /// <c>AS JSON</c> after the path is a flag with no node — so this is the parts-in-token-order shape
    /// that column definitions already use.
    /// </remarks>
    private Doc PrintSchemaDeclarationItem(SchemaDeclarationItem item) =>
        PrintPartsInTokenOrder(item, item.ColumnDefinition, item.Mapping);

    /// <summary><c>CURRENT_TIMESTAMP</c>, <c>SESSION_USER</c>, <c>CURRENT_USER</c>, <c>SYSTEM_USER</c>.</summary>
    /// <remarks>
    /// A call with no parentheses at all, so there is nothing to lay out — but a handler still earns its
    /// place: without one these are verbatim subtree roots and keyword casing never reaches them.
    /// <c>ParameterlessCallType</c> is an enum, so the word is provably grammar.
    /// </remarks>
    private Doc PrintParameterlessCall(ParameterlessCall call) =>
        call.Collation is null
            ? Keywords(call.FirstTokenIndex, call.LastTokenIndex)
            : Doc.Concat(
                Keywords(call.FirstTokenIndex, EffectiveFirstToken(call.Collation) - 1),
                Doc.Text(" "),
                Print(call.Collation));

    /// <summary><c>$PARTITION.MyPartitionFunction(@value)</c>.</summary>
    /// <remarks>
    /// Cased per token, not recased: <c>$PARTITION</c> is grammar but the function name beside it is a
    /// user-created object, and both sit in the same slice.
    /// </remarks>
    private Doc PrintPartitionFunctionCall(PartitionFunctionCall call)
    {
        if (call.FunctionName is null || call.Parameters.Count == 0 || !SeparatedBy(call.Parameters))
        {
            return Passthrough(call);
        }

        var closeParen = FirstSignificantToken(call.Parameters[^1].LastTokenIndex + 1, call.LastTokenIndex);

        if (SignificantTextBetween(call.FunctionName.LastTokenIndex + 1, EffectiveFirstToken(call.Parameters[0]) - 1) != "("
            || closeParen < 0
            || closeParen != call.LastTokenIndex
            || SignificantTextBetween(closeParen, closeParen) != ")")
        {
            return Passthrough(call);
        }

        return Doc.Group(Doc.Concat(
            CasedTokens(call.FirstTokenIndex, call.FunctionName.LastTokenIndex),
            Doc.Text("("),
            Doc.Indent(Doc.Concat(
                Doc.SoftLine,
                Doc.Join(Doc.Concat(Doc.Text(","), Doc.Line), call.Parameters.Select(Print)))),
            Doc.SoftLine,
            Doc.Text(")")));
    }
}
