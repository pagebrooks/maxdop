using Maxdop.Core.Printing;
using Maxdop.Core.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Formatting;

/// <summary>
/// <c>SELECT</c> and the expression machinery it needs. Deliberately bounded: the remaining exotica
/// (<c>PIVOT</c>, <c>FOR XML</c>, window clauses, <c>COUNT(DISTINCT …)</c>) fall through to
/// passthrough, which is a perfectly good v0 for constructs that appear rarely and read fine as the
/// author wrote them.
/// </summary>
/// <remarks>
/// Clause separators are <see cref="Doc.Line"/> rather than <see cref="Doc.HardLine"/>, inside
/// one group per query. A query that fits stays on a single line (<c>SELECT a FROM t</c>); one
/// that does not breaks at clause boundaries, and each clause then decides independently whether
/// its own contents fit. Nothing here inspects a width.
/// </remarks>
public sealed partial class SqlPrinter
{
    // --- statement level -------------------------------------------------------------

    private Doc PrintSelectStatement(SelectStatement select)
    {
        // `SELECT … INTO #t` hangs off the *statement* but sits textually inside the query, between
        // the select list and FROM — so it can only be laid out by handing it down to the query
        // handler, which is what the `into` parameter is for. It also means the query expression has
        // to be a plain query specification: `SELECT … INTO #t FROM a UNION SELECT …` puts the INTO
        // inside the first branch, and threading it through a binary query is a shape this does not
        // model.
        //
        // Everything between the query expression and the statement's terminator must be accounted
        // for, which TryPrintOptionClause does in both directions: it emits a trailing
        // `OPTION (…)` clause, and when there is none it checks that the tail really is empty. A
        // temporal clause puts the closing parenthesis of `FOR SYSTEM_TIME CONTAINED IN ('a','b')`
        // outside the query's range — and outside the table reference's too, so even passthrough of
        // the table could not recover it. This is the only level at which that token is visible.
        if (select.QueryExpression is null
            || select.On is not null
            || select.ComputeClauses.Count > 0)
        {
            return Passthrough(select);
        }

        // LastOf, not the query expression alone: a QuerySpecification's range covers its INTO
        // clause only when something follows it. `SELECT 1 AS x INTO #tmp` ends the range at `x`,
        // leaving `INTO #tmp` outside it — so measuring the statement's tail from the query alone
        // reported leftover tokens and sent every FROM-less SELECT INTO to passthrough.
        if (!TryPrintOptionClause(
                select,
                select.OptimizerHints,
                LastOf(select.QueryExpression, select.Into),
                out var optionClause))
        {
            return Passthrough(select);
        }

        if (!TryPrintCtes(select, out var ctePrologue))
        {
            return Passthrough(select);
        }

        // The terminator is appended by the dispatcher, not here.
        //
        // Printed directly rather than through Print when there is an INTO, because the dispatcher
        // has nowhere to pass the extra argument. WithComments keeps the query specification's own
        // attached comments, which bypassing Print would otherwise drop — the same mistake as
        // calling PrintStatements instead of Print on a StatementList.
        var body = select.Into is null
            ? Print(select.QueryExpression)
            : PrintQueryInto(select.QueryExpression, select.Into);

        return Doc.Concat(ctePrologue, body, optionClause);
    }

    /// <remarks>
    /// Handles the <c>XMLNAMESPACES</c> block as well as the CTE list, because both share one
    /// <c>WITH</c> and one node. Ignoring it and letting the caller guard against its presence was
    /// the single largest over-eager bail-out in the corpus — <c>WITH XMLNAMESPACES(…), cte AS (…)</c>
    /// is a normal way to write an XML-shredding query, and declining it froze the whole statement.
    /// </remarks>
    private Doc PrintCtes(WithCtesAndXmlNamespaces ctes)
    {
        var expressions = ctes.CommonTableExpressions;
        if (!SeparatedBy(expressions))
        {
            return Passthrough(ctes);
        }

        var list = Doc.Join(Doc.Concat(Doc.Text(","), Doc.HardLine), expressions.Select(Print));

        if (ctes.XmlNamespaces is not { } xmlns)
        {
            // Neither namespaces nor CTEs is not a WITH clause this handler can make sense of.
            return expressions.Count == 0
                ? Passthrough(ctes)
                : Doc.Concat(Keyword("WITH"), Doc.Text(" "), list);
        }

        // The namespace block's own range starts at its first *declaration*, not at the
        // `XMLNAMESPACES` keyword — the same trait as ExistsPredicate — so the keyword and its
        // opening parenthesis come from the gap in front of it. Deliberately not added to
        // EffectiveFirstToken: the keyword belongs in a cased slice, not absorbed into a node whose
        // contents are a URI that must not be touched.
        // `WITH XMLNAMESPACES(` — the URI and its alias are inside the XmlNamespaces node.
        var head = Doc.Concat(Keywords(ctes.FirstTokenIndex, xmlns.FirstTokenIndex - 1), Print(xmlns));

        // The namespace block may be the whole WITH clause — `WITH XMLNAMESPACES('…' AS p) UPDATE …`
        // has no CTE at all — so the comma is only correct when a CTE list actually follows.
        if (expressions.Count == 0)
        {
            return SignificantTextBetween(xmlns.LastTokenIndex + 1, ctes.LastTokenIndex).Length == 0
                ? head
                : Passthrough(ctes);
        }

        return SignificantTextBetween(xmlns.LastTokenIndex + 1, expressions[0].FirstTokenIndex - 1) == ","
            ? Doc.Concat(head, Doc.Text(","), Doc.HardLine, list)
            : Passthrough(ctes);
    }

    private Doc PrintCte(CommonTableExpression cte)
    {
        if (cte.ExpressionName is null || cte.QueryExpression is null || !SeparatedBy(cte.Columns))
        {
            return Passthrough(cte);
        }

        var parts = new List<Doc> { Print(cte.ExpressionName) };

        if (cte.Columns.Count > 0)
        {
            parts.Add(Doc.Group(Doc.Concat(
                Doc.Text(" ("),
                Doc.Indent(Doc.Concat(Doc.SoftLine, JoinList(cte.Columns))),
                Doc.SoftLine,
                Doc.Text(")"))));
        }

        // A CTE body always breaks: it is a whole query, and cramming one onto the AS line is
        // unreadable however narrow it happens to be.
        parts.Add(Doc.Text(" "));
        parts.Add(Keyword("AS"));
        parts.Add(Doc.Text(" ("));
        parts.Add(Doc.Indent(Doc.Concat(Doc.HardLine, Print(cte.QueryExpression))));
        parts.Add(Doc.HardLine);
        parts.Add(Doc.Text(")"));

        return Doc.Concat(parts);
    }

    // --- query expressions -----------------------------------------------------------

    /// <param name="into">
    /// The <c>INTO &lt;table&gt;</c> target, when the enclosing statement has one. It belongs to the
    /// statement in the AST but is written inside the query, so only the statement handler can
    /// supply it.
    /// </param>
    private Doc PrintQuerySpecification(QuerySpecification query, SchemaObjectName? into = null)
    {
        if (query.SelectElements.Count == 0
            || query.WindowClause is not null
            || !SeparatedBy(query.SelectElements))
        {
            return Passthrough(query);
        }

        // The INTO keyword belongs to no node, so it is read from the gap between the select list and
        // the target — and that gap has to be exactly the keyword, or something this handler does not
        // model sits there.
        if (into is not null
            && !SignificantTextBetween(query.SelectElements[^1].LastTokenIndex + 1, into.FirstTokenIndex - 1)
                .Equals("INTO", StringComparison.OrdinalIgnoreCase))
        {
            return Passthrough(query);
        }

        // Everything between SELECT and the first element in one slice: DISTINCT, ALL,
        // TOP (n), PERCENT, WITH TIES. One read instead of four enum mappings, cased per token so
        // a variable in `TOP (@n)` keeps its name.
        var modifiersFrom = query.FirstTokenIndex + 1;
        var modifiersTo = query.SelectElements[0].FirstTokenIndex - 1;

        var head = new List<Doc> { Keyword("SELECT") };
        if (SignificantTextBetween(modifiersFrom, modifiersTo).Length > 0)
        {
            head.Add(Doc.Text(" "));
            head.Add(CasedTokens(modifiersFrom, modifiersTo));
        }

        head.Add(Doc.Indent(Doc.Concat(
            Doc.Line,
            JoinList(query.SelectElements))));

        // Everything in the query's own range must be emitted by one of the clauses below. A
        // temporal clause — `FROM T FOR SYSTEM_TIME CONTAINED IN ('a','b')` — extends past the last
        // clause node, and its closing parenthesis was being dropped.
        var lastEnd = query.SelectElements[^1].LastTokenIndex;
        foreach (var clause in new TSqlFragment?[]
                 {
                     into, query.FromClause, query.WhereClause, query.GroupByClause, query.HavingClause,
                     query.OrderByClause, query.OffsetClause, query.ForClause,
                 })
        {
            if (clause is not null)
            {
                lastEnd = Math.Max(lastEnd, clause.LastTokenIndex);
            }
        }

        if (SignificantTextBetween(lastEnd + 1, query.LastTokenIndex).Length > 0)
        {
            return Passthrough(query);
        }

        var clauses = new List<Doc>
        {
            Doc.Group(
                Doc.Concat(head),
                shouldBreak: _options.AlwaysBreakSelectList && query.SelectElements.Count > 1),
        };

        if (into is not null)
        {
            clauses.Add(Doc.Line);
            clauses.Add(CasedTokensBetween(query.SelectElements[^1], into));
            clauses.Add(Doc.Text(" "));
            clauses.Add(Print(into));
        }

        AppendClause(clauses, query.FromClause);
        AppendClause(clauses, query.WhereClause);
        AppendClause(clauses, query.GroupByClause);
        AppendClause(clauses, query.HavingClause);
        AppendQueryTail(clauses, query);

        return Doc.Group(Doc.Concat(clauses));
    }

    /// <summary>
    /// Prints a query expression that owns a <c>SELECT … INTO</c>, handing the target down to the
    /// leftmost query specification — the branch it is written in.
    /// </summary>
    /// <remarks>
    /// <c>INTO</c> hangs off the <em>statement</em> in ScriptDom but sits textually inside the query,
    /// between the select list and <c>FROM</c>. With a set operator it is therefore inside the
    /// <b>first branch</b>: <c>SELECT a INTO #t FROM x UNION ALL SELECT b FROM y</c> creates the table
    /// from the whole union but writes the target in the leading <c>SELECT</c>. Requiring a plain query
    /// specification declined that outright; walking the left spine puts it where it belongs.
    /// <para>A parenthesised leading branch is declined: <c>(SELECT a INTO #t …) UNION …</c> would need
    /// the target emitted inside parentheses this handler does not own.</para>
    /// </remarks>
    private Doc PrintQueryInto(QueryExpression query, SchemaObjectName into) => query switch
    {
        QuerySpecification specification =>
            WithComments(specification, PrintQuerySpecification(specification, into)),
        BinaryQueryExpression binary => WithComments(binary, PrintBinaryQuery(binary, into)),
        _ => Passthrough(query),
    };

    private Doc PrintBinaryQuery(BinaryQueryExpression binary, SchemaObjectName? into = null)
    {
        if (binary.FirstQueryExpression is null || binary.SecondQueryExpression is null)
        {
            return Passthrough(binary);
        }

        // "UNION", "UNION ALL", "EXCEPT", "INTERSECT" — read from the source.
        //
        // Measured from past the INTO target, not from the first branch alone. A QuerySpecification's
        // range excludes its own `INTO` when nothing follows it, so `SELECT x INTO #t UNION SELECT …`
        // leaves `INTO #t` in the gap between the branches — and reading the operator from there gave
        // `INTO #TODELETE UNION`, which emitted the target twice *and* upper-cased the table name,
        // because an operator slice is a keyword position. Uninstall.sql stopped parsing.
        var gapFrom = LastOf(binary.FirstQueryExpression, into).LastTokenIndex + 1;
        var gapTo = EffectiveFirstToken(binary.SecondQueryExpression) - 1;
        var op = SignificantTextBetween(gapFrom, gapTo);

        // A comment written above the UNION stays above it rather than crossing to the far side.
        // See HoistLeadingBefore; the gap is the same one the operator text is read from.
        var hoisted = HoistLeadingBefore(binary.SecondQueryExpression, gapFrom, gapTo);

        var clauses = new List<Doc>
        {
            into is null ? Print(binary.FirstQueryExpression) : PrintQueryInto(binary.FirstQueryExpression, into),
            Doc.HardLine,
            hoisted ?? Doc.Empty,
            Keyword(op),
            Doc.HardLine,
            Print(binary.SecondQueryExpression),
        };

        AppendQueryTail(clauses, binary);
        return Doc.Concat(clauses);
    }

    private Doc PrintQueryParenthesis(QueryParenthesisExpression parenthesis)
    {
        if (parenthesis.QueryExpression is null
            || (parenthesis.OrderByClause is null
                && parenthesis.OffsetClause is null
                && parenthesis.ForClause is null
                && !IsPlainParenthesised(parenthesis, parenthesis.QueryExpression)))
        {
            return Passthrough(parenthesis);
        }

        var clauses = new List<Doc>
        {
            Doc.Text("("),
            Doc.Indent(Doc.Concat(Doc.HardLine, Print(parenthesis.QueryExpression))),
            Doc.HardLine,
            Doc.Text(")"),
        };

        AppendQueryTail(clauses, parenthesis);
        return Doc.Concat(clauses);
    }

    /// <summary>ORDER BY / OFFSET / FOR, which live on the query expression base type.</summary>
    private void AppendQueryTail(List<Doc> clauses, QueryExpression query)
    {
        AppendClause(clauses, query.OrderByClause);
        AppendClause(clauses, query.OffsetClause);
        AppendClause(clauses, query.ForClause);
    }

    private void AppendClause(List<Doc> clauses, TSqlFragment? clause)
    {
        if (clause is null)
        {
            return;
        }

        clauses.Add(Doc.Line);
        clauses.Add(Print(clause));
    }

    // --- select elements -------------------------------------------------------------

    private Doc PrintSelectScalar(SelectScalarExpression element)
    {
        if (element.Expression is null)
        {
            return Passthrough(element);
        }

        var expression = Print(element.Expression);
        if (element.ColumnName is null)
        {
            return expression;
        }

        // `alias = expression` and `expression AS alias` produce identical ASTs, so the only way
        // to keep the author's form is position: an alias whose tokens come first was written in
        // the assignment form. Normalising one into the other would pass every safety check and
        // still be an unrequested rewrite.
        if (element.ColumnName.FirstTokenIndex < element.Expression.FirstTokenIndex)
        {
            // SeparatorBefore, not a plain space: `wait_time_hms =` followed by an own-line comment
            // and then a CASE is a common shape, and pulling the comment up onto the `=` line
            // reclassified it on the next pass and moved it again.
            return Doc.Concat(
                Print(element.ColumnName),
                Doc.Text(" ="),
                SeparatorBefore(element.Expression),
                expression);
        }

        var asKeyword = TextBetween(element.Expression, element.ColumnName);
        return Doc.Concat(
            expression,
            Doc.Text(" "),
            asKeyword.Length > 0 ? Doc.Concat(Keyword(asKeyword), Doc.Text(" ")) : Doc.Empty,
            Print(element.ColumnName));
    }

    private Doc PrintSelectStar(SelectStarExpression star) => star.Qualifier is null
        ? Doc.Text("*")
        : Doc.Concat(Print(star.Qualifier), Doc.Text(".*"));

    // --- clauses ---------------------------------------------------------------------

    private Doc PrintFromClause(FromClause from)
    {
        if (from.TableReferences.Count == 0
            || from.PredictTableReference.Count > 0
            || !SeparatedBy(from.TableReferences))
        {
            return Passthrough(from);
        }

        // A single reference — including a whole join tree — sits on the FROM line, so joins
        // line up under FROM rather than being pushed a level right. That is the prevailing
        // T-SQL convention and it keeps join chains readable.
        if (from.TableReferences.Count == 1)
        {
            return Doc.Group(Doc.Concat(Keyword("FROM"), Doc.Text(" "), Print(from.TableReferences[0])));
        }

        return Doc.Group(Doc.Concat(
            Keyword("FROM"),
            Doc.Indent(Doc.Concat(
                Doc.Line,
                JoinList(from.TableReferences)))));
    }

    private Doc PrintWhereClause(WhereClause where)
    {
        // The clause must be exactly `WHERE <condition>`. Graph predicates break that assumption:
        // in `WHERE MATCH(N-(E)->N2 AND …)` the condition's range starts *after* `MATCH(`, so
        // emitting the keyword and the condition dropped the `MATCH(` and produced output that no
        // longer parsed. Verifying both ends turns that into passthrough.
        // `WHERE CURRENT OF <cursor>` — the positioned form, which has a cursor instead of a condition.
        // The three words in front of the name are grammar (the cursor is a node, so no name can be in
        // that run) and the name itself is Printed.
        if (where.Cursor is { } cursor)
        {
            return where.SearchCondition is not null
                || SignificantTextBetween(cursor.LastTokenIndex + 1, where.LastTokenIndex).Length > 0
                    ? Passthrough(where)
                    : Doc.Concat(
                        Keywords(where.FirstTokenIndex, EffectiveFirstToken(cursor) - 1),
                        Doc.Text(" "),
                        Print(cursor));
        }

        if (where.SearchCondition is null
            || !SignificantTextBetween(where.FirstTokenIndex, EffectiveFirstToken(where.SearchCondition) - 1)
                .Equals("WHERE", StringComparison.OrdinalIgnoreCase)
            || SignificantTextBetween(where.SearchCondition.LastTokenIndex + 1, where.LastTokenIndex).Length > 0)
        {
            return Passthrough(where);
        }

        return PrintPredicateClause("WHERE", where.SearchCondition);
    }

    /// <summary>
    /// A WHERE or HAVING and its condition — the same construct with a different keyword.
    /// </summary>
    private Doc PrintPredicateClause(string keyword, BooleanExpression condition) =>
        _options.AlwaysBreakWhere
            ? Doc.Concat(Keyword(keyword), AlignedPredicates(condition))

            // The default: the first predicate stays on the keyword's line and continuations indent
            // under it, breaking only when the clause does not fit.
            //   WHERE a = 1
            //       AND b = 2
            : Doc.Concat(Keyword(keyword), SeparatorBefore(condition), Doc.Indent(Print(condition)));

    /// <summary>
    /// One top-level predicate per line, operators right-aligned so every predicate starts in the
    /// same column (<see cref="FormatOptions.AlwaysBreakWhere"/>).
    /// </summary>
    /// <remarks>
    /// Only a run of the *same* operator is flattened, exactly as <see cref="PrintBooleanChain"/>
    /// does. Laying a mixed <c>AND</c>/<c>OR</c> chain out as one flat list would show
    /// <c>a OR (b AND c)</c> as three peers and read as if it evaluated top to bottom, which is a
    /// claim about precedence the SQL does not make. A nested run keeps its own group and stays
    /// together, which is the honest rendering.
    /// </remarks>
    private Doc AlignedPredicates(BooleanExpression condition)
    {
        // Comment emission is centralised in Print, and this path prints the chain's *operands*
        // rather than the chain node, so a comment attached to the node itself would be dropped.
        // `WHERE /* … */ a = 1 AND b = 2` is real code — sp_BlitzIndex has one — and it made the
        // whole file refuse. The gate caught it rather than losing the comment, which is the system
        // working, but a refusal is still a file the option cannot format. Fall back to the ordinary
        // condition layout, keeping only the promise this option can honestly keep here: the keyword
        // on a line of its own.
        if (_comments.HasAny(condition))
        {
            return Doc.Indent(Doc.Concat(Doc.HardLine, Print(condition)));
        }

        var spine = new List<BooleanBinaryExpression>();
        if (condition is BooleanBinaryExpression chain)
        {
            var current = chain;
            while (true)
            {
                spine.Add(current);

                // Stop at a node carrying comments, as PrintBooleanChain does: it will not be
                // visited by Print, so its comments would be dropped.
                if (current.FirstExpression is BooleanBinaryExpression left
                    && left.BinaryExpressionType == chain.BinaryExpressionType
                    && !_comments.HasAny(left))
                {
                    current = left;
                    continue;
                }

                break;
            }
        }

        var operators = new List<string>();
        for (var i = spine.Count - 1; i >= 0; i--)
        {
            var op = TextBetween(spine[i].FirstExpression, spine[i].SecondExpression);

            // Anything other than AND/OR in the gap means an operand's range does not cover its own
            // text — graph `MATCH(…)` predicates do exactly that. Fall back to the ordinary layout
            // rather than aligning on text that is not the operator; a formatting option must not
            // be able to turn a working file into a refusal.
            if (!op.Equals("AND", StringComparison.OrdinalIgnoreCase)
                && !op.Equals("OR", StringComparison.OrdinalIgnoreCase))
            {
                operators.Clear();
                break;
            }

            operators.Add(op);
        }

        // A single predicate, or a chain this cannot model: one line, no alignment to do.
        if (operators.Count == 0)
        {
            return Doc.Indent(Doc.Concat(Doc.HardLine, Print(condition)));
        }

        // The operator column is as wide as the widest operator in this chain, so `OR` right-aligns
        // under `AND` when a run of each meets.
        var width = operators.Max(op => op.Length);
        var parts = new List<Doc>
        {
            Doc.Text(new string(' ', width + 1)),
            Doc.Align(width + 1, Print(spine[^1].FirstExpression)),
        };

        for (var i = 0; i < operators.Count; i++)
        {
            var node = spine[spine.Count - 1 - i];

            // Padded to the predicate column rather than the operator's, so the comment lines up
            // with the predicates it describes and the operator column stays a clean gutter.
            var hoisted = HoistLeadingBefore(
                node.SecondExpression,
                node.FirstExpression.LastTokenIndex + 1,
                EffectiveFirstToken(node.SecondExpression) - 1);
            parts.Add(Doc.HardLine);

            // Outside the Align, exactly as JoinList keeps a hoisted comment outside its own: the
            // hard line that ends the comment would otherwise indent by the operator column too, and
            // carry the operator along with it.
            if (hoisted is not null)
            {
                parts.Add(Doc.Text(new string(' ', width + 1)));
                parts.Add(hoisted);
            }

            parts.Add(Doc.Align(width + 1, Doc.Concat(
                Doc.Text(new string(' ', width - operators[i].Length)),
                Keyword(operators[i]),
                SeparatorBefore(node.SecondExpression),
                Print(node.SecondExpression))));
        }

        return Doc.Indent(Doc.Concat(Doc.HardLine, Doc.Concat(parts)));
    }

    private Doc PrintHavingClause(HavingClause having) =>
        having.SearchCondition is null
        || !SignificantTextBetween(having.FirstTokenIndex, EffectiveFirstToken(having.SearchCondition) - 1)
            .Equals("HAVING", StringComparison.OrdinalIgnoreCase)
        || SignificantTextBetween(having.SearchCondition.LastTokenIndex + 1, having.LastTokenIndex).Length > 0
            ? Passthrough(having)
            : PrintPredicateClause("HAVING", having.SearchCondition);

    private Doc PrintGroupByClause(GroupByClause groupBy)
    {
        if (groupBy.GroupingSpecifications.Count == 0 || !SeparatedBy(groupBy.GroupingSpecifications))
        {
            return Passthrough(groupBy);
        }

        // GROUP BY ... WITH ROLLUP / WITH CUBE puts keywords *after* the specifications, and the
        // handler previously read only the ones before them — silently dropping the suffix.
        // Cased per token because ROLLUP and CUBE are non-reserved words that lex as identifiers.
        var suffixFrom = groupBy.GroupingSpecifications[^1].LastTokenIndex + 1;
        var suffix = CasedTokens(suffixFrom, groupBy.LastTokenIndex);
        var hasSuffix = SignificantTextBetween(suffixFrom, groupBy.LastTokenIndex).Length > 0;

        // Covers "GROUP BY" and "GROUP BY ALL" without needing the option enum.
        var keyword = SignificantTextBetween(
            groupBy.FirstTokenIndex,
            groupBy.GroupingSpecifications[0].FirstTokenIndex - 1);

        return Doc.Group(Doc.Concat(
            Keyword(keyword),
            Doc.Indent(Doc.Concat(
                Doc.Line,
                JoinList(groupBy.GroupingSpecifications))),
            hasSuffix ? Doc.Concat(Doc.Line, suffix) : Doc.Empty));
    }

    private Doc PrintOrderByClause(OrderByClause orderBy)
    {
        if (orderBy.OrderByElements.Count == 0 || !SeparatedBy(orderBy.OrderByElements))
        {
            return Passthrough(orderBy);
        }

        var keyword = SignificantTextBetween(
            orderBy.FirstTokenIndex,
            orderBy.OrderByElements[0].FirstTokenIndex - 1);

        return Doc.Group(Doc.Concat(
            Keyword(keyword),
            Doc.Indent(Doc.Concat(
                Doc.Line,
                JoinList(orderBy.OrderByElements)))));
    }

    private Doc PrintSortExpression(ExpressionWithSortOrder sort)
    {
        if (sort.Expression is null)
        {
            return Passthrough(sort);
        }

        // "ASC", "DESC", or nothing — whichever the author wrote.
        var order = SignificantTextBetween(sort.Expression.LastTokenIndex + 1, sort.LastTokenIndex);
        return order.Length == 0
            ? Print(sort.Expression)
            : Doc.Concat(Print(sort.Expression), Doc.Text(" "), Keyword(order));
    }

    // --- table references ------------------------------------------------------------

    private Doc PrintNamedTable(NamedTableReference table)
    {
        // TABLESAMPLE and temporal (`FOR SYSTEM_TIME AS OF …`) clauses are separate nodes whose
        // extent the reference's own range does not reliably cover, so the tail slice below
        // truncated them. Deferred rather than guessed at; both are rare.
        if (table.SchemaObject is null || table.TableSampleClause is not null)
        {
            return Passthrough(table);
        }

        // Declining is not enough for a temporal clause, because Passthrough slices the node's own
        // range and that range stops short of it: `FROM Product FOR SYSTEM_TIME ALL` came back as
        // `FROM Product`, the clause gone. The verifier caught it — three corpus files, every one a
        // temporal-table demo — but a refusal is still a file that will not format. Slicing to
        // whichever of the two ends last covers the clause wherever ScriptDom put it.
        if (table.TemporalClause is { } temporal)
        {
            var end = Math.Max(table.LastTokenIndex, temporal.LastTokenIndex);

            // `FOR SYSTEM_TIME ALL` has no operand, so ScriptDom gives the clause no token range at
            // all — [-1..-1] — and the table's range stops at its own name. The enum is the only
            // evidence the clause is there, and the tokens have to be found by reading them. Every
            // other form (AS OF, BETWEEN, CONTAINED IN) sits inside the table's range and needs none
            // of this.
            if (temporal.TemporalClauseType == TemporalClauseType.TemporalAll)
            {
                var last = -1;
                var seen = 0;
                for (var i = end + 1; i < _tokens.Count && seen < 3; i++)
                {
                    if (_tokens[i].IsTrivia())
                    {
                        continue;
                    }

                    seen++;
                    last = i;
                }

                // Confirmed against the text rather than assumed: a shape this does not recognise is
                // one whose extent it cannot know, and guessing at a slice there would drop tokens
                // silently instead of declining loudly.
                if (seen != 3
                    || !Compact(SignificantTextBetween(end + 1, last))
                        .Equals("FORSYSTEM_TIMEALL", StringComparison.OrdinalIgnoreCase))
                {
                    return Passthrough(table);
                }

                end = last;
            }

            return CasedTokens(EffectiveFirstToken(table), end);
        }

        var parts = new List<Doc> { Print(table.SchemaObject) };
        var tailFrom = table.SchemaObject.LastTokenIndex + 1;

        if (table.Alias is not null)
        {
            // Cased per token: `FROM t (nolock) x` puts the hint between the table and its alias,
            // and `nolock` is a non-reserved word that must keep the author's casing.
            var asKeyword = TextBetween(table.SchemaObject, table.Alias);
            parts.Add(Doc.Text(" "));
            if (asKeyword.Length > 0)
            {
                parts.Add(CasedTokensBetween(table.SchemaObject, table.Alias));
                parts.Add(Doc.Text(" "));
            }

            parts.Add(Print(table.Alias));
            tailFrom = table.Alias.LastTokenIndex + 1;
        }

        // Hints, temporal and TABLESAMPLE clauses in one slice. Cased per token, so `WITH` and
        // `NOLOCK` follow the keyword option while an index name in `WITH (INDEX(IX_Foo))` keeps
        // the casing that identifies it.
        if (SignificantTextBetween(tailFrom, table.LastTokenIndex).Length > 0)
        {
            parts.Add(Doc.Text(" "));
            parts.Add(CasedTokens(tailFrom, table.LastTokenIndex));
        }

        return Doc.Concat(parts);
    }

    /// <summary><c>FROM @t AS v</c></summary>
    /// <remarks>
    /// A table variable in a FROM clause. Two nodes and an optional <c>AS</c> in the gap, so the parts
    /// helper covers it — and there were 2,800 of them in the corpus, each one a small hole in
    /// otherwise-formatted output.
    /// </remarks>
    private Doc PrintVariableTableReference(VariableTableReference table) =>
        PrintKeywordParts(table, table.Variable, table.Alias);

    private Doc PrintQueryDerivedTable(QueryDerivedTable derived)
    {
        if (derived.QueryExpression is null)
        {
            return Passthrough(derived);
        }

        var parts = new List<Doc>
        {
            Doc.Text("("),
            Doc.Indent(Doc.Concat(Doc.HardLine, Print(derived.QueryExpression))),
            Doc.HardLine,
            Doc.Text(")"),
        };

        if (derived.Alias is not null)
        {
            // The words between the closing parenthesis and the alias: `AS`, or `FOR PATH AS` in
            // graph syntax. Emitted as a *claimed* token slice rather than as recased text, because
            // `PATH` is not reserved and lexes as an identifier — recasing it as text is a token
            // change the verifier rejects, and it did: `keywordCase: lower` refused every file with
            // `FOR PATH`. Invisible under the default upper-case, where recasing `PATH` changes
            // nothing. Slicing claims the position, so the verifier tolerates the case either way.
            //
            // From after the parenthesis, which the parts above already emitted; the whole gap would
            // print it twice.
            var closing = FirstTokenOfType(
                TSqlTokenType.RightParenthesis,
                derived.QueryExpression.LastTokenIndex + 1,
                derived.Alias.FirstTokenIndex - 1);

            if (closing < 0)
            {
                return Passthrough(derived);
            }

            parts.Add(Doc.Text(" "));
            if (SignificantTextBetween(closing + 1, derived.Alias.FirstTokenIndex - 1).Length > 0)
            {
                parts.Add(Keywords(closing + 1, derived.Alias.FirstTokenIndex - 1));
                parts.Add(Doc.Text(" "));
            }

            parts.Add(Print(derived.Alias));
        }

        // A derived table may rename its output columns: `) AS t10 (c1, c2)`. These live on
        // TableReferenceWithAliasAndColumns, one inheritance level above the alias, and were being
        // dropped entirely — the single cause behind the largest cluster of corpus refusals.
        if (derived.Columns.Count > 0)
        {
            if (!SeparatedBy(derived.Columns))
            {
                return Passthrough(derived);
            }

            parts.Add(Doc.Group(Doc.Concat(
                Doc.Text(" ("),
                Doc.Indent(Doc.Concat(Doc.SoftLine, JoinList(derived.Columns))),
                Doc.SoftLine,
                Doc.Text(")"))));
        }

        return Doc.Concat(parts);
    }

    /// <summary>
    /// Prints a chain of joins as one flat list rather than nested pairs.
    /// </summary>
    /// <remarks>
    /// Joins nest left-associatively, so <c>a JOIN b JOIN c</c> is <c>((a JOIN b) JOIN c)</c>.
    /// Printing that shape gives each join its own group, and the groups then break
    /// independently — producing output where the first join stayed inline on the FROM line while
    /// the second went to its own, which reads as arbitrary. Flattening puts every join in one
    /// group, so the chain breaks all-or-nothing. Same reasoning as
    /// <see cref="PrintBooleanChain"/>, and iterative for the same reason.
    /// </remarks>
    private Doc PrintJoin(JoinTableReference join)
    {
        var spine = new List<JoinTableReference>();
        var current = join;
        while (true)
        {
            if (current.FirstTableReference is null || current.SecondTableReference is null)
            {
                return Passthrough(join);
            }

            spine.Add(current);

            // Stop at a node with comments: it will not be visited by Print, so its comments
            // would be dropped.
            if (current.FirstTableReference is JoinTableReference left && !_comments.HasAny(left))
            {
                current = left;
                continue;
            }

            break;
        }

        var parts = new List<Doc> { Print(spine[^1].FirstTableReference) };

        for (var i = spine.Count - 1; i >= 0; i--)
        {
            var node = spine[i];

            // "INNER JOIN", "LEFT OUTER JOIN", "CROSS APPLY", "INNER LOOP JOIN" — including the
            // join hint, all straight from the source. Cased per token, because `APPLY` is a
            // non-reserved word that lexes as an identifier and must keep the author's casing.
            if (TextBetween(node.FirstTableReference, node.SecondTableReference).Length == 0)
            {
                return Passthrough(join);
            }

            parts.Add(Doc.Line);

            // A comment written above the join keywords belongs above them. Left where it attached
            // it printed *inside* the join — `OUTER APPLY` then the comment then the table — which
            // reads as a note about the operand rather than about the join. See HoistLeadingBefore;
            // the range is the same one the keywords are read from, so a comment written between two
            // of those keywords stays where it is.
            var hoistedJoin = HoistLeadingBefore(
                node.SecondTableReference,
                node.FirstTableReference.LastTokenIndex + 1,
                EffectiveFirstToken(node.SecondTableReference) - 1);

            if (hoistedJoin is not null)
            {
                parts.Add(hoistedJoin);
            }

            // `INNER JOIN`, `LEFT OUTER JOIN`, `CROSS APPLY`, `INNER LOOP JOIN`. Every word the
            // grammar allows between two table references is a join keyword or a join hint — a name
            // cannot appear — so `cross apply` is recased rather than left half-cased.
            parts.Add(Keywords(
                node.FirstTableReference.LastTokenIndex + 1,
                EffectiveFirstToken(node.SecondTableReference) - 1));
            parts.Add(SeparatorBefore(node.SecondTableReference));
            parts.Add(Print(node.SecondTableReference));

            if (node is QualifiedJoin { SearchCondition: not null } qualified)
            {
                // The ON gets its own group so it is measured on its own. Sharing the chain's group
                // meant it broke whenever the chain did — and since the chain now always breaks, the
                // condition would always have been pushed to its own line, which is not what the
                // convention asks for. Attached when it fits, indented on its own line when it does
                // not.
                //
                // Except when a comment sits between the table and the ON. An end-of-line comment is
                // emitted as a line suffix, so it is pushed to the end of whatever line it lands on —
                // pulling the condition up put the comment *after* it:
                //
                //     INNER JOIN u ON u.id = t.id -- keep me with the join
                //
                // which reads as a note about the join condition rather than about the table. The
                // comment is not lost and the file is still a fixed point, so no gate objects; it is
                // simply the wrong thing to have written. Keeping the ON on its own line preserves
                // what the author annotated.
                //
                // This is a comment changing a layout decision, which the printer otherwise refuses
                // to allow. The exception is the same one SeparatorBefore already makes, and for the
                // same reason: where a comment's position carries meaning, that meaning outranks the
                // layout preference.
                var commentBeforeOn = !NoCommentsIn(
                    node.SecondTableReference.LastTokenIndex + 1,
                    EffectiveFirstToken(qualified.SearchCondition) - 1);

                // An own-line comment between the table and the ON introduces the join condition, so
                // it is emitted before the keyword rather than after it. See HoistLeadingBefore.
                var hoistedOn = HoistLeadingBefore(
                    qualified.SearchCondition,
                    node.SecondTableReference.LastTokenIndex + 1,
                    EffectiveFirstToken(qualified.SearchCondition) - 1);

                parts.Add(Doc.Group(
                    Doc.Indent(Doc.Concat(
                        Doc.Line,
                        hoistedOn ?? Doc.Empty,
                        Keyword("ON"),
                        SeparatorBefore(qualified.SearchCondition),
                        Doc.Indent(Print(qualified.SearchCondition)))),
                    shouldBreak: commentBeforeOn));
            }
        }

        // A join chain always breaks, however short. A join is a relationship between tables, and
        // reading the relationships down the left edge is what makes a FROM clause scannable — the
        // prevailing T-SQL convention, and unlike CASE there is no counter-idiom wanting an inline
        // one. Adding a table is then a one-line diff too.
        return Doc.Group(Doc.Concat(parts), shouldBreak: true);
    }

    // --- boolean expressions ---------------------------------------------------------

    /// <summary>
    /// Flattens a chain of same-operator boolean expressions so <c>a AND b AND c</c> lays out as
    /// one list rather than nested pairs.
    /// </summary>
    /// <remarks>
    /// ScriptDom parses these left-associatively, giving <c>((a AND b) AND c)</c>. Printing that
    /// shape directly nests a group per operand, so the second and third predicates indent
    /// differently — which looks arbitrary. Walking the left spine iteratively also keeps a
    /// thousand-term legacy <c>OR</c> chain off the call stack.
    /// </remarks>
    private Doc PrintBooleanChain(BooleanBinaryExpression expression)
    {
        var spine = new List<BooleanBinaryExpression>();
        var current = expression;
        while (true)
        {
            spine.Add(current);

            // Stop descending at a node carrying comments: it will not be visited by Print, so
            // its comments would be silently dropped. Better a nested layout than a lost comment.
            if (current.FirstExpression is BooleanBinaryExpression left
                && left.BinaryExpressionType == expression.BinaryExpressionType
                && !_comments.HasAny(left))
            {
                current = left;
                continue;
            }

            break;
        }

        // The only boolean binary operators in T-SQL are AND and OR, so anything else in the gap
        // means an operand's range does not cover its own text. Graph predicates do exactly that:
        // in `MATCH(a AND b) AND MATCH(c AND d)` the operand ranges start after `MATCH(`, so the
        // gap read `) AND MATCH (` — and printing that as the operator dropped the first `MATCH(`
        // while smuggling the second into the operator text. Output that no longer parsed.
        var parts = new List<Doc> { Print(spine[^1].FirstExpression) };
        for (var i = spine.Count - 1; i >= 0; i--)
        {
            var node = spine[i];
            var op = TextBetween(node.FirstExpression, node.SecondExpression);
            if (!op.Equals("AND", StringComparison.OrdinalIgnoreCase)
                && !op.Equals("OR", StringComparison.OrdinalIgnoreCase))
            {
                return Passthrough(expression);
            }

            parts.Add(Doc.Line);

            // An own-line comment in front of the operand goes in front of the operator instead, so
            // `AND` keeps its predicate. See HoistLeadingBefore.
            var hoisted = HoistLeadingBefore(
                node.SecondExpression,
                node.FirstExpression.LastTokenIndex + 1,
                EffectiveFirstToken(node.SecondExpression) - 1);
            if (hoisted is not null)
            {
                parts.Add(hoisted);
            }

            parts.Add(Keyword(op));
            parts.Add(SeparatorBefore(node.SecondExpression));
            parts.Add(Print(node.SecondExpression));
        }

        return Doc.Group(Doc.Concat(parts));
    }

    private Doc PrintBooleanComparison(BooleanComparisonExpression comparison)
    {
        if (comparison.FirstExpression is null || comparison.SecondExpression is null)
        {
            return Passthrough(comparison);
        }

        // No break opportunity: comparisons are short, and `a =` / newline / `1` reads worse
        // than an over-long line. Long right-hand sides are subqueries or calls, which group
        // themselves.
        //
        // SeparatorBefore rather than a plain space, for the reason it exists: a comment that had a
        // line of its own before the right-hand operand would otherwise be dragged onto the
        // operator's line, where it has code to its left, which reclassifies it as end-of-line on
        // the next pass and moves it again. Four corpus files stopped being a fixed point over
        // exactly this — `LTRIM(…) =` then `/* why */` then `LTRIM(…)`.
        return Doc.Concat(
            Print(comparison.FirstExpression),
            Doc.Text(" "),
            Doc.Text(TextBetween(comparison.FirstExpression, comparison.SecondExpression)),
            SeparatorBefore(comparison.SecondExpression),
            Print(comparison.SecondExpression));
    }

    private Doc PrintBooleanParenthesis(BooleanParenthesisExpression parenthesis) =>
        parenthesis.Expression is null || !IsPlainParenthesised(parenthesis, parenthesis.Expression)
            ? Passthrough(parenthesis)
            : Doc.Group(Doc.Concat(
                Doc.Text("("),
                Doc.Indent(Doc.Concat(Doc.SoftLine, Print(parenthesis.Expression))),
                Doc.SoftLine,
                Doc.Text(")")));

    private Doc PrintBooleanNot(BooleanNotExpression not) => not.Expression is null
        ? Passthrough(not)
        : Doc.Concat(Keyword("NOT"), SeparatorBefore(not.Expression), Print(not.Expression));

    private Doc PrintIsNull(BooleanIsNullExpression isNull)
    {
        if (isNull.Expression is null)
        {
            return Passthrough(isNull);
        }

        // `IS [NOT] DISTINCT FROM NULL` (SQL 2022) also parses as a null test, and writing out a
        // bare `IS NULL` silently reduced it to a different predicate. Confirm the tail really is
        // what this handler models.
        var tail = SignificantTextBetween(isNull.Expression.LastTokenIndex + 1, isNull.LastTokenIndex);
        var expected = isNull.IsNot ? "IS NOT NULL" : "IS NULL";
        if (!tail.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            return Passthrough(isNull);
        }

        return Doc.Concat(Print(isNull.Expression), Doc.Text(" "), Keyword(expected));
    }

    private Doc PrintExists(ExistsPredicate exists) => exists.Subquery is null
        ? Passthrough(exists)
        : Doc.Concat(Keyword("EXISTS"), SeparatorBefore(exists.Subquery), Print(exists.Subquery));

    /// <summary><c>x LIKE '%y%'</c>, <c>x NOT LIKE 'q%'</c>, <c>x LIKE @p ESCAPE '\'</c></summary>
    /// <remarks>
    /// Through the shared parts helper: the operator and the <c>ESCAPE</c> keyword both live in the
    /// gaps between children, so reading them keeps <c>NOT LIKE</c> and the escape clause without this
    /// handler needing to know either exists. <c>OdbcEscape</c> — the <c>{fn …}</c> spelling — has a
    /// different shape and is left to the range check to catch.
    /// </remarks>
    private Doc PrintLikePredicate(LikePredicate predicate) => PrintKeywordParts(
        predicate,
        predicate.FirstExpression,
        predicate.SecondExpression,
        predicate.EscapeExpression);

    private Doc PrintIn(InPredicate inPredicate)
    {
        if (inPredicate.Expression is null)
        {
            return Passthrough(inPredicate);
        }

        var keyword = Keyword(inPredicate.NotDefined ? "NOT IN" : "IN");

        if (inPredicate.Subquery is not null)
        {
            return Doc.Concat(Print(inPredicate.Expression), Doc.Text(" "), keyword, Doc.Text(" "), Print(inPredicate.Subquery));
        }

        if (inPredicate.Values.Count == 0 || !SeparatedBy(inPredicate.Values))
        {
            return Passthrough(inPredicate);
        }

        return Doc.Concat(
            Print(inPredicate.Expression),
            Doc.Text(" "),
            keyword,
            Doc.Text(" "),
            Doc.Group(Doc.Concat(
                Doc.Text("("),
                Doc.Indent(Doc.Concat(
                    Doc.SoftLine,
                    Doc.Join(Doc.Concat(Doc.Text(","), Doc.Line), inPredicate.Values.Select(Print)))),
                Doc.SoftLine,
                Doc.Text(")"))));
    }

    // --- scalar expressions ----------------------------------------------------------

    /// <summary>
    /// Flattens a chain of same-operator arithmetic or concatenation into one list rather than
    /// nested pairs.
    /// </summary>
    /// <remarks>
    /// The third handler to need this, after <see cref="PrintBooleanChain"/> and
    /// <see cref="PrintJoin"/>, and the only one that was left recursive. That inconsistency showed
    /// up as a measurement: the <see cref="MaxDepth"/> backstop was firing on real code — six
    /// `BinaryExpression` chains across four corpus files, worth 2,000 tokens — because a procedure
    /// that builds a long message with `N'…' + x + N'…' + y + …` nests one level per term, and
    /// eighty terms is not unusual. Depth is now proportional to real nesting rather than to term
    /// count.
    /// <para>Restricted to the same operator kind, like the boolean chain, and it stops at any node
    /// carrying comments — a node the spine walks past is never visited by <see cref="Print"/>, so
    /// its comments would be dropped. Each operator is still read from its own gap, so the emitted
    /// text is the author's regardless of how ScriptDom associated the terms.</para>
    /// </remarks>
    private Doc PrintBinaryExpression(BinaryExpression expression)
    {
        var spine = new List<BinaryExpression>();
        var current = expression;
        while (true)
        {
            if (current.FirstExpression is null || current.SecondExpression is null)
            {
                return Passthrough(expression);
            }

            spine.Add(current);

            if (current.FirstExpression is BinaryExpression left
                && left.BinaryExpressionType == expression.BinaryExpressionType
                && !_comments.HasAny(left))
            {
                current = left;
                continue;
            }

            break;
        }

        var parts = new List<Doc> { Print(spine[^1].FirstExpression) };
        for (var i = spine.Count - 1; i >= 0; i--)
        {
            var node = spine[i];
            var op = Doc.Text(TextBetween(node.FirstExpression, node.SecondExpression));

            // A comment written above the operator has to be emitted above it, and this chain hangs
            // its operators at the end of the previous line — so there is nowhere to put the comment
            // that does not also move the operator. This one link therefore leads with its operator
            // instead of trailing it:
            //
            //     … END                          … END
            //     + /*End non-columnstore */     /*End non-columnstore */
            //     CASE …                         + CASE …
            //
            // A layout decision changed by a comment, which the printer otherwise refuses to allow.
            // The exception is the one PrintJoin already makes for a comment before `ON`, on the
            // same grounds: where a comment's position carries meaning, that meaning outranks the
            // layout preference. See HoistLeadingBefore.
            var gapFrom = node.FirstExpression.LastTokenIndex + 1;
            var gapTo = EffectiveFirstToken(node.SecondExpression) - 1;
            var hoisted = HoistLeadingBefore(node.SecondExpression, gapFrom, gapTo);

            if (hoisted is not null)
            {
                parts.Add(Doc.HardLine);
                parts.Add(hoisted);
                parts.Add(op);
                parts.Add(Doc.Text(" "));
                parts.Add(Print(node.SecondExpression));
                continue;
            }

            // The trailing half of the same problem: a comment written after the left operand on its
            // own line is deferred as a line suffix, so appending the operator to that line prints it
            // ahead of the comment. Flush first, and this link leads with its operator too.
            var operatorIndex = FirstCodeToken(gapFrom, gapTo);
            if (operatorIndex > gapFrom && DeferredCommentIn(gapFrom, operatorIndex - 1))
            {
                parts.Add(Doc.LineSuffixBoundary);
                parts.Add(op);
                parts.Add(Doc.Text(" "));
                parts.Add(Print(node.SecondExpression));
                continue;
            }

            parts.Add(Doc.Text(" "));
            parts.Add(op);
            parts.Add(Doc.Line);
            parts.Add(Print(node.SecondExpression));
        }

        return Doc.Group(Doc.Concat(parts));
    }

    /// <summary>
    /// A comparison against a subquery with a quantifier: <c>x &gt; ANY (SELECT …)</c>,
    /// <c>x &lt;&gt; ALL (SELECT …)</c>, <c>x = SOME (SELECT …)</c>.
    /// </summary>
    /// <remarks>
    /// The operator and the quantifier are both enums on the node (<c>ComparisonType</c> and
    /// <c>SubqueryComparisonPredicateType</c>), so the run between the two operands is provably grammar
    /// and is recased — which matters because <c>SOME</c> lexes as an identifier.
    /// <para>The subquery is a <c>ScalarSubquery</c>, which brings its own parentheses.</para>
    /// </remarks>
    private Doc PrintSubqueryComparison(SubqueryComparisonPredicate predicate)
    {
        var expression = predicate.Expression;
        var subquery = predicate.Subquery;

        if (expression is null || subquery is null)
        {
            return Passthrough(predicate);
        }

        return Doc.Group(Doc.Concat(
            Print(expression),
            Doc.Text(" "),
            Keywords(expression.LastTokenIndex + 1, EffectiveFirstToken(subquery) - 1),
            Doc.Text(" "),
            Print(subquery)));
    }

    /// <summary><c>UPDATE(ColumnName)</c>, the predicate available inside a trigger body.</summary>
    /// <remarks>
    /// A boolean expression that looks like a call. The column name is Printed, so it keeps its exact
    /// spelling; only the <c>UPDATE</c> and its parenthesis are recased.
    /// </remarks>
    private Doc PrintUpdateCall(UpdateCall call)
    {
        var column = call.Identifier;

        if (column is null
            || !Compact(SignificantTextBetween(call.FirstTokenIndex, column.FirstTokenIndex - 1))
                .Equals("UPDATE(", StringComparison.OrdinalIgnoreCase)
            || SignificantTextBetween(column.LastTokenIndex + 1, call.LastTokenIndex) != ")")
        {
            return Passthrough(call);
        }

        return Doc.Concat(
            Keywords(call.FirstTokenIndex, column.FirstTokenIndex - 1),
            Print(column),
            Doc.Text(")"));
    }

    private Doc PrintParenthesis(ParenthesisExpression parenthesis) =>
        parenthesis.Expression is null || !IsPlainParenthesised(parenthesis, parenthesis.Expression)
        ? Passthrough(parenthesis)
        : Doc.Group(Doc.Concat(
            Doc.Text("("),
            Doc.Indent(Doc.Concat(Doc.SoftLine, Print(parenthesis.Expression))),
            Doc.SoftLine,
            Doc.Text(")")));

    private Doc PrintScalarSubquery(ScalarSubquery subquery) =>
        subquery.QueryExpression is null || !IsPlainParenthesised(subquery, subquery.QueryExpression)
        ? Passthrough(subquery)
        : Doc.Group(Doc.Concat(
            Doc.Text("("),
            Doc.Indent(Doc.Concat(Doc.SoftLine, Print(subquery.QueryExpression))),
            Doc.SoftLine,
            Doc.Text(")")));

    /// <summary>
    /// A function call, including its <c>DISTINCT</c> modifier and its <c>OVER</c> clause.
    /// </summary>
    /// <remarks>
    /// Both extras live outside the argument list, and both used to send the whole call to
    /// passthrough. <c>UniqueRowFilter</c> is an enum with no node, so <c>DISTINCT</c> appears only in
    /// the tokens between the opening parenthesis and the first argument; the <c>OVER</c> clause sits
    /// after the closing one. Window functions were the largest single guard left in real-world
    /// scripts at 17% of remaining verbatim text — and the guard was right to decline them, because
    /// emitting the argument list alone would have dropped the clause that gives the call its meaning.
    /// </remarks>
    private Doc PrintFunctionCall(FunctionCall call)
    {
        // The JSON constructors, `WITHIN GROUP`, `IGNORE NULLS` and the ANSI `TRIM` options each add
        // their own syntax around the arguments; none is modelled.
        if (call.FunctionName is null
            || call.JsonParameters.Count > 0
            || call.ReturnType.Count > 0
            || call.TrimOptions is not null
            || call.JsonOrderByClause is not null
            || call.IgnoreRespectNulls.Count > 0
            || call.AbsentOrNullOnNull.Count > 0)
        {
            return Passthrough(call);
        }

        // The separator between a call target and the function is `.` for a schema but `::` for a
        // type or user-defined type. Hard-coding `.` turned `t2::f()` into `t2::.f()`.
        var name = call.CallTarget is null
            ? Print(call.FunctionName)
            : Doc.Concat(
                Print(call.CallTarget),
                Doc.Text(TextBetween(call.CallTarget, call.FunctionName)),
                Print(call.FunctionName));

        // The argument list ends where the OVER clause begins, if there is one.
        var over = call.OverClause;
        var argsFrom = call.FunctionName.LastTokenIndex + 1;
        var argsEnd = over is null ? call.LastTokenIndex : EffectiveFirstToken(over) - 1;

        if (over is not null && SignificantTextBetween(over.LastTokenIndex + 1, call.LastTokenIndex).Length > 0)
        {
            return Passthrough(call);
        }

        Doc arguments;
        int closeParen;

        if (call.Parameters.Count == 0)
        {
            // Nothing ScriptDom models as an argument, which covers `()` and the `*` of `COUNT(*)`.
            // Emitted as written rather than reconstructed, since `*` is not a parameter node.
            var open = FirstSignificantToken(argsFrom, argsEnd);
            closeParen = LastTokenOfType(TSqlTokenType.RightParenthesis, open + 1, argsEnd);

            var between = Compact(SignificantTextBetween(argsFrom, closeParen));
            if (open < 0 || closeParen < 0 || !between.StartsWith('(') || !between.EndsWith(')'))
            {
                return Passthrough(call);
            }

            arguments = CasedTokens(argsFrom, closeParen);
        }
        else
        {
            var openEnd = EffectiveFirstToken(call.Parameters[0]) - 1;
            var open = Compact(SignificantTextBetween(argsFrom, openEnd));
            closeParen = FirstSignificantToken(call.Parameters[^1].LastTokenIndex + 1, argsEnd);

            // `(` or `(DISTINCT`. The inter-argument check is the one the corpus forced:
            // `TRIM('[]' FROM x)` passed both outer checks and was re-joined with a comma, producing
            // `TRIM('[]', x)`. Validating the ends of a list says nothing about its middle.
            if (!open.StartsWith('(')
                || closeParen < 0
                || SignificantTextBetween(closeParen, closeParen) != ")"
                || !SeparatedBy(call.Parameters))
            {
                return Passthrough(call);
            }

            arguments = Doc.Group(Doc.Concat(
                CasedTokens(argsFrom, openEnd),

                // No space after a bare `(`; one after `(DISTINCT`.
                open.EndsWith('(') ? Doc.Empty : Doc.Text(" "),
                Doc.Indent(Doc.Concat(
                    Doc.SoftLine,
                    Doc.Join(Doc.Concat(Doc.Text(","), Doc.Line), call.Parameters.Select(Print)))),
                Doc.SoftLine,
                Doc.Text(")")));
        }

        var parts = new List<Doc> { name, arguments };

        // A collation can follow the closing parenthesis and still be inside the call's range —
        // `RTRIM(x) COLLATE SQL_Latin1_General_CP1_CI_AS` — the same trait that puts one after
        // `CASE … END`. Located by finding the parenthesis rather than assuming the arguments run to
        // the end, so the clause is emitted instead of the whole call being declined.
        if (SignificantTextBetween(closeParen + 1, argsEnd).Length > 0)
        {
            // Measured from the end of the last argument, not from the parenthesis. A comment in
            // front of the `)` attaches to whatever follows it — `WITHIN GROUP`, a collation — and
            // that clause is emitted by the slice below, which starts *after* the parenthesis. The
            // comment falls between the two and was emitted by neither, so the file was refused.
            // Declining the call keeps it: verbatim text for one expression beats an unchanged file.
            var afterArguments = call.Parameters.Count > 0
                ? call.Parameters[^1].LastTokenIndex + 1
                : closeParen + 1;

            if (!NoCommentsIn(afterArguments, argsEnd))
            {
                return Passthrough(call);
            }

            parts.Add(Doc.Text(" "));
            parts.Add(CasedTokens(closeParen + 1, argsEnd));
        }

        if (over is not null)
        {
            parts.Add(Doc.Text(" "));
            parts.Add(Print(over));
        }

        return Doc.Concat(parts);
    }

    private Doc PrintCase(CaseExpression expression)
    {
        var whenClauses = expression switch
        {
            SearchedCaseExpression searched => searched.WhenClauses.Cast<WhenClause>().ToList(),
            SimpleCaseExpression simple => simple.WhenClauses.Cast<WhenClause>().ToList(),
            _ => null,
        };

        if (whenClauses is null || whenClauses.Count == 0)
        {
            return Passthrough(expression);
        }

        var head = new List<Doc> { Keyword("CASE") };
        if (expression is SimpleCaseExpression { InputExpression: not null } simpleCase)
        {
            head.Add(SeparatorBefore(simpleCase.InputExpression));
            head.Add(Print(simpleCase.InputExpression));
        }

        var body = new List<Doc>();
        foreach (var clause in whenClauses)
        {
            body.Add(Doc.Line);
            body.Add(Print(clause));
        }

        if (expression.ElseExpression is not null)
        {
            body.Add(Doc.Line);
            body.Add(Keyword("ELSE"));
            body.Add(SeparatorBefore(expression.ElseExpression));
            body.Add(Print(expression.ElseExpression));
        }

        // `CASE … END COLLATE Latin1_General_BIN2` puts a collation inside the expression's range, so
        // the `END` is *located* and anything after it emitted as a slice rather than the whole
        // expression being declined. Requiring the tail to be exactly `END` cost 546 tokens of
        // real-world text across three files — and the collation is a name, so the slice is cased per
        // token rather than recased.
        var lastChild = (TSqlFragment?)expression.ElseExpression ?? whenClauses[^1];
        var endIndex = FirstSignificantToken(lastChild.LastTokenIndex + 1, expression.LastTokenIndex);

        if (endIndex < 0
            || !SignificantTextBetween(endIndex, endIndex).Equals("END", StringComparison.OrdinalIgnoreCase))
        {
            return Passthrough(expression);
        }

        // A CASE with more than one WHEN always breaks, fitting or not. Two branches inline read as a
        // run of keywords to be scanned for the boundaries between them; stacked, they are a column
        // to read down, and adding a branch is a one-line diff rather than a reflow. Same argument
        // the CTE body carries: some constructs are unreadable crammed onto a line however narrow
        // they happen to be.
        //
        // One WHEN is the deliberate exception, because of an idiom that is counting rather than
        // branching and is everywhere:
        //
        //     SUM(CASE WHEN o.Status = 'shipped' THEN 1 ELSE 0 END) AS Shipped
        //
        // Forcing that open would be a plain regression, so a single-branch CASE keeps the ordinary
        // fit-first treatment.
        return Doc.Group(
            shouldBreak: whenClauses.Count > 1,
            contents: Doc.Concat(
                Doc.Concat(head),
                Doc.Indent(Doc.Concat(body)),
                Doc.Line,
                Keyword("END"),
                endIndex == expression.LastTokenIndex
                    ? Doc.Empty
                    : Doc.Concat(Doc.Text(" "), CasedTokens(endIndex + 1, expression.LastTokenIndex))));
    }

    private Doc PrintWhenClause(WhenClause clause)
    {
        var condition = clause switch
        {
            SearchedWhenClause searched => (TSqlFragment?)searched.WhenExpression,
            SimpleWhenClause simple => simple.WhenExpression,
            _ => null,
        };

        if (condition is null || clause.ThenExpression is null)
        {
            return Passthrough(clause);
        }

        return Doc.Group(Doc.Concat(
            Keyword("WHEN"),
            SeparatorBefore(condition),
            Print(condition),
            Doc.Text(" "),
            Keyword("THEN"),
            SeparatorBefore(clause.ThenExpression),
            Print(clause.ThenExpression)));
    }
}
