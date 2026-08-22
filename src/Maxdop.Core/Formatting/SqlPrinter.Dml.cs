using Maxdop.Core.Printing;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Formatting;

/// <summary>
/// The row-modifying statements — <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c> — the
/// <c>OUTPUT</c> clause they share, and variable assignment (<c>SET @x = …</c>,
/// <c>SELECT @x = …</c>).
/// </summary>
/// <remarks>
/// Picked from corpus data: <c>INSERT</c> and <c>UPDATE</c> were the two largest
/// blocks of text the formatter was still leaving verbatim, and in both cases most of it was
/// there because <c>OUTPUT</c> was deferred rather than because the statement was hard. Handling
/// the clause once, on the shared <c>DataModificationSpecification</c> base, unblocks all three.
/// <para><c>DELETE</c> comes along for free: strip the <c>SET</c> list from <c>UPDATE</c> and the
/// two are the same statement, down to the <c>DELETE FROM t FROM t JOIN u</c> form where the first
/// <c>FROM</c> belongs to the head slice and the second is a real <c>FromClause</c>.</para>
/// <para>Assignment operators come from the token stream rather than from
/// <c>AssignmentKind</c>: that covers <c>=</c>, <c>+=</c>, <c>-=</c>, <c>*=</c>, <c>/=</c>,
/// <c>%=</c>, <c>&amp;=</c>, <c>^=</c> and <c>|=</c> with no enum mapping to get wrong, and
/// preserves exactly what the author wrote.</para>
/// </remarks>
public sealed partial class SqlPrinter
{
    // --- INSERT ----------------------------------------------------------------------

    private Doc PrintInsert(InsertStatement statement)
    {
        var specification = statement.InsertSpecification;
        if (specification?.Target is null
            || specification.InsertSource is null
            || !TryGetOutputClause(specification, out var output)
            || !SeparatedBy(specification.Columns))
        {
            return Passthrough(statement);
        }

        // The column list, when present, sits between the target and whatever comes next — which is
        // the OUTPUT clause if there is one, and the insert source otherwise.
        var afterColumns = output ?? specification.InsertSource;

        if (specification.Columns.Count > 0)
        {
            // The gaps around the column list must be exactly the parentheses, or the statement
            // has something this handler does not model.
            if (SignificantTextBetween(
                    specification.Target.LastTokenIndex + 1,
                    specification.Columns[0].FirstTokenIndex - 1) != "("
                || SignificantTextBetween(
                    specification.Columns[^1].LastTokenIndex + 1,
                    EffectiveFirstToken(afterColumns) - 1) != ")")
            {
                return Passthrough(statement);
            }
        }
        else if (SignificantTextBetween(
                     specification.Target.LastTokenIndex + 1,
                     EffectiveFirstToken(afterColumns) - 1).Length > 0)
        {
            // No column list means the target must abut what follows it. Previously unchecked,
            // which would silently drop anything the handler had not thought of.
            return Passthrough(statement);
        }

        // Everything from the OUTPUT clause to the end of the statement must be accounted for; a
        // stray extra semicolon after the source was being dropped.
        if (!ClausesAbut(output, specification.InsertSource))
        {
            return Passthrough(statement);
        }

        if (!TryPrintOptionClause(
                statement,
                statement.OptimizerHints,
                specification.InsertSource,
                out var optionClause))
        {
            return Passthrough(statement);
        }

        if (!TryPrintCtes(statement, out var ctePrologue))
        {
            return Passthrough(statement);
        }

        // `INSERT`, `INSERT INTO`, `INSERT TOP (5) INTO` — read rather than reconstructed, so the
        // optional INTO and any TOP filter survive as written. Sliced from after any CTE prologue,
        // because with `WITH … INSERT` the statement's own range starts at the `WITH`.
        var parts = new List<Doc>
        {
            CasedTokens(CteBodyStart(statement), specification.Target.FirstTokenIndex - 1),
            Doc.Text(" "),
            Print(specification.Target),
        };

        if (specification.Columns.Count > 0)
        {
            parts.Add(Doc.Group(Doc.Concat(
                Doc.Text(" ("),
                Doc.Indent(Doc.Concat(Doc.SoftLine, JoinList(specification.Columns))),
                Doc.SoftLine,
                Doc.Text(")"))));
        }

        if (output is not null)
        {
            parts.Add(Doc.HardLine);
            parts.Add(Print(output));
        }

        parts.Add(Doc.HardLine);
        parts.Add(Print(specification.InsertSource));

        // Same reason as UPDATE: the specification is read from but never Printed. The CTE
        // prologue stays outside the wrapper so the specification's own leading comments land
        // after the WITH rather than above it.
        // The option clause stays *outside* the wrapper, for the mirror image of the reason the CTE
        // prologue does. The specification's range ends before `OPTION`, so a comment trailing it
        // belongs before the clause; emitted inside the wrapper it was flushed after the clause and
        // the terminator, and `AND h.[object_id] IS NULL /*don't duplicate the prior check.*/` came
        // back reading as a remark about `OPTION (RECOMPILE);`. Ten occurrences in one corpus file,
        // and invisible to every gate — the comment is neither lost nor reordered against the other
        // comments, only against the code.
        return Doc.Concat(ctePrologue, WithComments(specification, Doc.Concat(parts)), optionClause);
    }

    private Doc PrintValuesInsertSource(ValuesInsertSource source)
    {
        // `DEFAULT VALUES` has no rows at all.
        if (source.IsDefaultValues || source.RowValues.Count == 0)
        {
            return CasedTokens(source.FirstTokenIndex, source.LastTokenIndex);
        }

        if (!SeparatedBy(source.RowValues))
        {
            return Passthrough(source);
        }

        var keyword = CasedTokens(source.FirstTokenIndex, source.RowValues[0].FirstTokenIndex - 1);
        var rows = Doc.Join(Doc.Concat(Doc.Text(","), Doc.Line), source.RowValues.Select(Print));

        // A single row stays on the VALUES line; several break one per line, which is how a
        // multi-row insert stays readable and diffs cleanly.
        return source.RowValues.Count == 1
            ? Doc.Group(Doc.Concat(keyword, Doc.Text(" "), rows))
            : Doc.Group(Doc.Concat(keyword, Doc.Indent(Doc.Concat(Doc.Line, rows))), shouldBreak: true);
    }

    private Doc PrintRowValue(RowValue row)
    {
        if (row.ColumnValues.Count == 0 || !SeparatedBy(row.ColumnValues))
        {
            return Passthrough(row);
        }

        return Doc.Group(Doc.Concat(
            Doc.Text("("),
            Doc.Indent(Doc.Concat(Doc.SoftLine, Doc.Join(Doc.Concat(Doc.Text(","), Doc.Line), row.ColumnValues.Select(Print)))),
            Doc.SoftLine,
            Doc.Text(")")));
    }

    private Doc PrintSelectInsertSource(SelectInsertSource source) =>
        source.Select is null ? Passthrough(source) : Print(source.Select);

    // --- UPDATE and DELETE ------------------------------------------------------------

    private Doc PrintUpdate(UpdateStatement statement)
    {
        // Split one condition per bail-out rather than a single compound `if`, so the corpus tool's
        // guard ranking can name which one fires. As one condition they were indistinguishable, and
        // the answer turned out to be the CTE clause rather than anything about UPDATE itself.
        var specification = statement.UpdateSpecification;
        if (specification?.Target is null
            || specification.SetClauses.Count == 0
            || !TryGetOutputClause(specification, out var output)
            || !SeparatedBy(specification.SetClauses))
        {
            return Passthrough(statement);
        }

        if (!ClausesAbut(
                specification.SetClauses[^1],
                output,
                specification.FromClause,
                specification.WhereClause))
        {
            return Passthrough(statement);
        }

        if (!TryPrintOptionClause(statement, statement.OptimizerHints, LastOf(
                specification.SetClauses[^1],
                output,
                specification.FromClause,
                specification.WhereClause), out var optionClause))
        {
            return Passthrough(statement);
        }

        if (!TryPrintCtes(statement, out var ctePrologue))
        {
            return Passthrough(statement);
        }

        // `UPDATE` / `UPDATE TOP (5)`, then `SET` — both read rather than reconstructed, so a TOP
        // filter and any spelling of the keyword survive as written.
        var parts = new List<Doc>
        {
            CasedTokens(CteBodyStart(statement), specification.Target.FirstTokenIndex - 1),
            Doc.Text(" "),
            Print(specification.Target),
            Doc.HardLine,
            Doc.Group(Doc.Concat(
                CasedTokens(
                    specification.Target.LastTokenIndex + 1,
                    specification.SetClauses[0].FirstTokenIndex - 1),
                Doc.Indent(Doc.Concat(
                    Doc.Line,
                    JoinList(specification.SetClauses))))),
        };

        AppendModificationTail(parts, specification, output);

        // The specification is an intermediate node this handler reads children from but never
        // Prints, so its own attached comments need emitting explicitly. A comment between the
        // statement's two semicolons in the `;WITH` idiom attaches here.
        // The option clause stays *outside* the wrapper, for the mirror image of the reason the CTE
        // prologue does. The specification's range ends before `OPTION`, so a comment trailing it
        // belongs before the clause; emitted inside the wrapper it was flushed after the clause and
        // the terminator, and `AND h.[object_id] IS NULL /*don't duplicate the prior check.*/` came
        // back reading as a remark about `OPTION (RECOMPILE);`. Ten occurrences in one corpus file,
        // and invisible to every gate — the comment is neither lost nor reordered against the other
        // comments, only against the code.
        return Doc.Concat(ctePrologue, WithComments(specification, Doc.Concat(parts)), optionClause);
    }

    private Doc PrintDelete(DeleteStatement statement)
    {
        var specification = statement.DeleteSpecification;
        if (specification?.Target is null
            || !TryGetOutputClause(specification, out var output)
            || !ClausesAbut(
                specification.Target,
                output,
                specification.FromClause,
                specification.WhereClause)
            || !TryPrintOptionClause(statement, statement.OptimizerHints, LastOf(
                specification.Target,
                output,
                specification.FromClause,
                specification.WhereClause), out var optionClause)
            || !TryPrintCtes(statement, out var ctePrologue))
        {
            return Passthrough(statement);
        }

        // `DELETE`, `DELETE FROM`, `DELETE TOP (5) FROM` — all one slice. The `FROM` here is part of
        // the DELETE syntax and is *not* the same node as `specification.FromClause`: the join form
        // `DELETE FROM t FROM t JOIN u ON …` has both, and reconstructing either would lose one.
        var parts = new List<Doc>
        {
            CasedTokens(CteBodyStart(statement), specification.Target.FirstTokenIndex - 1),
            Doc.Text(" "),
            Print(specification.Target),
        };

        AppendModificationTail(parts, specification, output);

        // The option clause stays *outside* the wrapper, for the mirror image of the reason the CTE
        // prologue does. The specification's range ends before `OPTION`, so a comment trailing it
        // belongs before the clause; emitted inside the wrapper it was flushed after the clause and
        // the terminator, and `AND h.[object_id] IS NULL /*don't duplicate the prior check.*/` came
        // back reading as a remark about `OPTION (RECOMPILE);`. Ten occurrences in one corpus file,
        // and invisible to every gate — the comment is neither lost nor reordered against the other
        // comments, only against the code.
        return Doc.Concat(ctePrologue, WithComments(specification, Doc.Concat(parts)), optionClause);
    }

    /// <summary>
    /// The <c>OUTPUT</c> / <c>FROM</c> / <c>WHERE</c> tail shared by <c>UPDATE</c> and
    /// <c>DELETE</c>, each clause on its own line.
    /// </summary>
    private void AppendModificationTail(
        List<Doc> parts,
        UpdateDeleteSpecificationBase specification,
        TSqlFragment? output)
    {
        foreach (var clause in new[] { output, specification.FromClause, (TSqlFragment?)specification.WhereClause })
        {
            if (clause is not null)
            {
                parts.Add(Doc.HardLine);
                parts.Add(Print(clause));
            }
        }
    }

    private Doc PrintAssignmentSetClause(AssignmentSetClause clause)
    {
        if (clause.NewValue is null)
        {
            return Passthrough(clause);
        }

        var parts = new List<Doc>();

        // A set clause may assign to a column, to a variable, or — the case that caught this out —
        // to *both*: `SET @a = c1 += NULL` updates the column and captures the result in one go.
        // Picking one target silently dropped the other.
        if (clause.Variable is not null && clause.Column is not null)
        {
            parts.Add(Print(clause.Variable));
            parts.Add(Doc.Text(" "));
            parts.Add(Doc.Text(TextBetween(clause.Variable, clause.Column)));
            parts.Add(Doc.Text(" "));
        }

        var target = (TSqlFragment?)clause.Column ?? clause.Variable;
        if (target is null)
        {
            return Passthrough(clause);
        }

        parts.Add(Print(target));
        parts.Add(Doc.Text(" "));
        parts.Add(Doc.Text(TextBetween(target, clause.NewValue)));
        parts.Add(SeparatorBefore(clause.NewValue));
        parts.Add(Print(clause.NewValue));

        return Doc.Concat(parts);
    }

    // --- MERGE -------------------------------------------------------------------------

    /// <summary>
    /// <c>MERGE … USING … ON … WHEN MATCHED THEN … WHEN NOT MATCHED THEN …</c>
    /// </summary>
    /// <remarks>
    /// The last statement named in the MVP scope without a handler. It reuses almost everything the
    /// other DML statements already have — the target, the <c>OUTPUT</c> clause, <c>OPTION</c> hints,
    /// set clauses, and <c>VALUES</c> rows — so what is actually new is the action-clause list.
    /// <para><b>A <c>MergeActionClause</c>'s range excludes its own <c>WHEN … THEN</c>.</b> The clause
    /// begins at its search condition, or at its action when it has none, so
    /// <c>WHEN NOT MATCHED BY TARGET THEN</c> belongs to no node at all — the same trait as
    /// <c>ExistsPredicate</c> starting at its parenthesis. Reading each of those runs from the gap in
    /// front of its clause is what keeps them, and is why this handler never needs to know the
    /// <c>MergeCondition</c> enum.</para>
    /// </remarks>
    private Doc PrintMerge(MergeStatement statement)
    {
        var specification = statement.MergeSpecification;
        if (specification?.Target is null
            || specification.TableReference is null
            || specification.SearchCondition is null
            || specification.ActionClauses.Count == 0
            || !TryGetOutputClause(specification, out var output))
        {
            return Passthrough(statement);
        }

        var clauses = specification.ActionClauses;
        var lastClause = clauses[^1];

        if (!ClausesAbut(lastClause, output)
            || !TryPrintOptionClause(
                statement,
                statement.OptimizerHints,
                LastOf(lastClause, output),
                out var optionClause)
            || !TryPrintCtes(statement, out var ctePrologue))
        {
            return Passthrough(statement);
        }

        // `MERGE`, `MERGE INTO`, `MERGE TOP (5) INTO` — read rather than reconstructed, so the optional
        // INTO and any TOP filter survive as written.
        var parts = new List<Doc>
        {
            Keywords(CteBodyStart(statement), specification.Target.FirstTokenIndex - 1),
            Doc.Text(" "),
            Print(specification.Target),
        };

        // `AS t` on the target, then `USING <source>` and `ON <condition>` each on their own line.
        // Every keyword between them is read from its gap.
        TSqlFragment beforeSource = specification.Target;
        if (specification.TableAlias is not null)
        {
            if (!AppendKeywordGap(parts, specification.Target, specification.TableAlias, Doc.Text(" ")))
            {
                return Passthrough(statement);
            }

            parts.Add(Print(specification.TableAlias));
            beforeSource = specification.TableAlias;
        }

        if (!AppendKeywordGap(parts, beforeSource, specification.TableReference, Doc.HardLine))
        {
            return Passthrough(statement);
        }

        parts.Add(Print(specification.TableReference));

        // `ON <condition>` indented under the source, which is how a join's ON is laid out — the two
        // read the same way and mean the same thing.
        var onFrom = specification.TableReference.LastTokenIndex + 1;
        var onTo = EffectiveFirstToken(specification.SearchCondition) - 1;

        if (SignificantTextBetween(onFrom, onTo).Length == 0)
        {
            return Passthrough(statement);
        }

        parts.Add(Doc.Indent(Doc.Concat(
            Doc.HardLine,
            Keywords(onFrom, onTo),
            Doc.Text(" "),
            Doc.Indent(Print(specification.SearchCondition)))));

        // Each `WHEN … THEN` run lives in the gap in front of its clause, because the clause's own
        // range starts after it.
        TSqlFragment previous = specification.SearchCondition;
        foreach (var clause in clauses)
        {
            if (!AppendKeywordGap(parts, previous, clause, Doc.HardLine))
            {
                return Passthrough(statement);
            }

            parts.Add(Print(clause));
            previous = clause;
        }

        if (output is not null)
        {
            parts.Add(Doc.HardLine);
            parts.Add(Print(output));
        }

        parts.Add(optionClause);
        return Doc.Concat(ctePrologue, WithComments(specification, Doc.Concat(parts)));
    }

    /// <summary>
    /// Appends the keyword run between two children — <c>AS</c>, <c>USING</c>, <c>ON</c>,
    /// <c>WHEN NOT MATCHED BY SOURCE THEN</c> — preceded by the given separator.
    /// </summary>
    /// <remarks>
    /// Returns false when the gap is empty or holds a comment, either of which means a shape this
    /// handler cannot account for. A keyword position: none of these runs can contain a name.
    /// </remarks>
    private bool AppendKeywordGap(List<Doc> parts, TSqlFragment left, TSqlFragment right, Doc separator)
    {
        var from = left.LastTokenIndex + 1;
        var to = EffectiveFirstToken(right) - 1;

        if (SignificantTextBetween(from, to).Length == 0 || !NoCommentsIn(from, to))
        {
            return false;
        }

        parts.Add(separator);
        parts.Add(Keywords(from, to));
        parts.Add(Doc.Text(" "));
        return true;
    }

    /// <summary><c>[&lt;condition&gt; THEN] &lt;action&gt;</c> — the body of one <c>WHEN</c> clause.</summary>
    /// <remarks>
    /// The <c>WHEN …</c> prefix is emitted by <see cref="PrintMerge"/> from the gap in front of this
    /// node, since the node's range does not cover it. What is left is an optional
    /// <c>AND &lt;condition&gt; THEN</c> and the action itself, which the shared parts helper joins.
    /// </remarks>
    private Doc PrintMergeActionClause(MergeActionClause clause)
    {
        var action = clause.Action;
        if (action is null)
        {
            return Passthrough(clause);
        }

        var docs = new List<Doc>();

        if (clause.SearchCondition is not null)
        {
            // `AND <condition> THEN`, with the `THEN` read from the gap in front of the action.
            var from = clause.SearchCondition.LastTokenIndex + 1;
            var to = EffectiveFirstToken(action) - 1;

            if (SignificantTextBetween(from, to).Length == 0 || !NoCommentsIn(from, to))
            {
                return Passthrough(clause);
            }

            docs.Add(Print(clause.SearchCondition));
            docs.Add(Doc.Text(" "));
            docs.Add(Keywords(from, to));
        }
        else if (EffectiveFirstToken(action) != clause.FirstTokenIndex)
        {
            // No condition means the clause is nothing but its action; anything else in front of it is
            // a shape this does not model.
            return Passthrough(clause);
        }

        // The action on its own indented line, which is how MERGE is conventionally written and keeps a
        // long `INSERT (…) VALUES (…)` from being pushed off the margin by the WHEN clause in front of
        // it. The printer trims the trailing blank left by the caller's keyword run.
        docs.Add(Doc.Indent(Doc.Concat(Doc.HardLine, Print(action))));
        return Doc.Concat(docs);
    }

    /// <summary><c>UPDATE SET a = s.a, b = s.b</c></summary>
    private Doc PrintUpdateMergeAction(UpdateMergeAction action) =>
        action.SetClauses.Count == 0 || !SeparatedBy(action.SetClauses)
            ? Passthrough(action)
            : Doc.Group(Doc.Concat(
                Keywords(action.FirstTokenIndex, action.SetClauses[0].FirstTokenIndex - 1),
                Doc.Indent(Doc.Concat(
                    Doc.Line,
                    JoinList(action.SetClauses)))));

    /// <summary><c>INSERT (a, b) VALUES (s.a, s.b)</c>, <c>INSERT DEFAULT VALUES</c></summary>
    private Doc PrintInsertMergeAction(InsertMergeAction action)
    {
        var source = action.Source;
        if (source is null || !SeparatedBy(action.Columns))
        {
            return Passthrough(action);
        }

        if (action.Columns.Count == 0)
        {
            // `INSERT VALUES (…)` or `INSERT DEFAULT VALUES` — nothing between the keyword and the
            // source to account for.
            return SignificantTextBetween(action.FirstTokenIndex, EffectiveFirstToken(source) - 1)
                .Equals("INSERT", StringComparison.OrdinalIgnoreCase)
                ? Doc.Concat(Keyword("INSERT"), Doc.Text(" "), Print(source))
                : Passthrough(action);
        }

        // Compared without case, because the author's `insert` is what is in the token stream — the
        // third time an ordinal comparison against a keyword literal has silently sent a construct to
        // passthrough.
        if (!Compact(SignificantTextBetween(action.FirstTokenIndex, action.Columns[0].FirstTokenIndex - 1))
                .Equals("INSERT(", StringComparison.OrdinalIgnoreCase)
            || SignificantTextBetween(action.Columns[^1].LastTokenIndex + 1, EffectiveFirstToken(source) - 1) != ")")
        {
            return Passthrough(action);
        }

        return Doc.Concat(
            Keyword("INSERT"),
            Doc.Group(Doc.Concat(
                Doc.Text(" ("),
                Doc.Indent(Doc.Concat(Doc.SoftLine, JoinList(action.Columns))),
                Doc.SoftLine,
                Doc.Text(")"))),
            Doc.Text(" "),
            Print(source));
    }

    /// <summary><c>DELETE</c></summary>
    private Doc PrintDeleteMergeAction(DeleteMergeAction action) =>
        Keywords(action.FirstTokenIndex, action.LastTokenIndex);

    // --- OUTPUT ----------------------------------------------------------------------

    /// <summary>
    /// The statement's <c>OUTPUT</c> clause in whichever of its two spellings is present, or null
    /// when there is none. False when the shape is not one this printer models.
    /// </summary>
    /// <remarks>
    /// ScriptDom models <c>OUTPUT …</c> and <c>OUTPUT … INTO …</c> as two unrelated properties, so
    /// a handler that reads one and not the other silently drops the clause. Reading both through
    /// one accessor is what makes that impossible.
    /// </remarks>
    private static bool TryGetOutputClause(
        DataModificationSpecification specification,
        out TSqlFragment? output)
    {
        output = (TSqlFragment?)specification.OutputClause ?? specification.OutputIntoClause;

        // Both at once is not legal T-SQL, so if it ever appears the parse means something other
        // than what this reads. Bail rather than pick one.
        return specification.OutputClause is null || specification.OutputIntoClause is null;
    }

    private Doc PrintOutputClause(OutputClause clause) =>
        IsOutputColumnList(clause, clause.SelectColumns)
        && SignificantTextBetween(clause.SelectColumns[^1].LastTokenIndex + 1, clause.LastTokenIndex).Length == 0
            ? PrintOutputColumns(clause, clause.SelectColumns)
            : Passthrough(clause);

    private Doc PrintOutputIntoClause(OutputIntoClause clause)
    {
        if (clause.IntoTable is null
            || !IsOutputColumnList(clause, clause.SelectColumns)
            || !SeparatedBy(clause.IntoTableColumns)
            || !TextBetween(clause.SelectColumns[^1], clause.IntoTable)
                .Equals("INTO", StringComparison.OrdinalIgnoreCase))
        {
            return Passthrough(clause);
        }

        // `INTO #log` may or may not be followed by a column list, and the clause's range ends at
        // whichever it is.
        var hasColumns = clause.IntoTableColumns.Count > 0;
        if (hasColumns
            ? SignificantTextBetween(
                  clause.IntoTable.LastTokenIndex + 1,
                  clause.IntoTableColumns[0].FirstTokenIndex - 1) != "("
              || SignificantTextBetween(
                  clause.IntoTableColumns[^1].LastTokenIndex + 1,
                  clause.LastTokenIndex) != ")"
            : SignificantTextBetween(clause.IntoTable.LastTokenIndex + 1, clause.LastTokenIndex).Length > 0)
        {
            return Passthrough(clause);
        }

        var parts = new List<Doc>
        {
            PrintOutputColumns(clause, clause.SelectColumns),
            Doc.Line,
            Keywords(clause.SelectColumns[^1].LastTokenIndex + 1, EffectiveFirstToken(clause.IntoTable) - 1),
            Doc.Text(" "),
            Print(clause.IntoTable),
        };

        if (hasColumns)
        {
            parts.Add(Doc.Group(Doc.Concat(
                Doc.Text(" ("),
                Doc.Indent(Doc.Concat(
                    Doc.SoftLine,
                    JoinList(clause.IntoTableColumns))),
                Doc.SoftLine,
                Doc.Text(")"))));
        }

        return Doc.Group(Doc.Concat(parts));
    }

    /// <summary>
    /// Whether the clause really begins <c>OUTPUT &lt;comma-separated columns&gt;</c>.
    /// </summary>
    private bool IsOutputColumnList(TSqlFragment clause, IList<SelectElement> columns) =>
        columns.Count > 0
        && SeparatedBy(columns)
        && SignificantTextBetween(clause.FirstTokenIndex, columns[0].FirstTokenIndex - 1)
            .Equals("OUTPUT", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>OUTPUT</c> and its column list, laid out like a select list.
    /// </summary>
    /// <remarks>
    /// <c>OUTPUT</c> is a non-reserved word that lexes as an identifier, so it needs
    /// <see cref="Keywords"/> rather than <see cref="Keyword"/>: the region is read from the tokens,
    /// and because nothing but the keyword can appear there it is recased to match the rest.
    /// </remarks>
    private Doc PrintOutputColumns(TSqlFragment clause, IList<SelectElement> columns) => Doc.Group(Doc.Concat(
        Keywords(clause.FirstTokenIndex, columns[0].FirstTokenIndex - 1),
        Doc.Indent(Doc.Concat(Doc.Line, JoinList(columns)))));

    // --- assignment ------------------------------------------------------------------

    private Doc PrintSetVariable(SetVariableStatement statement)
    {
        // `SET @x.modify(N'…')` — a method call on a variable rather than an assignment. Common in
        // XML-shredding code, and structurally a different statement: there is no `=` and no
        // right-hand expression, just a name, a dotted method and its arguments. The parts helper
        // covers it, with the `.` and the parentheses coming from the gaps.
        if (statement.Expression is null && statement.Identifier is not null)
        {
            return SeparatedBy(statement.Parameters)
                ? PrintPartsInTokenOrder(
                    statement,
                    [statement.Variable, statement.Identifier, .. statement.Parameters])
                : Passthrough(statement);
        }

        // `SET @c = CURSOR FORWARD_ONLY STATIC FOR SELECT …` — the cursor-variable assignment. The
        // definition is its own node with its own handler, and the `= CURSOR` in front of it belongs to
        // no node: a CursorDefinition's range starts at its first *option*, so even the `CURSOR` keyword
        // is in the gap.
        if (statement.CursorDefinition is { } cursor)
        {
            return statement.Variable is null
                || statement.Expression is not null
                || statement.Identifier is not null
                || statement.Parameters.Count > 0
                    ? Passthrough(statement)
                    : Doc.Concat(
                        CasedTokens(statement.FirstTokenIndex, statement.Variable.FirstTokenIndex - 1),
                        Doc.Text(" "),
                        Print(statement.Variable),
                        Doc.Text(" "),
                        Keywords(statement.Variable.LastTokenIndex + 1, EffectiveFirstToken(cursor) - 1),
                        Doc.Text(" "),
                        Print(cursor));
        }

        // The named-parameter forms have shapes this does not model.
        if (statement.Variable is null
            || statement.Expression is null
            || statement.FunctionCallExists
            || statement.Identifier is not null
            || statement.Parameters.Count > 0)
        {
            return Passthrough(statement);
        }

        // Everything before the variable: `SET`, or `SELECT` in the rare assignment spelling.
        var keyword = CasedTokens(statement.FirstTokenIndex, statement.Variable.FirstTokenIndex - 1);

        return Doc.Group(Doc.Concat(
            keyword,
            Doc.Text(" "),
            Print(statement.Variable),
            Doc.Text(" "),
            Doc.Text(TextBetween(statement.Variable, statement.Expression)),
            SeparatorBefore(statement.Expression),
            Doc.Indent(Print(statement.Expression))));
    }

    private Doc PrintSelectSetVariable(SelectSetVariable element)
    {
        if (element.Variable is null || element.Expression is null)
        {
            return Passthrough(element);
        }

        return Doc.Concat(
            Print(element.Variable),
            Doc.Text(" "),
            Doc.Text(TextBetween(element.Variable, element.Expression)),
            Doc.Text(" "),
            Print(element.Expression));
    }

    // --- BULK INSERT ------------------------------------------------------------------

    /// <summary>
    /// <c>BULK INSERT dbo.t FROM 'f.csv' WITH (FIELDTERMINATOR = ',', FIRSTROW = 2)</c>.
    /// </summary>
    /// <remarks>
    /// The target and the source file go on their own lines, as the three clauses of any other DML
    /// statement do. The option list is grouped rather than always broken: <c>WITH (TABLOCK)</c> is
    /// one short clause and reads worse spread over three lines, while the load options a real import
    /// carries do not fit and break one per line.
    /// <para>Only <c>BULK INSERT</c>, not its <c>BulkInsertBase</c> sibling <c>INSERT BULK</c>: the two
    /// share a base and nothing else, since <c>INSERT BULK</c> takes a column definition list in place
    /// of a source file. Dispatching on the base would mean a handler whose first act is to check which
    /// of the two it actually got.</para>
    /// </remarks>
    private Doc PrintBulkInsert(BulkInsertStatement statement)
    {
        var target = statement.To;
        var source = statement.From;

        if (target is null || source is null)
        {
            return Passthrough(statement);
        }

        // `BULK INSERT` — reserved `INSERT` next to non-reserved `BULK`, so per-token recasing, and no
        // object name can appear before the target.
        var head = Doc.Concat(
            Keywords(statement.FirstTokenIndex, EffectiveFirstToken(target) - 1),
            Doc.Text(" "),
            Print(target),
            Doc.HardLine,
            CasedTokensBetween(target, source),
            Doc.Text(" "),
            Print(source));

        if (statement.Options.Count == 0)
        {
            // Nothing models a tail here, so there must not be one.
            return NothingAfter(source, statement) ? head : Passthrough(statement);
        }

        var end = RangeEndBeforeTerminators(statement);
        var headEnd = EffectiveFirstToken(statement.Options[0]) - 1;

        // The same three-part proof the other option-list handlers use: the gap before the first
        // option must be exactly the clause's own keyword and parenthesis, the gap after the last must
        // be exactly its closing one, and no comment may sit in either — CasedTokens strips comments,
        // and neither gap belongs to a node that would emit them.
        if (!SeparatedBy(statement.Options)
            || !Compact(SignificantTextBetween(source.LastTokenIndex + 1, headEnd))
                .Equals("WITH(", StringComparison.OrdinalIgnoreCase)
            || SignificantTextBetween(statement.Options[^1].LastTokenIndex + 1, end) != ")")
        {
            return Passthrough(statement);
        }

        return Doc.Concat(
            head,
            Doc.HardLine,
            Doc.Group(Doc.Concat(
                Keywords(source.LastTokenIndex + 1, headEnd),
                Doc.Indent(Doc.Concat(
                    Doc.SoftLine,
                    Doc.Join(Doc.Concat(Doc.Text(","), Doc.Line), statement.Options.Select(Print)))),
                Doc.SoftLine,
                Doc.Text(")"))));
    }

    /// <summary>
    /// A bare load option — <c>TABLOCK</c>, <c>KEEPNULLS</c>, <c>CHECK_CONSTRAINTS</c>.
    /// </summary>
    /// <remarks>
    /// Read from the tokens rather than mapped from <c>BulkInsertOptionKind</c>: the enum spells
    /// several of these differently from the source (<c>TabLock</c>, <c>CheckConstraints</c>), so a
    /// mapping would be a table of thirty spellings to keep correct for no gain. Every word here is a
    /// keyword position — an option name cannot be an object name — so the slice is recased.
    /// </remarks>
    private Doc PrintBulkInsertOption(BulkInsertOption option) =>
        Keywords(option.FirstTokenIndex, option.LastTokenIndex);

    /// <summary><c>FIELDTERMINATOR = ','</c>, <c>FIRSTROW = 2</c>.</summary>
    /// <remarks>
    /// Normalised to one space either side of the <c>=</c>, which is the one piece of spacing in a
    /// load option worth an opinion. The literal is Printed, so a terminator like <c>'\t'</c> keeps its
    /// exact text.
    /// </remarks>
    private Doc PrintLiteralBulkInsertOption(LiteralBulkInsertOption option)
    {
        var value = option.Value;
        if (value is null)
        {
            return Passthrough(option);
        }

        var equals = PreviousSignificantToken(EffectiveFirstToken(value) - 1);

        if (equals <= option.FirstTokenIndex
            || _tokens[equals].TokenType != TSqlTokenType.EqualsSign
            || value.LastTokenIndex != option.LastTokenIndex)
        {
            return Passthrough(option);
        }

        return Doc.Concat(
            Keywords(option.FirstTokenIndex, equals - 1),
            Doc.Text(" = "),
            Print(value));
    }

    /// <summary><c>ORDER (a ASC, b DESC)</c>, and <c>INSERT BULK</c>'s <c>ORDER (…) UNIQUE</c>.</summary>
    /// <remarks>
    /// Unlike the rest of this family the node owns its own keyword and parentheses, so the head and
    /// tail come from its own range. The tail is sliced rather than written as <c>")"</c> because it
    /// carries the optional <c>UNIQUE</c>.
    /// </remarks>
    private Doc PrintOrderBulkInsertOption(OrderBulkInsertOption option)
    {
        if (option.Columns.Count == 0 || !SeparatedBy(option.Columns))
        {
            return Passthrough(option);
        }

        var headEnd = EffectiveFirstToken(option.Columns[0]) - 1;

        if (!Compact(SignificantTextBetween(option.FirstTokenIndex, headEnd))
                .Equals("ORDER(", StringComparison.OrdinalIgnoreCase))
        {
            return Passthrough(option);
        }

        return Doc.Group(Doc.Concat(
            Keywords(option.FirstTokenIndex, headEnd),
            Doc.Indent(Doc.Concat(
                Doc.SoftLine,
                Doc.Join(Doc.Concat(Doc.Text(","), Doc.Line), option.Columns.Select(Print)))),
            Doc.SoftLine,
            Keywords(option.Columns[^1].LastTokenIndex + 1, option.LastTokenIndex)));
    }
}
