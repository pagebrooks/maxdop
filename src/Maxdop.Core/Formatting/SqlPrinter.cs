using Maxdop.Core.Comments;
using Maxdop.Core.Printing;
using Maxdop.Core.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Formatting;

/// <summary>
/// Turns a parsed AST into doc IR. Handlers describe layout structure; every width decision
/// belongs to <see cref="DocPrinter"/>.
/// </summary>
/// <remarks>
/// <para>Currently implements the <b>structural spine</b> only: script, batches and <c>GO</c>,
/// procedure declarations, <c>BEGIN … END</c>, and statement sequencing. Everything else falls
/// through to verbatim passthrough. The spine is what makes passthrough useful at all — the
/// granularity of "leave it alone" is whichever node bails out, so with handlers at each
/// structural level an unhandled <c>MERGE</c> inside a procedure comes out untouched while the
/// statements around it format normally. Without the spine, one unhandled procedure header
/// would mean the entire file passes through.</para>
/// <para>Comment emission is centralised in <see cref="Print"/> rather than left to each
/// handler, so a handler physically cannot forget to emit them.</para>
/// </remarks>
public sealed partial class SqlPrinter
{
    /// <summary>
    /// Recursion limit for handler dispatch. Beyond it a node is passed through verbatim.
    /// </summary>
    /// <remarks>
    /// Handler dispatch is recursive over the AST, and legacy T-SQL contains genuinely deep
    /// left-leaning expression trees. A stack overflow under NativeAOT is an unrecoverable
    /// process abort, which cannot honour "return the input untouched" — so past this depth the
    /// formatter stops descending and emits source text instead. Deeply nested SQL is the one
    /// case where degrading to verbatim output is obviously the right trade.
    /// </remarks>
    private const int MaxDepth = 200;

    private readonly CommentMap _comments;
    private readonly IList<TSqlParserToken> _tokens;
    private readonly FormatOptions _options;

    /// <summary>Nodes emitted verbatim, so the dispatcher knows not to decorate them.</summary>
    private readonly HashSet<TSqlFragment> _passedThrough = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Leading comments already emitted somewhere ahead of the node they attach to, so
    /// <see cref="Print"/> must not emit them a second time.
    /// </summary>
    /// <remarks>
    /// Keyed by comment rather than by node, which is what lets a node have <em>some</em> of its
    /// leading comments hoisted. A run of leading <c>GO</c>s needs exactly that: ScriptDom gives the
    /// whole run to one batch, so every comment between them attaches to the statement that follows
    /// the last one, and printing them in source order means emitting them one at a time between the
    /// tokens they were written between.
    /// </remarks>
    private readonly HashSet<Comment> _hoistedComments = [];

    /// <summary>
    /// Comments to emit immediately before the current batch's <c>GO</c>, or null.
    /// </summary>
    /// <remarks>
    /// A field rather than a parameter because the batch is printed through the dispatcher, and
    /// calling <see cref="PrintBatch"/> directly to pass one would skip the comment wrapping every
    /// other node gets. Batches do not nest, so a single slot is enough; it is cleared on use.
    /// </remarks>
    private Doc? _batchTerminatorLead;

    /// <summary>Comments by token index, built on first use. See <see cref="CommentAt"/>.</summary>
    private Dictionary<int, Comment>? _commentByToken;

    /// <summary>
    /// Token indices at which a statement terminator has already been emitted.
    /// </summary>
    /// <remarks>
    /// Nested statements share a terminator: in <c>CREATE PROC p AS BEGIN … END;</c> both the
    /// procedure and its block end at the same <c>;</c> token. Printing is depth-first, so the
    /// innermost statement reaches this set first and the enclosing ones then skip — which also
    /// puts the semicolon in the right place, since the outer doc ends with the inner one.
    /// </remarks>
    private readonly HashSet<int> _terminatorsEmitted = [];

    private readonly ICollection<TSqlFragment>? _passthroughSink;

    /// <summary>
    /// Input token indices the printer recased as keywords despite their lexing as identifiers.
    /// </summary>
    /// <remarks>
    /// The permission slip handed to <see cref="RoundTripVerifier"/>. Per token rather than a relaxed
    /// rule, so a handler that opts a region in wrongly is still caught for every identifier it did
    /// not explicitly claim.
    /// </remarks>
    private readonly HashSet<int> _keywordCasedTokens = [];

    private int _depth;

    /// <param name="root">The parsed fragment to print; its token stream backs every slice.</param>
    /// <param name="comments">Comments already attached to their nodes by the pre-pass.</param>
    /// <param name="options">Formatting options. Print options travel separately to the DocPrinter.</param>
    /// <param name="passthroughSink">
    /// Optional collector receiving the root of every verbatim subtree.
    /// </param>
    /// <remarks>
    /// The sink exists for the corpus tool, which uses it to rank node types by how much text they
    /// leave unformatted — the handler shopping list. Only subtree <em>roots</em> arrive, since a
    /// passed-through node's descendants are never dispatched, and roots are exactly what a new
    /// handler would need to cover. Null by default, so it costs nothing in normal use.
    /// </remarks>
    public SqlPrinter(
        TSqlFragment root,
        CommentMap comments,
        FormatOptions options,
        ICollection<TSqlFragment>? passthroughSink = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(comments);
        ArgumentNullException.ThrowIfNull(options);

        _comments = comments;
        _tokens = root.ScriptTokenStream ?? [];
        _options = options;
        _passthroughSink = passthroughSink;
    }

    /// <summary>
    /// Nodes emitted verbatim because no handler modelled them, and total nodes dispatched.
    /// </summary>
    /// <remarks>
    /// The ratio is the most useful single number for deciding what to build next: it says how
    /// much of a real codebase the formatter is still declining to touch. Exposed for the corpus
    /// tool rather than for end users.
    /// </remarks>
    public int PassthroughCount { get; private set; }

    public int NodeCount { get; private set; }

    /// <summary>
    /// For each verbatim subtree root, the handler and line of the guard that declined to format it.
    /// Populated only when a passthrough sink is attached.
    /// </summary>
    /// <remarks>
    /// Lets the corpus tool rank <em>guards</em> by the text they decline, alongside ranking node
    /// types by the text they leave unformatted. The two answer different questions: a node type at
    /// the top of the histogram may be missing a handler, or may have one that keeps bailing, and
    /// only this distinguishes them.
    /// </remarks>
    public Dictionary<TSqlFragment, string> PassthroughGuards { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Identifier token texts encountered inside keyword slices, with how often. Populated only when
    /// a passthrough sink is attached.
    /// </summary>
    /// <remarks>
    /// The evidence behind the non-reserved-keyword vocabulary. Words like <c>NOCOUNT</c> and
    /// <c>APPLY</c> lex as identifiers but are keywords in these positions; index names, collations and
    /// filegroup names lex the same way and are not. Reading the real distribution is the only way to
    /// tell which is which without guessing.
    /// </remarks>
    public Dictionary<string, int> KeywordSliceIdentifiers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Input token indices this printer recased as keywords even though they lex as identifiers.
    /// </summary>
    /// <remarks>
    /// Handed to <see cref="RoundTripVerifier.Verify"/>, which compares exactly these positions
    /// case-insensitively. Read after printing.
    /// </remarks>
    public IReadOnlySet<int> KeywordCasedTokens => _keywordCasedTokens;

    /// <summary>
    /// Whether a node is emitted verbatim <em>by design</em> rather than for want of a handler.
    /// </summary>
    /// <remarks>
    /// Identifiers, literals and variable references have no layout to decide: their text is
    /// byte-identical whether a handler exists or not, and rewriting them is a correctness risk
    /// rather than a formatting improvement (see the dispatch table's note on <c>db..obj</c>). No
    /// handler will ever be written for them.
    /// <para>This matters for measurement, not for printing. A node only becomes a passthrough
    /// <em>root</em> when its parent was handled, so a value leaf always sits inside formatted
    /// output — the surrounding layout is the formatter's even though the token text is the
    /// author's. Counting those tokens as "unformatted" understated coverage by roughly fifteen
    /// points and would have grown with every handler added, exactly like the dispatched-node ratio
    /// this replaced.</para>
    /// </remarks>
    public static bool IsVerbatimByDesign(TSqlFragment node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node
            is ValueExpression            // literals and variable references
            or Identifier
            or MultiPartIdentifier        // includes SchemaObjectName
            or ColumnReferenceExpression
            or IdentifierOrValueExpression;
    }

    /// <summary>
    /// Prints a node together with its attached comments. All dispatch goes through here, which
    /// is what guarantees no handler drops a comment.
    /// </summary>
    public Doc Print(TSqlFragment node)
    {
        ArgumentNullException.ThrowIfNull(node);
        NodeCount++;

        // Leading comments already emitted ahead of this node — by JoinList in front of a separator,
        // by a keyword hoist, or by UnownedRegion between two stray tokens — are dropped here, or
        // they would appear twice.
        IReadOnlyList<Comment> leading = RemainingLeading(node);
        IReadOnlyList<Comment> trailing = Remaining(_comments.Trailing(node));

        // Dangling comments — the attacher's case 4, a construct with no children to attach to — are
        // *not* emitted here, unlike leading and trailing. They belong to the handler, because only the
        // handler knows where in its layout an empty construct's comment goes: PrintScript puts a
        // comment-only file's comments at the top with no following break, and a block would need them
        // indented inside its BEGIN/END. Adding them here as well double-emitted every one of them.
        Doc body;
        if (_depth >= MaxDepth)
        {
            body = Passthrough(node);
        }
        else
        {
            _depth++;
            try
            {
                body = PrintCore(node);
            }
            finally
            {
                _depth--;
            }
        }

        // Statement terminators are emitted here rather than by each handler, for the same reason
        // comments are: a handler that forgets one silently drops a token, and the corpus showed
        // exactly that — only SELECT re-emitted its semicolon, so `END;` before a `GO` lost it.
        // Passed-through nodes are skipped because their verbatim text already contains it.
        if (node is TSqlStatement && !_passedThrough.Contains(node))
        {
            // Every trailing semicolon, not just one: `EXECUTE sp_executesql @sql;;` really does
            // end a statement's range with two, and emitting one silently dropped the other.
            var claimed = new List<int>();
            foreach (var index in TrailingSemicolons(node))
            {
                if (_terminatorsEmitted.Add(index))
                {
                    claimed.Add(index);
                }
            }

            if (claimed.Count > 0)
            {
                // Source order: TrailingSemicolons walks backwards, and the line breaks between them
                // have to be read in the direction they were written.
                claimed.Reverse();

                // A comment can sit *between* two terminators, which is the `;WITH` idiom with a
                // section header in it:
                //
                //     … ORDER BY x DESC;
                //     /* Section 8 */
                //     ;WITH cte AS (…)
                //
                // ScriptDom folds that second semicolon into the previous statement's range, so the
                // comment lands inside it and attaches to this statement as trailing — where the
                // default emission would put it *after* both terminators, moving it past the semicolon
                // it introduced. Splitting them out and interleaving them here is what lets these
                // statements format at all; the whole statement used to be emitted verbatim because
                // there was nowhere faithful to put the comment.
                var interleaved = trailing.Where(c => c.TokenIndex < claimed[^1]).ToList();
                if (interleaved.Count > 0)
                {
                    trailing = [.. trailing.Where(c => c.TokenIndex > claimed[^1])];
                }

                // A line break between two terminators is kept for the same reason: the second one
                // belongs on the CTE's line, and running them together produced
                // `DECLARE @n INT;;WITH`.
                var terminators = new List<Doc>();
                var next = 0;

                for (var i = 0; i < claimed.Count; i++)
                {
                    var afterComment = false;
                    while (next < interleaved.Count && interleaved[next].TokenIndex < claimed[i])
                    {
                        // A blank line the author left above the comment is kept, subject to the same
                        // MaxBlankLines cap as everywhere else — a section header separated from the
                        // statement above it is exactly the vertical grouping the formatter respects.
                        if (interleaved[next].BlankLineBefore && _options.MaxBlankLines > 0)
                        {
                            terminators.Add(Doc.HardLine);
                        }

                        // Trailing supplies its own leading break for a comment that had a line of its
                        // own, so this needs no separator in front of it.
                        terminators.Add(CommentDocs.Trailing(interleaved[next]));
                        afterComment = true;
                        next++;
                    }

                    // Unconditional after a comment, not just when the source changed line: a `--`
                    // comment is emitted as plain text, so a semicolon appended to its line would end up
                    // *inside* the comment and the statement would silently lose its terminator.
                    if (afterComment || (i > 0 && _tokens[claimed[i]].Line > _tokens[claimed[i - 1]].Line))
                    {
                        terminators.Add(Doc.HardLine);
                    }
                    else if (LastCodeToken(node.FirstTokenIndex, claimed[i] - 1) is var lastCode
                        && lastCode >= 0
                        && DeferredCommentIn(lastCode + 1, claimed[i] - 1))
                    {
                        // Only when the comment was written *before* this terminator. A comment after
                        // it — `… = N'SELECT'; /*no read permissions*/` — is deferred and pending here
                        // too, and flushing that one would carry it to the semicolon's far side. The
                        // gap test is what tells them apart, and the same rule every hoist follows:
                        // keep each comment on the side of the separator it was written on.
                        //
                        // A comment *inside* the statement that ended its line is deferred as a line
                        // suffix, so a terminator appended to that line is emitted ahead of it:
                        //
                        //     … = 0 --bitmask: 1 = email      … = 0; --bitmask: 1 = email
                        //     ;                          ->
                        //
                        // which reads as a note about the terminator rather than about the predicate,
                        // and is the same ordering the concatenation chain and the join's ON already
                        // correct. The semicolon cannot simply move to the comment's left either — a
                        // `--` comment runs to end of line, so appending one there would swallow it.
                        // The boundary flushes the pending comment and takes the terminator to the next
                        // line, which is where the author had written it. It is a no-op when nothing is
                        // pending, which is every other statement in the corpus.
                        terminators.Add(Doc.LineSuffixBoundary);
                    }

                    terminators.Add(Doc.Text(";"));
                }

                body = Doc.Concat(body, Doc.Concat(terminators));
            }
        }

        // A passed-through node emits its own source verbatim, and that text starts at
        // EffectiveFirstToken — which reaches *backwards* over the keywords the node's range
        // excludes. A comment written among them is a leading comment of the node and also inside
        // the verbatim text, so emitting it here too printed it twice:
        //
        //     SELECT a FROM t FOR /* c */ JSON PATH
        //       ->  SELECT a FROM t /* c */ FOR /* c */ JSON PATH
        //
        // which the survival gate caught and refused. The verbatim text is authoritative for its own
        // range; anything inside it has already been printed.
        if (_passedThrough.Contains(node))
        {
            var verbatimFrom = EffectiveFirstToken(node);
            leading = [.. leading.Where(comment => comment.TokenIndex < verbatimFrom)];
            trailing = [.. trailing.Where(comment => comment.TokenIndex > node.LastTokenIndex)];
        }

        if (leading.Count == 0 && trailing.Count == 0)
        {
            return body;
        }

        return Doc.Concat(
            CommentDocs.AllLeading(leading),
            body,
            CommentDocs.AllTrailing(trailing));
    }

    private Doc PrintCore(TSqlFragment node) => node switch
    {
        TSqlScript script => PrintScript(script),
        TSqlBatch batch => PrintBatch(batch),

        // StatementList carries an unset [-1..-1] token range, so it cannot be passed through
        // verbatim — it must be handled or its statements would vanish.
        StatementList list => PrintStatements(list.Statements),

        BeginEndBlockStatement block => PrintBeginEnd(block),
        ProcedureStatementBody procedure => PrintProcedure(procedure),

        // --- the other module definitions (SqlPrinter.Modules.cs) ---
        // Dispatched on ScriptDom's abstract bases, so CREATE, ALTER and CREATE OR ALTER are all
        // covered by one entry each.
        ViewStatementBody view => PrintView(view),
        FunctionStatementBody function => PrintFunction(function),
        TriggerStatementBody trigger => PrintTrigger(trigger),
        ScalarFunctionReturnType scalarReturn => PrintScalarFunctionReturnType(scalarReturn),
        TableValuedFunctionReturnType tableReturn => PrintTableValuedFunctionReturnType(tableReturn),
        SelectFunctionReturnType selectReturn => PrintSelectFunctionReturnType(selectReturn),
        TriggerAction triggerAction => PrintTriggerAction(triggerAction),
        TriggerObject triggerObject => PrintTriggerObject(triggerObject),

        // --- control flow (SqlPrinter.ControlFlow.cs) ---
        IfStatement ifStatement => PrintIf(ifStatement),
        WhileStatement whileStatement => PrintWhile(whileStatement),
        TryCatchStatement tryCatch => PrintTryCatch(tryCatch),

        // --- function-shaped expressions and call targets (SqlPrinter.Calls.cs) ---
        // Each of these is its own ScriptDom type rather than a FunctionCall, so each needs an
        // entry even though they share one implementation.
        NullIfExpression nullIf => PrintNullIf(nullIf),
        CoalesceExpression coalesce => PrintCoalesce(coalesce),
        LeftFunctionCall left => PrintLeftFunction(left),
        RightFunctionCall right => PrintRightFunction(right),
        IIfCall iif => PrintIif(iif),
        ExpressionCallTarget expressionTarget => PrintCallTarget(expressionTarget, expressionTarget.Expression),
        MultiPartIdentifierCallTarget identifierTarget =>
            PrintCallTarget(identifierTarget, identifierTarget.MultiPartIdentifier),
        OverClause over => PrintOverClause(over),
        SchemaObjectFunctionTableReference functionTable => PrintSchemaObjectFunctionTable(functionTable),
        PivotedTableReference pivot => PrintPivotedTable(pivot),
        UnpivotedTableReference unpivot => PrintUnpivotedTable(unpivot),
        GlobalFunctionTableReference globalFunctionTable => PrintGlobalFunctionTable(globalFunctionTable),
        BuiltInFunctionTableReference builtInFunctionTable => PrintBuiltInFunctionTable(builtInFunctionTable),
        VariableMethodCallTableReference methodCallTable => PrintVariableMethodCallTable(methodCallTable),
        OpenJsonTableReference openJsonTable => PrintOpenJsonTable(openJsonTable),
        SchemaDeclarationItem declarationItem => PrintSchemaDeclarationItem(declarationItem),
        ParameterlessCall parameterlessCall => PrintParameterlessCall(parameterlessCall),
        ParseCall parseCall => PrintParseCall(parseCall),
        SubqueryComparisonPredicate subqueryComparison => PrintSubqueryComparison(subqueryComparison),
        UpdateCall updateCall => PrintUpdateCall(updateCall),
        PartitionFunctionCall partitionCall => PrintPartitionFunctionCall(partitionCall),

        // Transparent wrappers: a node whose range coincides with its single child's, present only
        // because the grammar needed somewhere to hang an alternative. PrintCallTarget verifies the
        // ranges really do coincide before forwarding.
        ProcedureReferenceName procedureName =>
            PrintCallTarget(procedureName, (TSqlFragment?)procedureName.ProcedureReference ?? procedureName.ProcedureVariable),
        ProcedureReference procedureReference when procedureReference.Number is null =>
            PrintCallTarget(procedureReference, procedureReference.Name),
        ExpressionGroupingSpecification grouping => PrintCallTarget(grouping, grouping.Expression),

        // --- conversions and data types (SqlPrinter.Types.cs) ---
        // Four sibling types rather than a hierarchy, so four entries for two implementations.
        CastCall cast => PrintCastLike(cast, cast.Parameter, cast.DataType),
        TryCastCall tryCast => PrintCastLike(tryCast, tryCast.Parameter, tryCast.DataType),
        ConvertCall convert => PrintConvertLike(convert, convert.DataType, convert.Parameter, convert.Style),
        TryConvertCall tryConvert =>
            PrintConvertLike(tryConvert, tryConvert.DataType, tryConvert.Parameter, tryConvert.Style),
        DataTypeReference dataType => PrintDataType(dataType),

        // --- the small statements (SqlPrinter.Simple.cs) ---
        // Dispatched on ScriptDom's abstract bases wherever one exists, so a spelling nobody thought
        // of is covered rather than missed: SetOnOffStatement alone stands in for a dozen SET forms.
        // Before SetOnOffStatement, which it derives from: it is the one SET form carrying an object
        // name, and the base handler would recase it.
        SetIdentityInsertStatement identityInsert => PrintSetIdentityInsert(identityInsert),
        SetOnOffStatement setOnOff => PrintKeywordStatement(setOnOff),
        AlterTableAddTableElementStatement alterAdd => PrintAlterTableAddTableElement(alterAdd),
        DeclareCursorStatement declareCursor => PrintDeclareCursor(declareCursor),
        CursorDefinition cursorDefinition => PrintCursorDefinition(cursorDefinition),

        // Before CursorStatement, which it derives from: the base handler knows only about the cursor
        // name and would leave the INTO variables as an unformatted tail slice.
        FetchCursorStatement fetch => PrintFetchCursor(fetch),
        XmlForClause xmlFor => PrintXmlForClause(xmlFor),
        XmlForClauseOption xmlOption => PrintXmlForClauseOption(xmlOption),
        XmlNamespacesElement xmlNamespaceElement => PrintXmlNamespacesElement(xmlNamespaceElement),
        CreateIndexStatement createIndex => PrintCreateIndex(createIndex),
        InlineDerivedTable inlineTable => PrintInlineDerivedTable(inlineTable),
        XmlNamespaces xmlNamespaces => PrintXmlNamespaces(xmlNamespaces),
        UniqueConstraintDefinition unique => PrintUniqueConstraint(unique),
        DefaultConstraintDefinition defaultConstraint => PrintDefaultConstraint(defaultConstraint),
        IdentityOptions identity => PrintIdentityOptions(identity),
        IndexDefinition indexDefinition => PrintIndexDefinition(indexDefinition),
        SetTransactionIsolationLevelStatement isolation => PrintKeywordStatement(isolation),
        BreakStatement breakStatement => PrintKeywordStatement(breakStatement),
        ContinueStatement continueStatement => PrintKeywordStatement(continueStatement),
        // Not PrintKeywordStatement: a label lives in a plain string property with no AST node, so
        // recasing the statement would rename it and leave every GOTO pointing at the old spelling.
        LabelStatement label => PrintNameBearingStatement(label),
        TransactionStatement transaction => PrintTransaction(transaction),
        CursorStatement cursor => PrintCursorStatement(cursor),
        DropObjectsStatement drop => PrintDropObjects(drop),
        TruncateTableStatement truncate => PrintTruncateTable(truncate),
        UpdateStatisticsStatement updateStatistics => PrintUpdateStatistics(updateStatistics),
        ReturnStatement returnStatement => PrintReturn(returnStatement),
        ThrowStatement throwStatement => PrintThrow(throwStatement),
        WaitForStatement waitFor => PrintWaitFor(waitFor),
        EndConversationStatement endConversation => PrintEndConversation(endConversation),
        GoToStatement goTo => PrintGoTo(goTo),
        ColumnWithSortOrder sortedColumn => PrintColumnWithSortOrder(sortedColumn),
        CursorId cursorId => PrintCursorId(cursorId),
        UnaryExpression unary => PrintUnaryExpression(unary),
        BooleanTernaryExpression ternary => PrintBooleanTernary(ternary),

        // --- EXECUTE (SqlPrinter.Execute.cs) ---
        ExecuteStatement execute => PrintExecute(execute),
        ExecuteSpecification executeSpecification => PrintExecuteSpecification(executeSpecification),
        ExecuteInsertSource executeSource => PrintCallTarget(executeSource, executeSource.Execute),
        ExecutableProcedureReference procedureCall => PrintExecutableProcedureReference(procedureCall),
        ExecutableStringList strings => PrintExecutableStringList(strings),
        ExecuteParameter executeParameter => PrintExecuteParameter(executeParameter),

        // --- procedural statements (SqlPrinter.Statements.cs) ---
        RaiseErrorStatement raiseError => PrintRaiseError(raiseError),
        PrintStatement print => PrintPrint(print),

        // --- declarations and table structure (SqlPrinter.Ddl.cs) ---
        CreateTableStatement createTable => PrintCreateTable(createTable),
        CreateTypeTableStatement createTypeTable => PrintCreateTypeTable(createTypeTable),
        TableDefinition tableDefinition => PrintTableDefinition(tableDefinition),
        SystemTimePeriodDefinition period => PrintSystemTimePeriod(period),
        ColumnDefinition column => PrintColumnDefinition(column),

        // After ColumnDefinition, which derives from it. The cut-down form is what an
        // `OPENJSON … WITH` declaration uses: a name, a type, and nothing else.
        ColumnDefinitionBase columnBase => PrintPartsInTokenOrder(
            columnBase, columnBase.ColumnIdentifier, columnBase.DataType, columnBase.Collation),
        NullableConstraintDefinition nullable => PrintNullableConstraint(nullable),
        DeclareVariableStatement declare => PrintDeclareVariable(declare),
        DeclareTableVariableStatement declareTable => PrintDeclareTableVariable(declareTable),
        DeclareTableVariableBody declareTableBody => PrintDeclareTableVariableBody(declareTableBody),

        // ProcedureParameter derives from DeclareVariableElement, so this one entry covers both a
        // variable declaration and a procedure's parameter list.
        DeclareVariableElement declaration => PrintDeclareVariableElement(declaration),

        // --- INSERT / UPDATE / DELETE, OUTPUT and assignment (SqlPrinter.Dml.cs) ---
        InsertStatement insert => PrintInsert(insert),
        UpdateStatement update => PrintUpdate(update),
        DeleteStatement delete => PrintDelete(delete),
        MergeStatement merge => PrintMerge(merge),

        // The two option subtypes come before their base: a switch expression takes the first arm that
        // matches, and BulkInsertOption is the base of both.
        LiteralBulkInsertOption literalOption => PrintLiteralBulkInsertOption(literalOption),
        OrderBulkInsertOption orderOption => PrintOrderBulkInsertOption(orderOption),
        BulkInsertOption bulkOption => PrintBulkInsertOption(bulkOption),
        BulkInsertStatement bulkInsert => PrintBulkInsert(bulkInsert),

        MergeActionClause mergeClause => PrintMergeActionClause(mergeClause),
        UpdateMergeAction updateAction => PrintUpdateMergeAction(updateAction),
        InsertMergeAction insertAction => PrintInsertMergeAction(insertAction),
        DeleteMergeAction deleteAction => PrintDeleteMergeAction(deleteAction),
        AssignmentSetClause setClause => PrintAssignmentSetClause(setClause),
        OutputClause output => PrintOutputClause(output),
        OutputIntoClause outputInto => PrintOutputIntoClause(outputInto),
        ValuesInsertSource values => PrintValuesInsertSource(values),
        SelectInsertSource selectSource => PrintSelectInsertSource(selectSource),
        RowValue row => PrintRowValue(row),
        SetVariableStatement setVariable => PrintSetVariable(setVariable),
        SelectSetVariable selectSetVariable => PrintSelectSetVariable(selectSetVariable),

        // --- SELECT and everything it contains (SqlPrinter.Select.cs) ---
        SelectStatement select => PrintSelectStatement(select),
        WithCtesAndXmlNamespaces ctes => PrintCtes(ctes),
        CommonTableExpression cte => PrintCte(cte),
        QuerySpecification query => PrintQuerySpecification(query),
        BinaryQueryExpression binary => PrintBinaryQuery(binary),
        QueryParenthesisExpression parenthesis => PrintQueryParenthesis(parenthesis),

        SelectScalarExpression scalar => PrintSelectScalar(scalar),
        SelectStarExpression star => PrintSelectStar(star),

        FromClause from => PrintFromClause(from),
        WhereClause where => PrintWhereClause(where),
        GroupByClause groupBy => PrintGroupByClause(groupBy),
        HavingClause having => PrintHavingClause(having),
        OrderByClause orderBy => PrintOrderByClause(orderBy),
        ExpressionWithSortOrder sort => PrintSortExpression(sort),

        NamedTableReference table => PrintNamedTable(table),
        VariableTableReference variableTable => PrintVariableTableReference(variableTable),
        QueryDerivedTable derived => PrintQueryDerivedTable(derived),
        JoinTableReference join => PrintJoin(join),

        BooleanBinaryExpression booleanBinary => PrintBooleanChain(booleanBinary),
        BooleanComparisonExpression comparison => PrintBooleanComparison(comparison),
        BooleanParenthesisExpression booleanParenthesis => PrintBooleanParenthesis(booleanParenthesis),
        BooleanNotExpression not => PrintBooleanNot(not),
        BooleanIsNullExpression isNull => PrintIsNull(isNull),
        ExistsPredicate exists => PrintExists(exists),
        InPredicate inPredicate => PrintIn(inPredicate),
        LikePredicate like => PrintLikePredicate(like),

        BinaryExpression arithmetic => PrintBinaryExpression(arithmetic),
        ParenthesisExpression grouped => PrintParenthesis(grouped),
        FunctionCall call => PrintFunctionCall(call),
        ScalarSubquery subquery => PrintScalarSubquery(subquery),
        CaseExpression caseExpression => PrintCase(caseExpression),
        WhenClause whenClause => PrintWhenClause(whenClause),
        GlobalVariableExpression global => PrintGlobalVariable(global),
        WithinGroupClause withinGroup => PrintWithinGroup(withinGroup),

        // Identifiers, literals and variable references are deliberately absent: passthrough
        // reproduces their text exactly, including bracket quoting, `N'…'` prefixes and numeric
        // formatting. Rewriting them is not layout, and `db..obj` shows why trying is risky —
        // reassembling a dotted name from its parts can silently change which object it means.
        // The generic fallback, for the administrative DDL surface: several hundred statement types
        // shaped alike, discovered at runtime rather than written down one by one. Statements and the
        // option/definition nodes under them only — never expressions, whose layout carries meaning.
        // See SqlPrinter.Generic.cs.
        DbccStatement dbcc => PrintDbcc(dbcc),
        TSqlStatement or TSqlFragment when IsGenericCandidate(node) => PrintGeneric(node),

        _ => Passthrough(node),
    };

    /// <summary>
    /// Whether a node is eligible for the generic fallback in <see cref="PrintGeneric"/>.
    /// </summary>
    /// <remarks>
    /// Written as an exclusion list, because the eligible set is the long tail and the ineligible set is
    /// small and nameable. Excluded, each for its own reason:
    /// <list type="bullet">
    /// <item><b>Expressions</b> (<c>ScalarExpression</c>, <c>BooleanExpression</c>): line breaking carries
    /// meaning — where an operator may break, what binds to what — and a generic emitter cannot reason
    /// about precedence. These already have hand-written handlers.</item>
    /// <item><b>Names</b> (<c>Identifier</c>, <c>MultiPartIdentifier</c>, <c>DataTypeReference</c>):
    /// passthrough reproduces them exactly, including bracket quoting and <c>N'…'</c> prefixes.
    /// Reassembling <c>db..obj</c> from its parts can silently change which object it means.</item>
    /// <item><b>Table references and query expressions</b>: joins and set operators have a designed
    /// layout, and the handlers for them exist.</item>
    /// </list>
    /// Everything else — statements, and the option, permission, event and definition nodes that hang off
    /// them — is a keyword run with names in it, which is exactly what the fallback handles. Restricting
    /// it to statements alone left <c>EventDeclaration</c>, <c>Permission</c>, <c>IndexStateOption</c> and
    /// <c>AuditSpecificationPart</c> verbatim inside otherwise-formatted statements, so a
    /// <c>DROP INDEX ix   ON   dbo.t</c> kept its original spacing in the middle of the line.
    /// </remarks>
    private static bool IsGenericCandidate(TSqlFragment node) => node
        is not ScalarExpression
        and not BooleanExpression
        and not Identifier
        and not MultiPartIdentifier
        and not DataTypeReference
        and not TableReference
        and not QueryExpression
        and not StatementList;

    // --- script and batches ---------------------------------------------------------

    private Doc PrintScript(TSqlScript script)
    {
        var dangling = _comments.Dangling(script);

        // A comment-only file parses to zero batches, with the comments dangling here. Emitting
        // them is the difference between passing the file through and formatting it to nothing.
        if (script.Batches.Count == 0)
        {
            // …but "no batches" does not mean "nothing but comments". A file that is a comment and
            // a `GO` parses to zero batches too, and the GO belongs to no node — so returning the
            // comments alone dropped it, and the verifier refused the whole file. Two lines of
            // input, and the only shape in which maxdop declined a file it had fully understood.
            if (SignificantTextBetween(0, _tokens.Count - 1).Length == 0)
            {
                return CommentDocs.Dangling(dangling);
            }

            var unowned = UnownedRegion(0, _tokens.Count - 1);
            return dangling.Count > 0
                ? Doc.Concat(CommentDocs.Dangling(dangling), Doc.HardLine, unowned)
                : unowned;
        }

        var parts = new List<Doc>();
        if (dangling.Count > 0)
        {
            parts.Add(CommentDocs.Dangling(dangling));
            parts.Add(Doc.HardLine);
        }

        // Walked with a token cursor rather than handed to AppendSequence, because not every token
        // in a script belongs to a batch. `;;CREATE TABLE …` puts stray semicolons before the first
        // batch's range, and a trailing `GO` after the last one can be similarly unowned — both were
        // dropped outright. The cursor makes every significant token somebody's responsibility.
        var cursor = 0;

        for (var i = 0; i < script.Batches.Count; i++)
        {
            var batch = script.Batches[i];

            // Last stray token emitted in front of this batch, or -1 when there were none.
            var regionEnd = -1;

            if (SignificantTextBetween(cursor, batch.FirstTokenIndex - 1).Length > 0)
            {
                if (parts.Count > 0)
                {
                    parts.Add(Doc.HardLine);
                }

                // Tokens here belong to no batch — the `;;` of `;;CREATE TABLE`, or a run of leading
                // GOs — while the comments among them belong to what comes after. Handing the region
                // its owner lets it put each one back where it was written, which an all-or-nothing
                // hoist in front of the region cannot do once a comment sits *between* two of these
                // tokens.
                regionEnd = LastCodeToken(cursor, batch.FirstTokenIndex - 1);
                parts.Add(UnownedRegion(cursor, batch.FirstTokenIndex - 1, CommentOwnerOf(batch)));
            }

            if (parts.Count > 0)
            {
                parts.Add(Doc.HardLine);

                if (i > 0)
                {
                    // Measured from the stray tokens when there are any, because those are what the
                    // batch now follows — the blank lines below are emitted between the two.
                    // Measuring from the previous batch counted the lines the region itself occupies:
                    // maxdop puts the `;` of a `;WITH` on its own line, and on the next pass that line
                    // read as a gap worth preserving. A blank line appeared, then another, and
                    // formatting was not a fixed point. Five files in the corpus did this.
                    var blanks = Math.Min(
                        regionEnd >= 0
                            ? Math.Max(0, batch.StartLine - EndLineOfToken(regionEnd) - 1)
                            : BlankLinesBetween(script.Batches[i - 1], batch),
                        _options.MaxBlankLines);
                    for (var b = 0; b < blanks; b++)
                    {
                        parts.Add(Doc.HardLine);
                    }
                }
            }

            var terminator = FindBatchTerminator(batch);

            // A comment above a GO introduces what follows it, but it belongs to the *next* batch's
            // first statement — so the GO was printed above the file's header comment. The comment
            // is written before the GO and stays there; only this loop can see both batches at once.
            _batchTerminatorLead = terminator >= 0 && i + 1 < script.Batches.Count
                ? HoistBatchLead(script.Batches[i + 1], terminator, terminator)
                : null;

            parts.Add(Print(batch));
            _batchTerminatorLead = null;

            cursor = (terminator >= 0 ? terminator : batch.LastTokenIndex) + 1;
        }

        // Anything after the final batch that no batch claimed.
        if (SignificantTextBetween(cursor, _tokens.Count - 1).Length > 0)
        {
            parts.Add(Doc.HardLine);
            parts.Add(UnownedRegion(cursor, _tokens.Count - 1));
        }

        return Doc.Concat(parts);
    }

    private Doc PrintBatch(TSqlBatch batch)
    {
        var parts = new List<Doc>();

        // Same sequencer as a block body, so statement separation behaves identically whether a
        // statement sits at batch level or inside BEGIN…END.
        if (batch.Statements.Count > 0)
        {
            parts.Add(PrintStatements(batch.Statements));
        }

        // GO is a client-side batch separator, not a statement, so it belongs to no AST node.
        // The batch that it terminates is the only sensible owner.
        if (FindBatchTerminator(batch) >= 0)
        {
            if (parts.Count > 0)
            {
                parts.Add(Doc.HardLine);
            }

            var lead = _batchTerminatorLead;
            _batchTerminatorLead = null;

            if (lead is not null)
            {
                parts.Add(lead);
            }

            parts.Add(Keyword("GO"));
        }

        return Doc.Concat(parts);
    }

    /// <summary>
    /// Token index of the <c>GO</c> terminating this batch, or -1. Shares its implementation
    /// with the comment pre-pass so both agree on where a batch ends.
    /// </summary>
    private int FindBatchTerminator(TSqlBatch batch) =>
        SqlTokens.FindBatchTerminator(_tokens, batch.LastTokenIndex);

    // --- blocks and statement sequencing --------------------------------------------

    private Doc PrintBeginEnd(BeginEndBlockStatement block)
    {
        var statements = block.StatementList?.Statements;
        if (statements is null || statements.Count == 0)
        {
            return Passthrough(block);
        }

        // `BEGIN` is not always a bare keyword. A natively compiled body is
        // `BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, …)`, and a stray semicolon
        // after `BEGIN` is common in generated scripts. Both were previously grounds for
        // passthrough, and `BEGIN;` alone accounted for the single largest remaining block of
        // unformatted text in the corpus — whole procedure bodies frozen over one semicolon.
        // Emitting the opening region instead handles every spelling.
        var end = RangeEndBeforeTerminators(block);
        if (!SignificantTextBetween(statements[^1].LastTokenIndex + 1, end)
                .Equals("END", StringComparison.OrdinalIgnoreCase))
        {
            return Passthrough(block);
        }

        // EffectiveFirstToken for the same reason PrintTryCatch uses it: a statement whose range
        // excludes its own keywords would have them absorbed into this head slice.
        var openEnd = EffectiveFirstToken(statements[0]) - 1;

        // …and the same care is owed to the defensive semicolon of the `;WITH` idiom. It reads as
        // part of the statement it guards — that is the entire reason it is written — but it is not
        // inside that statement's range, so the head slice swallowed it and printed
        //
        //     BEGIN ;                        BEGIN
        //         WITH max_batch AS (   ->       ;WITH max_batch AS (
        //
        // orphaning the WITH. Distinguished from `BEGIN;` — an empty statement, and a different
        // thing — by the line the author put it on: a semicolon sharing the BEGIN's line stays with
        // the BEGIN.
        var semicolon = LastCodeToken(block.FirstTokenIndex, openEnd);
        var guarded = semicolon > block.FirstTokenIndex
            && _tokens[semicolon].TokenType == TSqlTokenType.Semicolon
            && _tokens[semicolon].Line > _tokens[block.FirstTokenIndex].Line;

        // Comments above the `;WITH` stay above it, rather than landing between the semicolon and
        // the WITH it guards. Same treatment as any other introducing token.
        var guard = guarded
            ? Doc.Concat(
                HoistLeadingBefore(statements[0], semicolon, semicolon) ?? Doc.Empty,
                Doc.Text(";"))
            : Doc.Empty;

        return Doc.Concat(
            CasedTokens(block.FirstTokenIndex, guarded ? semicolon - 1 : openEnd),
            Doc.Indent(Doc.Concat(Doc.HardLine, guard, Print(block.StatementList!))),
            Doc.HardLine,
            Keyword("END"));
    }

    private Doc PrintStatements(IList<TSqlStatement> statements)
    {
        var parts = new List<Doc>();

        for (var i = 0; i < statements.Count; i++)
        {
            if (i > 0 && !JoinsToPreviousStatement(statements[i - 1], statements[i]))
            {
                parts.Add(Doc.HardLine);

                var blanks = Math.Min(
                    BlankLinesBetween(statements[i - 1], statements[i]),
                    _options.MaxBlankLines);

                for (var b = 0; b < blanks; b++)
                {
                    parts.Add(Doc.HardLine);
                }
            }

            parts.Add(Print(statements[i]));

            // Tokens between this statement and the next that no statement owns. `commit Work`
            // parses as a statement covering just `commit`, leaving `Work` homeless.
            if (i + 1 < statements.Count)
            {
                var gapFrom = statements[i].LastTokenIndex + 1;

                // EffectiveFirstToken, so keywords the next statement owns but its range excludes
                // are not mistaken for unowned text and emitted twice.
                var gapTo = EffectiveFirstToken(statements[i + 1]) - 1;
                if (SignificantTextBetween(gapFrom, gapTo).Length > 0)
                {
                    parts.Add(Doc.Text(" "));
                    parts.Add(UnownedRegion(gapFrom, gapTo));
                }
            }
        }

        return Doc.Concat(parts);
    }

    /// <summary>
    /// Whether a statement should be emitted immediately after its predecessor with no separator.
    /// </summary>
    /// <remarks>
    /// Written for the defensive <c>;WITH</c> idiom, which is everywhere in legacy T-SQL: a
    /// <c>WITH</c> clause must begin a batch or follow a semicolon, so authors put a bare
    /// <c>;</c> in front of it.
    /// <para>ScriptDom does <em>not</em> model that semicolon as an empty statement — it extends
    /// the <em>preceding</em> statement's token range to cover it. So the tell is a predecessor
    /// whose text ends in two semicolons, and the redundant one arrives already sitting on its
    /// own line. Suppressing the separator here is what turns that into <c>;WITH</c>.</para>
    /// <para>Narrow on purpose: only a doubled semicolon, and only when a CTE-bearing statement
    /// follows. This becomes unnecessary once DECLARE and SET have handlers, since a handler emits
    /// exactly one terminator and drops the redundant one.</para>
    /// </remarks>
    private bool JoinsToPreviousStatement(TSqlStatement previous, TSqlStatement current)
    {
        if (current is not StatementWithCtesAndXmlNamespaces { WithCtesAndXmlNamespaces: not null })
        {
            return false;
        }

        var text = SignificantTextBetween(previous.FirstTokenIndex, previous.LastTokenIndex);
        return text.EndsWith(';') && text[..^1].TrimEnd().EndsWith(';');
    }

    /// <summary>
    /// Emits items separated by hard lines, preserving up to
    /// <see cref="FormatOptions.MaxBlankLines"/> of the author's own vertical grouping.
    /// </summary>
    private void AppendSequence<T>(List<Doc> parts, IList<T> items, Func<T, Doc> print)
        where T : TSqlFragment
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                parts.Add(Doc.HardLine);

                var blanks = Math.Min(BlankLinesBetween(items[i - 1], items[i]), _options.MaxBlankLines);
                for (var b = 0; b < blanks; b++)
                {
                    parts.Add(Doc.HardLine);
                }
            }

            parts.Add(print(items[i]));
        }
    }

    /// <summary>
    /// Blank lines the author left between two constructs. Measured against attached comments
    /// rather than the bare nodes: a comment sitting above a statement is printed as part of
    /// that statement, so the gap that matters is the one above the <em>comment</em>.
    /// </summary>
    private int BlankLinesBetween(TSqlFragment previous, TSqlFragment next)
    {
        var leading = _comments.Leading(next);
        if (leading.Count > 0)
        {
            return leading[0].BlankLineBefore ? 1 : 0;
        }

        var trailing = _comments.Trailing(previous);
        var previousEnd = trailing.Count > 0
            ? trailing[^1].Line + CountNewLines(trailing[^1].Text)
            : EndLineOf(previous);

        return Math.Max(0, next.StartLine - previousEnd - 1);
    }

    /// <summary>Line a token ends on, counting the newlines inside it.</summary>
    private int EndLineOfToken(int index) =>
        _tokens[index].Line + CountNewLines(_tokens[index].Text);

    private int EndLineOf(TSqlFragment fragment)
    {
        var index = fragment.LastTokenIndex;

        // A batch's range stops before its GO, but the GO occupies a line and is printed as
        // part of the batch. Measuring from the statement would report a phantom blank line
        // between every pair of batches.
        if (fragment is TSqlBatch batch)
        {
            var terminator = FindBatchTerminator(batch);
            if (terminator >= 0)
            {
                index = terminator;
            }
        }

        if (index < 0 || index >= _tokens.Count)
        {
            return fragment.StartLine;
        }

        return _tokens[index].Line + CountNewLines(_tokens[index].Text);
    }

    private static int CountNewLines(string? text) => SqlTokens.CountNewLines(text);

    // --- procedures -----------------------------------------------------------------

    private Doc PrintProcedure(ProcedureStatementBody procedure)
    {
        // A CLR procedure (`AS EXTERNAL NAME asm.Class.Method`) has no T-SQL body at all, so
        // the layout below does not apply. Pass the whole statement through.
        if (procedure.MethodSpecifier is not null)
        {
            return Passthrough(procedure);
        }

        var recognised = procedure is CreateProcedureStatement
            or AlterProcedureStatement
            or CreateOrAlterProcedureStatement;

        var body = procedure.StatementList;
        if (!recognised
            || procedure.ProcedureReference is null
            || body is null
            || body.Statements.Count == 0
            || !SeparatedBy(procedure.Parameters))
        {
            // An unrecognised subclass from a future ScriptDom. Passthrough beats guessing.
            return Passthrough(procedure);
        }

        // The `AS` introducing the body anchors everything: whatever sits between the parameter
        // list and it is qualifiers, and is emitted as one slice.
        var asIndex = PrecedingKeyword(TSqlTokenType.As, body.Statements[0].FirstTokenIndex);
        if (asIndex < 0)
        {
            return Passthrough(procedure);
        }

        // Read the introducing keywords rather than reconstructing them, so `CREATE PROC` is not
        // silently expanded to `CREATE PROCEDURE`. `Proc` and `Procedure` are distinct token
        // types, so expanding one is a token change the verifier rightly rejects — and it is a
        // rewrite nobody asked for.
        var keyword = CasedTokens(procedure.FirstTokenIndex, procedure.ProcedureReference.FirstTokenIndex - 1);

        var parts = new List<Doc>
        {
            // The header groups so that a short signature stays on one line and a long
            // parameter list breaks one-per-line, without either being hard-coded.
            Doc.Group(Doc.Concat(
                keyword,
                Doc.Text(" "),
                Print(procedure.ProcedureReference),
                PrintParameters(procedure))),
        };

        // Everything between the parameter list and the body's AS in one slice: `WITH RECOMPILE`,
        // `WITH EXECUTE AS CALLER`, `FOR REPLICATION`, and any combination of them.
        //
        // Reassembling this from the Options nodes was wrong, and wrong in the worst way:
        // `ExecuteAsProcedureOption`'s token range does not cover its principal, so
        // `WITH EXECUTE AS CALLER` was emitted as `WITH EXECUTE AS` and the output stopped
        // parsing. Anchoring on tokens instead of on node ranges removes a whole class of that
        // mistake — there is no option kind this can fail to reproduce.
        var qualifiersFrom = ParameterListEnd(procedure) + 1;
        if (SignificantTextBetween(qualifiersFrom, asIndex - 1).Length > 0)
        {
            parts.Add(Doc.HardLine);
            parts.Add(Keywords(qualifiersFrom, asIndex - 1));
        }

        // An own-line comment written above the AS annotates the *header* — a commented-out
        // `WITH EXECUTE AS OWNER` is the corpus case — so it is emitted above the AS. Left where it
        // attached, it printed below the AS, where it reads as a note about the body's first
        // statement instead. Same defect as the predicate operators; see HoistLeadingBefore.
        //
        // The range is the AS token alone, so a comment *after* the AS is left alone. When the
        // procedure also has qualifiers, a comment written above them is emitted below them
        // instead — still on the correct side of the AS, and rare enough not to model separately.
        var hoistedAs = HoistLeadingBefore(body, asIndex, asIndex);

        parts.Add(Doc.HardLine);

        if (hoistedAs is not null)
        {
            parts.Add(hoistedAs);
        }

        parts.Add(Keyword("AS"));
        parts.Add(Doc.HardLine);

        // Through Print, not PrintStatements: a commented-out qualifier between the parameter list
        // and AS attaches to this StatementList, and bypassing the dispatcher dropped it.
        parts.Add(Print(body));

        return Doc.Concat(parts);
    }

    /// <summary>
    /// Last token of the parameter list, including its closing parenthesis when present, or the
    /// procedure name when there are no parameters.
    /// </summary>
    private int ParameterListEnd(ProcedureStatementBody procedure)
    {
        if (procedure.Parameters.Count == 0)
        {
            return procedure.ProcedureReference!.LastTokenIndex;
        }

        var last = procedure.Parameters[^1].LastTokenIndex;
        var next = last + 1;
        while (next < _tokens.Count && _tokens[next].IsTrivia())
        {
            next++;
        }

        return next < _tokens.Count && _tokens[next].TokenType == TSqlTokenType.RightParenthesis
            ? next
            : last;
    }

    /// <summary>
    /// Index of the given keyword immediately before <paramref name="beforeIndex"/>, skipping
    /// trivia, or -1 if the preceding token is something else.
    /// </summary>
    private int PrecedingKeyword(TSqlTokenType keyword, int beforeIndex)
    {
        var i = Math.Min(beforeIndex, _tokens.Count) - 1;
        while (i >= 0 && _tokens[i].IsTrivia())
        {
            i--;
        }

        return i >= 0 && _tokens[i].TokenType == keyword ? i : -1;
    }

    private Doc PrintParameters(ProcedureStatementBody procedure)
    {
        var parameters = procedure.Parameters;
        if (parameters.Count == 0)
        {
            return Doc.Empty;
        }

        var list = JoinList(parameters);

        // Parameter lists may be parenthesised — `CREATE PROCEDURE p (@a INT)` — and dropping the
        // parentheses was losing tokens. Detected from the source rather than assumed, since both
        // spellings are legal and the author's choice should survive.
        var parenthesised = SignificantTextBetween(
            procedure.ProcedureReference!.LastTokenIndex + 1,
            parameters[0].FirstTokenIndex - 1) == "(";

        if (parenthesised)
        {
            return Doc.Concat(
                Doc.Text(" ("),
                Doc.Indent(Doc.Concat(Doc.SoftLine, list)),
                Doc.SoftLine,
                Doc.Text(")"));
        }

        return Doc.Indent(Doc.Concat(Doc.Line, list));
    }

    // --- passthrough ----------------------------------------------------------------

    /// <summary>
    /// Emits a construct exactly as the author wrote it. This is safety invariant #2
    /// and the reason the formatter can ship before covering all of T-SQL.
    /// </summary>
    /// <param name="node">The construct to emit verbatim.</param>
    /// <param name="guard">
    /// Handler and line of the bail-out, supplied by the compiler. Never pass this explicitly.
    /// </param>
    /// <param name="line">Line of the bail-out, supplied by the compiler. Never pass this explicitly.</param>
    /// <remarks>
    /// The caller info exists because "which guard is costing me coverage?" had no answer short of
    /// reading eighty call sites and guessing. It cost real time: <c>OUTPUT</c> clauses were assumed
    /// to be what kept <c>INSERT</c> at the top of the histogram, and handling them moved coverage by
    /// 0.1 points because the actual bail-out was somewhere else entirely. A guard that declines to
    /// format is safe but never free, so it needs to be as measurable as a missing handler.
    /// <para>Caller <em>member name and line</em> rather than file path: the member name is the more
    /// useful half, and <c>CallerFilePath</c> would bake absolute build-machine paths into a shipped
    /// AOT binary. Recorded only when a sink is attached, so normal formatting allocates nothing.
    /// </para>
    /// </remarks>
    private Doc Passthrough(
        TSqlFragment node,
        [System.Runtime.CompilerServices.CallerMemberName] string guard = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        PassthroughCount++;
        _passedThrough.Add(node);

        if (_passthroughSink is not null)
        {
            _passthroughSink.Add(node);
            PassthroughGuards[node] = $"{guard}:{line}";
        }

        // Verbatim text already contains any terminator in its range, so claim that token index.
        // Without this, `CREATE PROC p AS SET @x = 1;` emits `;;` — the passthrough body prints
        // the semicolon and the enclosing procedure, which ends at the same token, adds another.
        foreach (var index in TrailingSemicolons(node))
        {
            _terminatorsEmitted.Add(index);
        }

        // Corrected start, so a passed-through node does not lose the keyword that introduces it.
        var first = EffectiveFirstToken(node);
        var last = node.LastTokenIndex;
        if (first < 0 || last < first)
        {
            return Doc.Empty;
        }

        var text = SqlText.Slice(_tokens, first, last);
        return CanReindent(first, last) ? Reindented(text) : Doc.Verbatim(text);
    }

    /// <summary>
    /// Whether a passthrough block's leading whitespace may be rewritten to match its new
    /// indentation.
    /// </summary>
    /// <remarks>
    /// Safe only when every newline in the range lives inside a whitespace token. If a newline
    /// sits inside a token, that token is a multi-line string literal or a block comment, and
    /// in both cases the interior whitespace is content: re-indenting a multi-line literal
    /// changes the string's value, and re-indenting a block comment reflows text the formatter
    /// has no business touching. The round-trip verifier would catch the literal case and
    /// refuse to format — correct, but useless. Better not to produce it.
    /// </remarks>
    private bool CanReindent(int first, int last)
    {
        var to = Math.Min(last, _tokens.Count - 1);
        for (var i = Math.Max(0, first); i <= to; i++)
        {
            var token = _tokens[i];
            if (!token.IsWhiteSpace() && CountNewLines(token.Text) > 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Re-emits multi-line text so it picks up the current indentation: strips the block's own
    /// common indent, then uses hard lines so the printer re-applies its own.
    /// </summary>
    private static Doc Reindented(string text)
    {
        var normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalised.Split('\n');
        if (lines.Length == 1)
        {
            return Doc.Text(lines[0]);
        }

        // The first line's indent is supplied by whatever precedes the block, so only
        // continuation lines contribute to the common prefix.
        var commonIndent = int.MaxValue;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0)
            {
                continue;
            }

            commonIndent = Math.Min(commonIndent, lines[i].Length - lines[i].TrimStart().Length);
        }

        if (commonIndent == int.MaxValue)
        {
            commonIndent = 0;
        }

        var parts = new List<Doc>(lines.Length * 2) { Doc.Text(lines[0].TrimEnd()) };
        for (var i = 1; i < lines.Length; i++)
        {
            parts.Add(Doc.HardLine);

            var line = lines[i];
            var stripped = line.Length >= commonIndent ? line[commonIndent..] : line.TrimStart();
            parts.Add(Doc.Text(stripped.TrimEnd()));
        }

        return Doc.Concat(parts);
    }

    private Doc Keyword(string text) => Doc.Text(
        _options.KeywordCase == KeywordCase.Lower ? text.ToLowerInvariant() : text.ToUpperInvariant());

    /// <summary>
    /// Source text between two token indices (inclusive), with comments dropped and whitespace
    /// runs collapsed to single spaces.
    /// </summary>
    /// <remarks>
    /// This is how operators and keyword sequences are recovered — <c>AND</c>, <c>&lt;&gt;</c>,
    /// <c>LEFT OUTER JOIN</c>, <c>DISTINCT TOP (10) PERCENT</c> — instead of mapping ScriptDom's
    /// enums to text. Three reasons it is better here:
    /// <list type="bullet">
    /// <item>It preserves what the author wrote: <c>!=</c> stays <c>!=</c> rather than being
    /// normalised to <c>&lt;&gt;</c>, which is a change no one asked for.</item>
    /// <item>It needs no knowledge of a dozen enum member sets, each a chance to be wrong in a
    /// way that only shows up on unusual input.</item>
    /// <item>It doubles as a safety check: when the text between two children is not what a
    /// handler expects, the construct has a modifier the handler does not know about, and it can
    /// bail to passthrough rather than silently dropping it.</item>
    /// </list>
    /// Comments are skipped because they are emitted separately through the attachment map;
    /// including them here would duplicate them.
    /// </remarks>
    private string SignificantTextBetween(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || toIndex < fromIndex || _tokens.Count == 0)
        {
            return string.Empty;
        }

        var to = Math.Min(toIndex, _tokens.Count - 1);
        var builder = new System.Text.StringBuilder();
        for (var i = Math.Max(0, fromIndex); i <= to; i++)
        {
            var token = _tokens[i];
            if (token.IsComment())
            {
                continue;
            }

            if (token.IsWhiteSpace())
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }

                continue;
            }

            builder.Append(token.Text);
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Emits a token range that no node claims, preserving its line structure.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="CasedTokens"/>, which collapses all whitespace to single spaces.
    /// That is wrong for unowned regions because a run of empty batches — `go` `go` `go` — has no
    /// AST nodes at all, and folding those onto one line produces invalid SQL: `GO` must stand
    /// alone on its line. Newlines in the source are therefore reproduced as breaks.
    /// </remarks>
    /// <param name="fromIndex">First token of the region, clamped to the stream.</param>
    /// <param name="toIndex">Last token of the region, clamped to the stream.</param>
    /// <param name="commentOwner">
    /// The node whose leading comments may fall inside this region. Each one found is emitted at the
    /// point it was written and recorded in <see cref="_hoistedComments"/> so the owner skips it.
    /// </param>
    private Doc UnownedRegion(int fromIndex, int toIndex, TSqlFragment? commentOwner = null)
    {
        var to = Math.Min(toIndex, _tokens.Count - 1);
        var from = Math.Max(0, fromIndex);

        // Trimmed to the significant tokens. Leaving the surrounding whitespace in would emit a
        // separator here *and* let the caller's own separator/blank-line logic add another, so every
        // pass grew the gap by a line — formatting stopped being a fixed point.
        //
        // Comments are trivia too, which is why the edges are trimmed of whitespace alone once there
        // is an owner: the comment written *before* the first stray token is precisely the one that
        // has to lead the region, and trimming it left it to its owner, printing it after every token
        // here. It cost the region its header comment and put two comments out of order — which the
        // verifier caught, refusing the file rather than shuffling them.
        bool Trimmable(TSqlParserToken token) =>
            commentOwner is null ? token.IsTrivia() : token.IsWhiteSpace();

        while (from <= to && Trimmable(_tokens[from]))
        {
            from++;
        }

        while (to >= from && Trimmable(_tokens[to]))
        {
            to--;
        }

        var parts = new List<Doc>();
        Doc? pendingSeparator = null;
        var previousWasGo = false;

        // Set after a comment, whose own doc already ends in the separator that follows it — a hard
        // line when it ended its line, a space when code shared it. Adding another would open a blank
        // line between a comment and the token it introduces.
        var afterComment = false;

        for (var i = from; i <= to; i++)
        {
            var token = _tokens[i];
            if (token.IsComment())
            {
                // A comment in this region normally belongs to a node printed later and is emitted
                // there. The exception is a comment written *between* these tokens: `-- a`, `GO`,
                // `-- b`, `GO` attaches both comments to the statement after the last GO, so leaving
                // them to their owner printed both GOs first and the comments after them. Emitting
                // each where it was written is the only order that matches what the author wrote.
                var owned = commentOwner is null ? null : LeadingCommentAt(commentOwner, i);
                if (owned is null || !_hoistedComments.Add(owned))
                {
                    continue;   // emitted via attachment, or already hoisted by a caller
                }

                if (parts.Count > 0 && !afterComment)
                {
                    parts.Add(Doc.HardLine);
                }

                parts.Add(CommentDocs.Leading(owned));
                pendingSeparator = null;
                previousWasGo = false;
                afterComment = true;
                continue;
            }

            if (token.IsWhiteSpace())
            {
                pendingSeparator = CountNewLines(token.Text) > 0 ? Doc.HardLine : Doc.Text(" ");
                continue;
            }

            var isGo = token.TokenType == TSqlTokenType.Go;
            if (parts.Count > 0 && !afterComment)
            {
                // Always a break around GO: it has to stand alone on its line, so reproducing the
                // source's spacing is not an option, and normalising it keeps the result stable.
                parts.Add(isGo || previousWasGo ? Doc.HardLine : pendingSeparator ?? Doc.Empty);
            }

            afterComment = false;
            pendingSeparator = null;
            previousWasGo = isGo;
            parts.Add(CasedTokens(i, i));
        }

        return Doc.Concat(parts);
    }

    /// <summary>
    /// Verifies a wrapper node really is just <c>( child )</c> with nothing else inside its range.
    /// </summary>
    /// <remarks>
    /// Both ends need checking, and for different reasons. Trailing: <c>(SELECT …) COLLATE x</c>
    /// keeps the collation inside the wrapper's range, so emitting <c>(</c>, the child and <c>)</c>
    /// dropped it. Leading: <c>(MATCH(a AND b))</c> gives the child a range starting after
    /// <c>MATCH(</c>, so the same emission dropped <c>MATCH(</c> and produced output that no longer
    /// parsed.
    /// </remarks>
    private bool IsPlainParenthesised(TSqlFragment node, TSqlFragment child) =>
        SignificantTextBetween(node.FirstTokenIndex, EffectiveFirstToken(child) - 1) == "("
        && SignificantTextBetween(child.LastTokenIndex + 1, node.LastTokenIndex) == ")";

    /// <summary>Operator or keyword text sitting between two sibling nodes.</summary>
    private string TextBetween(TSqlFragment left, TSqlFragment right) =>
        SignificantTextBetween(left.LastTokenIndex + 1, EffectiveFirstToken(right) - 1);

    /// <summary>
    /// The keyword run between two sibling nodes, cased per token.
    /// </summary>
    /// <remarks>
    /// Preferred over <c>Keyword(TextBetween(…))</c> wherever the run can contain a non-reserved
    /// word. <c>CROSS APPLY</c> is the example that forced this: <c>APPLY</c> lexes as an
    /// identifier, so uppercasing the whole run was a token change the verifier rejected — the
    /// same trap as <c>WITH ROLLUP</c> and <c>BEGIN TRY</c>.
    /// </remarks>
    private Doc CasedTokensBetween(TSqlFragment left, TSqlFragment right) =>
        CasedTokens(left.LastTokenIndex + 1, EffectiveFirstToken(right) - 1);

    /// <summary>
    /// Whether nothing significant remains between a node and the end of the statement that
    /// contains it, ignoring the statement terminator.
    /// </summary>
    /// <remarks>
    /// A handler that emits a known set of children must confirm it has emitted <em>everything</em>.
    /// `INSERT @t EXECUTE sp_executesql @sql;;` has a stray extra semicolon after the insert source,
    /// and without this check it was silently dropped. Cheap insurance against token loss anywhere a
    /// statement's tail is assumed empty.
    /// </remarks>
    private bool NothingAfter(TSqlFragment lastChild, TSqlStatement statement)
    {
        var end = RangeEndBeforeTerminators(statement);
        return SignificantTextBetween(lastChild.LastTokenIndex + 1, end).Length == 0;
    }

    /// <summary>
    /// Whether a statement's clauses sit end to end, with nothing between consecutive ones. Nulls
    /// are the absent optional clauses and are skipped.
    /// </summary>
    /// <remarks>
    /// The companion to <see cref="NothingAfter"/> and <see cref="TryPrintOptionClause"/>, which
    /// cover the tail: together the three account for every token in a statement built from a run
    /// of optional clauses — <c>UPDATE … SET … OUTPUT … FROM … WHERE … OPTION (…)</c>. Checking
    /// only the tail is not enough, because a construct this printer does not model can just as
    /// easily land <em>between</em> two clauses it does, and there it would be dropped in silence.
    /// <para>Clauses must be given in token order, which for these statements is the order they
    /// are written. <see cref="EffectiveFirstToken"/> is used on the right of each gap so a clause
    /// whose range omits its own leading keyword does not make the gap look non-empty.</para>
    /// </remarks>
    private bool ClausesAbut(params TSqlFragment?[] clauses)
    {
        var present = clauses.Where(c => c is not null).ToList();

        for (var i = 1; i < present.Count; i++)
        {
            if (SignificantTextBetween(
                    present[i - 1]!.LastTokenIndex + 1,
                    EffectiveFirstToken(present[i]!) - 1).Length > 0)
            {
                return false;
            }
        }

        return present.Count > 0;
    }

    /// <summary>
    /// The last of a statement's clauses to appear in the source, ignoring absent ones.
    /// </summary>
    /// <remarks>
    /// Not simply the last non-null argument: which optional clause ends a statement depends on
    /// which the author wrote, and <see cref="TryPrintOptionClause"/> needs the true end of the
    /// modelled text to know where the hint clause begins.
    /// </remarks>
    private static TSqlFragment LastOf(params TSqlFragment?[] clauses)
    {
        TSqlFragment? last = null;
        foreach (var clause in clauses)
        {
            if (clause is not null && (last is null || clause.LastTokenIndex > last.LastTokenIndex))
            {
                last = clause;
            }
        }

        return last ?? throw new ArgumentException("no clause present", nameof(clauses));
    }

    /// <summary>
    /// Emits a statement's trailing <c>OPTION (…)</c> query-hint clause on its own line, or nothing
    /// when the statement has none. False when the tail is not a shape this printer can account for.
    /// </summary>
    /// <remarks>
    /// Read as one token slice rather than rebuilt from the <c>OptimizerHint</c> nodes, and that is
    /// not a shortcut — it is the only correct option. <c>MAXDOP 1</c> parses as a
    /// <c>LiteralOptimizerHint</c> whose range covers just the <c>1</c>, so rejoining the hint nodes
    /// with commas would silently drop the word <c>MAXDOP</c>; and the <c>OPTION</c> keyword with its
    /// parentheses belongs to no node at all. Slicing the tokens reproduces every hint kind,
    /// including ones added to T-SQL after this was written.
    /// <para>Also serves as the tail check when there are no hints, so a handler gets both for one
    /// call and cannot accidentally verify one and not the other. This deferral was expensive: the
    /// same <c>OPTION (RECOMPILE)</c> guard was the leading cause of passthrough for
    /// <c>INSERT</c>, <c>UPDATE</c> <em>and</em> <c>SELECT</c> — the top four ranks of the histogram
    /// between them.</para>
    /// </remarks>
    private bool TryPrintOptionClause(
        TSqlStatement statement,
        IList<OptimizerHint> hints,
        TSqlFragment lastClause,
        out Doc clause)
    {
        clause = Doc.Empty;

        if (hints.Count == 0)
        {
            return NothingAfter(lastClause, statement);
        }

        var from = lastClause.LastTokenIndex + 1;
        var end = RangeEndBeforeTerminators(statement);

        // The slice has to be the hint clause and nothing but: introduced by OPTION, and wide
        // enough to contain every hint the parser found. Anything else means a construct sits
        // between the last clause and the hints, and emitting the slice would move it.
        var optionKeyword = FirstSignificantToken(from, end);

        // Comments *inside* the clause — `OPTION (RECOMPILE, /* why */ MAXDOP 1)` — have no faithful
        // position in a slice that CasedTokens strips them from, so those still fall back to
        // verbatim. Comments *before* the OPTION keyword are the common case by far and are handled
        // below rather than declined: an explanatory line above `OPTION (RECOMPILE)` is idiomatic,
        // and refusing it cost more coverage on UPDATE alone than several missing handlers.
        if (optionKeyword < 0
            || !SignificantTextBetween(from, end).StartsWith("OPTION", StringComparison.OrdinalIgnoreCase)
            || hints[0].FirstTokenIndex < from
            || hints[^1].LastTokenIndex > end
            || !NoCommentsIn(optionKeyword, end))
        {
            return false;
        }

        // WithComments on the first hint, because that is where a comment above the clause attaches:
        // the OPTION keyword and its parentheses belong to no node, so the attacher gives the comment
        // to the nearest node that follows — the first OptimizerHint — and this handler never Prints
        // the hints. Four corpus files lost a comment that way, caught only by the preservation gate.
        // `OPTION (RECOMPILE, MAXDOP 1)` is all keywords and literals, so its non-reserved words can be
        // recased — but `OPTION (TABLE HINT (t, INDEX(ix)))` names a table and an index in the same
        // slice. `TableHintsOptimizerHint` is the only hint kind that can, so testing for it is exact:
        // every other kind carries a literal or nothing at all.
        var namesAnObject = hints.Any(h => h is TableHintsOptimizerHint);

        clause = Doc.Concat(
            Doc.HardLine,
            WithComments(
                hints[0],
                namesAnObject ? CasedTokens(optionKeyword, end) : Keywords(optionKeyword, end)));
        return true;
    }

    /// <summary>
    /// Emits the <c>WITH &lt;cte&gt;, …</c> prologue that <c>SELECT</c>, <c>INSERT</c>,
    /// <c>UPDATE</c> and <c>DELETE</c> all share, or nothing when the statement has none. False
    /// when the clause is not a shape this printer models.
    /// </summary>
    /// <remarks>
    /// Handlers that reconstruct their leading keyword must slice from
    /// <see cref="CteBodyStart"/> rather than the statement's own first token: with a CTE present
    /// the statement's range begins at <c>WITH</c>, so the usual
    /// <c>[FirstTokenIndex .. firstChild)</c> head slice would re-emit the entire CTE list.
    /// <para>The prologue must also be concatenated <em>outside</em> any
    /// <see cref="WithComments"/> wrapper. A comment between the CTE list and the statement's own
    /// keyword — <c>WITH c AS (…)</c>, then <c>/* why we insert */</c>, then <c>INSERT</c> —
    /// attaches as a leading comment of the specification node, and wrapping the prologue in
    /// <c>WithComments</c> emitted it above the <c>WITH</c>: the comment jumped over the very
    /// clauses it followed, and then moved again on the next pass.</para>
    /// </remarks>
    private bool TryPrintCtes(StatementWithCtesAndXmlNamespaces statement, out Doc prologue)
    {
        prologue = Doc.Empty;

        var ctes = statement.WithCtesAndXmlNamespaces;
        if (ctes is null)
        {
            return true;
        }

        // A change-tracking context (`WITH CHANGE_TRACKING_CONTEXT (@ctx)`) shares the WITH clause
        // but has a layout of its own and is not modelled. XML namespaces used to be declined here
        // too; PrintCtes now handles them.
        //
        // An empty CTE list is only a problem when there are no namespaces either:
        // `WITH XMLNAMESPACES('…' AS p) UPDATE …` is a complete, common statement with no CTE at all,
        // and requiring one was the second-largest over-eager bail-out — 6% of declined text across
        // the UPDATE and INSERT handlers together.
        if (ctes.ChangeTrackingContext is not null
            || (ctes.CommonTableExpressions.Count == 0 && ctes.XmlNamespaces is null))
        {
            return false;
        }

        prologue = Doc.Concat(Print(ctes), Doc.HardLine);
        return true;
    }

    /// <summary>
    /// The token index at which a statement's own text begins — after its CTE prologue, if it has
    /// one.
    /// </summary>
    private static int CteBodyStart(StatementWithCtesAndXmlNamespaces statement) =>
        statement.WithCtesAndXmlNamespaces is { } ctes
            ? ctes.LastTokenIndex + 1
            : statement.FirstTokenIndex;

    /// <summary>
    /// A node's first token, corrected for ScriptDom ranges that omit the node's own leading
    /// keyword.
    /// </summary>
    /// <remarks>
    /// Several node types start their range <em>after</em> the keyword that introduces them.
    /// <c>ExistsPredicate</c> begins at the <c>(</c>, not at <c>EXISTS</c>; <c>ForClause</c> begins
    /// after <c>FOR</c>. Left uncorrected this misfires in two directions at once: an enclosing
    /// operator slice swallows the keyword (so <c>a AND EXISTS (…)</c> read its operator as
    /// <c>AND EXISTS</c>, and the handler then emitted <c>EXISTS</c> again — output that no longer
    /// parsed), while passthrough of the same node drops the keyword entirely.
    /// <para>Correcting the boundary once fixes both, and the table grows as the corpus finds more
    /// types rather than requiring every handler to know the quirk. The scan is conservative: it
    /// only claims the keyword when it is the token immediately before the node.</para>
    /// </remarks>
    private int EffectiveFirstToken(TSqlFragment node)
    {
        // The correction has to propagate up the left spine, not just apply to the node itself.
        // `WHERE EXISTS (…) AND x = 1` gives the AND chain a range starting at the EXISTS predicate's
        // `(`, because a chain begins where its leftmost operand begins — so the enclosing clause read
        // its keyword slice as `WHERE EXISTS`, decided it was not exactly `WHERE`, and declined the
        // whole clause. That made a completely ordinary predicate the seventh-largest bail-out in the
        // corpus.
        //
        // Iterative for the usual reason: a legacy OR chain can be thousands of terms deep, and this
        // walks exactly that spine.
        //
        // `ALTER TABLE t ADD PERIOD FOR SYSTEM_TIME (s, e)` is the same failure through a different
        // parent. The TableDefinition holds only the period, so the definition's range starts where the
        // period's does — at `s`. Correcting the period alone made the two disagree: the enclosing
        // handler sliced its head up to the definition's *raw* start and emitted
        // `ALTER TABLE T ADD PERIOD FOR SYSTEM_TIME (`, then the period emitted its own keywords again.
        // Three corpus files stopped parsing, which is the round-trip verifier earning its keep.
        while (true)
        {
            if (node is BooleanBinaryExpression { FirstExpression: not null } chain)
            {
                node = chain.FirstExpression;
                continue;
            }

            if (node is TableDefinition { SystemTimePeriod: { } period }
                && period.FirstTokenIndex == node.FirstTokenIndex)
            {
                node = period;
                continue;
            }

            break;
        }

        return node switch
        {
            ExistsPredicate => ClaimPrecedingKeyword(TSqlTokenType.Exists, node.FirstTokenIndex),

            // A FOR clause's range can begin anywhere from its own `FOR` to past `FOR XML`, depending
            // on the variant — so the scan has to look back several tokens rather than exactly one.
            // Three covers `FOR XML`, `FOR BROWSE` and `FOR READ ONLY`. Getting this wrong left the
            // option stranded on its own: `ORDER BY x PATH('r')`.
            ForClause => ClaimPrecedingKeyword(TSqlTokenType.For, node.FirstTokenIndex, lookBack: 3),

            // The worst offender found so far, and the only one that misses its keywords *and* claims
            // a delimiter it never opened: `PERIOD FOR SYSTEM_TIME (s, e)` gets the range `s, e)`. It
            // begins at the first column name, four significant tokens past its own start, and ends on
            // the parenthesis that closes it. Correcting the start is enough — the stray `)` on the end
            // is harmless here because it makes the node's range abut the table's own closing
            // parenthesis, which is exactly where CREATE TABLE resumes.
            //
            // No token type names `PERIOD` or `SYSTEM_TIME` (both lex as Identifier, and
            // TSqlTokenType.Period is the dot operator), so this one is matched on the word sequence.
            SystemTimePeriodDefinition => ClaimPrecedingWords(
                node.FirstTokenIndex, "PERIOD", "FOR", "SYSTEM_TIME", "("),

            // `END CONVERSATION @handle` is the same trap: the range starts at the *handle*, so an
            // enclosing block slicing up to its first statement swallowed the statement's keywords
            // and printed `BEGIN CATCH END CONVERSATION` with the handle stranded on the next line.
            // Claiming them here is only half the fix — PrintEndConversation emits them, because a
            // node whose range excludes its own keywords cannot be passed through either.
            EndConversationStatement => ClaimPrecedingWords(node.FirstTokenIndex, "END", "CONVERSATION"),

            _ => node.FirstTokenIndex,
        };
    }

    /// <summary>
    /// Extends a node's start backwards over an exact sequence of preceding words, given in source
    /// order. Returns the original index unless every one of them matches.
    /// </summary>
    /// <remarks>
    /// The text-matching counterpart to <see cref="ClaimPrecedingKeyword"/>, for constructs whose
    /// introducing words are non-reserved and so have no token type of their own. Requiring the whole
    /// sequence — including the opening parenthesis — is what keeps it from matching a column that
    /// merely happens to be called <c>period</c>: all four tokens must be in place, adjacent, in
    /// order, and immediately before the node.
    /// </remarks>
    private int ClaimPrecedingWords(int firstTokenIndex, params string[] words)
    {
        var index = firstTokenIndex;

        for (var w = words.Length - 1; w >= 0; w--)
        {
            var previous = PreviousSignificantToken(index - 1);
            if (previous < 0
                || !string.Equals(_tokens[previous].Text, words[w], StringComparison.OrdinalIgnoreCase))
            {
                return firstTokenIndex;
            }

            index = previous;
        }

        return index;
    }

    /// <summary>The last significant token at or before <paramref name="fromIndex"/>, or -1.</summary>
    private int PreviousSignificantToken(int fromIndex)
    {
        for (var i = Math.Min(fromIndex, _tokens.Count - 1); i >= 0; i--)
        {
            if (!_tokens[i].IsTrivia())
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Extends a node's start backwards onto its introducing keyword, searching at most
    /// <paramref name="lookBack"/> significant tokens. Returns the original index if not found.
    /// </summary>
    private int ClaimPrecedingKeyword(TSqlTokenType keyword, int firstTokenIndex, int lookBack = 1)
    {
        var remaining = lookBack;
        for (var i = Math.Min(firstTokenIndex, _tokens.Count) - 1; i >= 0 && remaining > 0; i--)
        {
            if (_tokens[i].IsTrivia())
            {
                continue;
            }

            if (_tokens[i].TokenType == keyword)
            {
                return i;
            }

            remaining--;
        }

        return firstTokenIndex;
    }

    /// <summary>
    /// Emits a token range with keyword casing applied per token, leaving identifiers, literals
    /// and variables exactly as written.
    /// </summary>
    /// <remarks>
    /// Needed wherever a slice mixes keywords with names — <c>WITH ROLLUP</c>,
    /// <c>WITH (INDEX(IX_Foo))</c>, <c>DISTINCT TOP (10) PERCENT</c>. Recasing the whole slice
    /// would rewrite identifiers, which under a case-sensitive collation changes which object is
    /// referenced; leaving the whole slice alone means keyword casing silently stops applying in
    /// those constructs. Per-token is the only answer that is both correct and consistent, and it
    /// shares its classification with the round-trip verifier so it can never recase something
    /// the verifier will then reject.
    /// </remarks>
    private Doc CasedTokens(int fromIndex, int toIndex) => Slice(fromIndex, toIndex, pureKeywords: false);

    /// <summary>
    /// A range the parser has already proved to be a built-in callee's name: a keyword slice when
    /// <see cref="FormatOptions.RecaseBuiltInFunctions"/> is on, the author's text when it is off.
    /// </summary>
    private Doc CasedOrKeyword(int fromIndex, int toIndex) =>
        Slice(fromIndex, toIndex, pureKeywords: _options.RecaseBuiltInFunctions);

    /// <summary>
    /// Emits a token range that is <em>known to contain no object names</em>, recasing the
    /// non-reserved words in it as the keywords they are.
    /// </summary>
    /// <remarks>
    /// <para>T-SQL has a large vocabulary of words that are keywords grammatically but lex as
    /// <see cref="TSqlTokenType.Identifier"/> because they are not reserved: <c>NVARCHAR</c>,
    /// <c>NOCOUNT</c>, <c>APPLY</c>, <c>OUTPUT</c>, <c>CAST</c>, <c>NOWAIT</c>, <c>TRY</c>. Under
    /// <see cref="KeywordCase.Upper"/> the author's <c>declare @a int</c> came out as
    /// <c>DECLARE @a int</c>, because the printer could not distinguish those words from a table
    /// called <c>int</c>.</para>
    /// <para><b>Why this is opt-in per site rather than a list of words.</b> Reading the corpus
    /// showed the two populations share the same slices: alongside <c>NVARCHAR</c> and <c>NOCOUNT</c>
    /// sit <c>dbo</c>, <c>t_history</c>, <c>SQL_Latin1_General_CP1_CI_AS</c>, <c>DatabaseName</c> and
    /// <c>COL0</c> — a schema, a history table, a collation, a column. Matching on spelling would
    /// eventually rename one of them, and under a case-sensitive collation that is silent corruption.
    /// So a handler calls this only where the grammar guarantees a name cannot appear, and says why.
    /// </para>
    /// <para>Two traps found while choosing those sites, both of which would have broken code:
    /// <c>SetIdentityInsertStatement</c> derives from <c>SetOnOffStatement</c> and carries a table
    /// name, so "a SET statement is all keywords" is false; and <c>LabelStatement</c> holds its label
    /// in a plain <c>string</c> property with no AST node at all, so "the node has no children"
    /// does not imply "the tokens are all keywords" either.</para>
    /// <para>Every index recased here is recorded and handed to
    /// <see cref="RoundTripVerifier"/>, which then compares those positions case-insensitively and
    /// every other identifier exactly. The permission is per token, not a relaxed rule — so a handler
    /// that opts in wrongly still gets caught for every identifier it did not claim.</para>
    /// </remarks>
    private Doc Keywords(int fromIndex, int toIndex) => Slice(fromIndex, toIndex, pureKeywords: true);

    /// <summary>
    /// Emits a token range with keyword casing applied per token, leaving identifiers, literals
    /// and variables exactly as written.
    /// </summary>
    /// <remarks>
    /// Needed wherever a slice mixes keywords with names — <c>WITH (INDEX(IX_Foo))</c>,
    /// <c>COLLATE SQL_Latin1_General_CP1_CI_AS</c>, <c>DISTINCT TOP (10) PERCENT</c>. Recasing the
    /// whole slice would rewrite identifiers, which under a case-sensitive collation changes which
    /// object is referenced; leaving the whole slice alone means keyword casing silently stops
    /// applying in those constructs. Per-token is the only answer that is both correct and
    /// consistent, and outside a <see cref="Keywords"/> region it shares its classification with the
    /// round-trip verifier so it can never recase something the verifier will then reject.
    /// </remarks>
    /// <param name="fromIndex">First token of the slice.</param>
    /// <param name="toIndex">Last token of the slice.</param>
    /// <param name="pureKeywords">
    /// The region's grammar admits no object name, so every identifier in it may be recased as a
    /// keyword.
    /// </param>
    /// <param name="extraKeywordPositions">
    /// Token indices to treat as keyword positions even in a region that is <em>not</em> pure keywords.
    /// For constructs where part of the scaffolding is provably grammar and part of it provably is not:
    /// a column definition's modifiers hold <c>GENERATED ALWAYS AS ROW START</c> — every word of which
    /// is fixed by the <c>GeneratedAlways</c> enum — in the same region as
    /// <c>COLLATE &lt;collation&gt;</c> and <c>CONSTRAINT &lt;name&gt;</c>, which are object names. One
    /// flag for the whole region can only be wrong in one direction or the other; naming the positions
    /// is what makes both correct at once.
    /// </param>
    /// <param name="preserveCase">
    /// Emit every token exactly as written, recasing nothing — not even reserved words. Used by the
    /// generic fallback in <see cref="PrintGeneric"/>, where the keyword vocabulary is unknown: recasing
    /// only the reserved words there produced <c>GRANT select on dbo.t TO [role]</c>, which is worse than
    /// leaving the statement alone. Normalised spacing and descent into children are worth having on
    /// their own; half-applied casing is not.
    /// </param>
    private Doc Slice(
        int fromIndex,
        int toIndex,
        bool pureKeywords,
        IReadOnlySet<int>? extraKeywordPositions = null,
        bool preserveCase = false)
    {
        if (fromIndex < 0 || toIndex < fromIndex || _tokens.Count == 0)
        {
            return Doc.Empty;
        }

        var to = Math.Min(toIndex, _tokens.Count - 1);
        var builder = new System.Text.StringBuilder();

        // A slice is a run of text, but a comment inside one has to stay a comment: it may need a
        // line of its own, and it must not be recased. So the run is emitted in pieces, with those
        // comments the slice is responsible for placed between them.
        var parts = new List<Doc>();

        // Trimmed at the slice's own edges only. Trimming every run would eat the space in front of
        // an inline comment and print `NOT/* … */ NULL`; that space belongs to the text, and only the
        // comment knows whether a break follows it.
        void Flush(bool trimEnd)
        {
            var run = builder.ToString();
            builder.Clear();

            if (parts.Count == 0)
            {
                run = run.TrimStart();
            }

            if (trimEnd)
            {
                run = run.TrimEnd();
            }

            if (run.Length > 0)
            {
                parts.Add(Doc.Text(run));
            }
        }

        for (var i = Math.Max(0, fromIndex); i <= to; i++)
        {
            var token = _tokens[i];
            if (token.IsComment())
            {
                if (!OwnedByThisSlice(i, fromIndex, to, out var comment))
                {
                    continue;   // emitted by the node it belongs to
                }

                MarkEmitted(comment);

                // A comment that had a line of its own keeps it. Letting it join the preceding text
                // would put code to its left, which reclassifies it as end-of-line on the next pass
                // and moves it again — the oscillation SeparatorBefore exists to prevent.
                Flush(trimEnd: comment.AloneOnLine);

                if (comment.AloneOnLine && parts.Count > 0)
                {
                    parts.Add(Doc.HardLine);
                }

                // Supplies its own trailing separator: a hard line when it ended its line, a space
                // when code followed it there.
                parts.Add(CommentDocs.Leading(comment));
                continue;
            }

            if (token.IsWhiteSpace())
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }

                continue;
            }

            var text = token.Text ?? string.Empty;
            var isIdentifier = token.TokenType == TSqlTokenType.Identifier;

            // The one other token type a claim may cover. `@@ROWCOUNT` lexes as a Variable, so the
            // identifier rule below would leave it — and only the handler that put it in a keyword
            // region knows it is a system variable rather than someone's `DECLARE @@MyVar`.
            var isGlobalVariable = token.IsGlobalVariable();
            var keywordPosition = pureKeywords || (extraKeywordPositions?.Contains(i) ?? false);

            if (_passthroughSink is not null && isIdentifier && !keywordPosition)
            {
                // Diagnostics only, and only when a sink is attached: the identifier-typed words still
                // being left alone, which is the shortlist of sites that could be opted in next.
                KeywordSliceIdentifiers.TryGetValue(text, out var seen);
                KeywordSliceIdentifiers[text] = seen + 1;
            }

            // A pure-keyword region recases identifiers and `@@` variables — and nothing else. String
            // literals, quoted identifiers, local variables and numbers keep their exact text even
            // here, because `EXECUTE AS 'user'` and `[My Column]` are data whatever region they sit in.
            var recase = !preserveCase
                && (!token.TokenType.CarriesValue()
                    || (keywordPosition && (isIdentifier || isGlobalVariable)));

            if (recase && (isIdentifier || isGlobalVariable))
            {
                _keywordCasedTokens.Add(i);
            }

            builder.Append(recase
                ? _options.KeywordCase == KeywordCase.Lower ? text.ToLowerInvariant() : text.ToUpperInvariant()
                : text);
        }

        Flush(trimEnd: true);

        return parts.Count switch
        {
            0 => Doc.Empty,
            1 => parts[0],
            _ => Doc.Concat(parts),
        };
    }

    /// <summary>
    /// Whether a comment inside a slice is the slice's to emit — because the node it was assigned
    /// to lies wholly inside the slice, and so is never dispatched.
    /// </summary>
    /// <remarks>
    /// The discriminator the first two attempts at this lacked. A slice reproduces its tokens as
    /// text and descends into nothing, so a comment whose owner is inside it would be emitted by
    /// nobody — which is how `NOT /* … */ NULL` lost its comment and had the whole file refused. But
    /// a comment whose owner *straddles or contains* the slice belongs to a node that is printed
    /// elsewhere, with the surrounding layout in hand; emitting it here as well stole it from a
    /// handler that was placing it correctly, at this slice's indent instead of its own. Nine tests
    /// said so the first time and eight the second, the round-trip verifier among them.
    /// <para>An unowned comment is left alone: the attacher reports those separately and a script
    /// carrying one is refused outright, so a slice inventing a home for it would only hide that.</para>
    /// </remarks>
    private bool OwnedByThisSlice(int commentIndex, int fromIndex, int toIndex, out Comment comment)
    {
        comment = null!;

        if (CommentAt(commentIndex) is not { } found)
        {
            return false;
        }

        comment = found;

        return !_hoistedComments.Contains(found)
            && _comments.Owner(found) is { } owner
            && owner.FirstTokenIndex >= fromIndex
            && owner.LastTokenIndex <= toIndex
            && owner.FirstTokenIndex >= 0;
    }

    /// <summary>
    /// Where a slice must begin to cover the comments attached to a node it is emitting in place of
    /// <see cref="Print"/>.
    /// </summary>
    /// <remarks>
    /// A handler that slices a node rather than printing it takes on the node's comments too: nothing
    /// else will emit them. Widening the slice to start at the first leading comment puts the node's
    /// range inside the slice, which is the condition on which the slice claims a comment as its own.
    /// </remarks>
    private int SliceStartCoveringComments(TSqlFragment node)
    {
        var start = EffectiveFirstToken(node);
        foreach (var comment in _comments.Leading(node))
        {
            if (comment.TokenIndex < start)
            {
                start = comment.TokenIndex;
            }
        }

        return start;
    }

    /// <summary>
    /// Where a slice must end to cover the comments trailing a node it is emitting in place of
    /// <see cref="Print"/>.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="SliceStartCoveringComments"/>, for a comment written *after* the
    /// sliced node: `@x.nodes /* … */ ('/p')` trails the method name, and the gap between the name
    /// and its argument list belonged to no emitter at all.
    /// </remarks>
    private int SliceEndCoveringComments(TSqlFragment node)
    {
        var end = node.LastTokenIndex;
        foreach (var comment in _comments.Trailing(node))
        {
            if (comment.TokenIndex > end)
            {
                end = comment.TokenIndex;
            }
        }

        return end;
    }

    /// <summary>
    /// Comments in a range that no node will emit, as their own indented lines, claimed so nothing
    /// tries to emit them again.
    /// </summary>
    /// <remarks>
    /// For a handler that owns a region of raw tokens outright. A comment before the closing
    /// parenthesis of a function's parameter list attaches to the next *node* the attacher can find,
    /// which is a <c>WITH SCHEMABINDING</c> option ten tokens later — emitted by a slice that starts
    /// well past the comment, so nothing emitted it at all. The handler knows where the author put
    /// it even though no node does.
    /// </remarks>
    private Doc ClaimCommentsIn(int fromIndex, int toIndex)
    {
        // Only the orphans. A comment here may just as easily trail the last item in the region —
        // `@a INT /* about a */ )` — and that one is emitted by the item's own Print; claiming it too
        // printed it twice. The ones nothing will emit are those whose owner starts *past* the
        // region, which is the attacher reaching forward for the next node it can find.
        var found = CommentsIn(fromIndex, toIndex)
            .Where(comment => _comments.Owner(comment) is { } owner && owner.FirstTokenIndex > toIndex)
            .ToList();

        if (found.Count == 0)
        {
            return Doc.Empty;
        }

        foreach (var comment in found)
        {
            MarkEmitted(comment);
        }

        return Doc.Indent(Doc.Concat(Doc.HardLine, CommentDocs.Dangling(found)));
    }

    /// <summary>The comment at a token index, or null.</summary>
    private Comment? CommentAt(int tokenIndex)
    {
        if (_commentByToken is null)
        {
            _commentByToken = [];
            foreach (var known in _comments.All)
            {
                _commentByToken[known.TokenIndex] = known;
            }
        }

        return _commentByToken.TryGetValue(tokenIndex, out var value) ? value : null;
    }

    /// <summary>
    /// Whether the construct is followed by an explicit semicolon. A statement's token range
    /// includes its terminator, so a reconstructed statement has to re-emit it — and inventing
    /// one where the author had none would be an unrequested change.
    /// </summary>
    private bool HasTrailingSemicolon(TSqlFragment node)
    {
        var index = node.LastTokenIndex;
        return index >= 0 && index < _tokens.Count && _tokens[index].TokenType == TSqlTokenType.Semicolon;
    }

    /// <summary>
    /// Last token of a node's range, excluding <em>every</em> trailing semicolon.
    /// </summary>
    /// <remarks>
    /// Handlers use this to check that nothing unexpected follows their last child. Excluding only
    /// one semicolon left `END;;` looking like `END ;` — so every block with a doubled terminator was
    /// dropped into passthrough, which turned out to be the single largest remaining source of
    /// unformatted text.
    /// </remarks>
    private int RangeEndBeforeTerminators(TSqlFragment node)
    {
        var semicolons = TrailingSemicolons(node);
        return semicolons.Count == 0 ? node.LastTokenIndex : semicolons[^1] - 1;
    }

    /// <summary>
    /// Indices of the consecutive semicolons that end a node's range, outermost last.
    /// </summary>
    private List<int> TrailingSemicolons(TSqlFragment node)
    {
        var found = new List<int>();
        for (var i = Math.Min(node.LastTokenIndex, _tokens.Count - 1); i >= 0; i--)
        {
            if (_tokens[i].IsTrivia())
            {
                continue;
            }

            if (_tokens[i].TokenType != TSqlTokenType.Semicolon)
            {
                break;
            }

            found.Add(i);
        }

        return found;
    }

    /// <summary>
    /// Index of the first non-trivia token in a range, or -1 if there is none.
    /// </summary>
    /// <remarks>
    /// Distinguishes "the region a handler emits" from "the whitespace and comments in front of it",
    /// which is what lets a keyword slice start at its keyword while the comments above it are
    /// emitted through attachment instead.
    /// </remarks>
    private int FirstSignificantToken(int fromIndex, int toIndex)
    {
        for (var i = Math.Max(0, fromIndex); i <= Math.Min(toIndex, _tokens.Count - 1); i++)
        {
            if (!_tokens[i].IsTrivia())
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether a token range contains no comments.
    /// </summary>
    /// <remarks>
    /// Guards the handlers that emit a keyword run through <see cref="CasedTokens"/>, which skips
    /// comments because they are normally emitted via attachment instead. A comment sitting inside
    /// such a run therefore vanishes — silent comment loss, caught only by the preservation check
    /// at the end. Bailing to passthrough for the whole construct is the safe answer.
    /// </remarks>
    private bool NoCommentsIn(int fromIndex, int toIndex)
    {
        for (var i = Math.Max(0, fromIndex); i <= Math.Min(toIndex, _tokens.Count - 1); i++)
        {
            if (_tokens[i].IsComment())
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Verifies that consecutive children really are separated by <paramref name="expected"/>.
    /// </summary>
    /// <remarks>
    /// Emitting a list means choosing a separator, and every handler here assumed a comma. The
    /// corpus proved that wrong: <c>TRIM('[]' FROM x)</c> is a function call whose two arguments
    /// are separated by <c>FROM</c>, and re-joining them with a comma produced
    /// <c>TRIM('[]', x)</c> — different SQL, silently.
    /// <para>The lesson generalises past that one function. Validating the text before the first
    /// child and after the last, as the handlers already did, says nothing about the gaps in
    /// between. So every list-shaped handler now checks every gap, and bails to passthrough when
    /// the separator is not what it assumed. A construct emitted verbatim is a cosmetic loss; a
    /// construct whose separator was rewritten is a correctness bug.</para>
    /// </remarks>
    private bool SeparatedBy<T>(IList<T> children, string expected = ",")
        where T : TSqlFragment
    {
        for (var i = 1; i < children.Count; i++)
        {
            if (!string.Equals(TextBetween(children[i - 1], children[i]), expected, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The separator to put between a keyword and the node that follows it: a space normally, a
    /// break when that node carries a comment which had a line of its own.
    /// </summary>
    /// <remarks>
    /// Without this, <c>AND</c> on its own line followed by <c>/* why */</c> on its own line prints
    /// as <c>AND /* why */</c> — the comment climbs onto the operator's line. Worse, it does not
    /// stay there: on the next pass the comment now has code to its left, so it reclassifies as
    /// end-of-line, attaches to the preceding operand instead, and moves again. Respecting the
    /// comment's own line keeps the author's shape and makes the result a fixed point.
    /// </remarks>
    private Doc SeparatorBefore(TSqlFragment node)
    {
        // The filtered view, not the raw one: a comment already emitted in front of the operator must
        // not also break the line after it, or the operand drops below an operator with nothing on
        // its line.
        var leading = RemainingLeading(node);
        return leading.Count > 0 && leading[0].AloneOnLine ? Doc.HardLine : Doc.Text(" ");
    }

    /// <summary>
    /// The leading comments of <paramref name="node"/>, to be emitted <em>before</em> the operator
    /// keyword that introduces it, or <c>null</c> when they do not belong there.
    /// </summary>
    /// <param name="node">The operand whose leading comments may need hoisting.</param>
    /// <param name="operatorFrom">Start of the token range between the previous operand and this one.</param>
    /// <param name="operatorTo">End of that range.</param>
    /// <remarks>
    /// The same move <see cref="JoinList"/> makes for a leading comma, generalised to the operators
    /// that introduce an operand: <c>AND</c>, <c>OR</c> and a join's <c>ON</c>. An own-line comment
    /// in front of one of those annotates the predicate that follows, and leaving it after the
    /// operator strands the operator on a line by itself:
    /// <code>
    ///     AND                             -- added for the 2021 audit
    ///     -- added for the 2021 audit     AND o.Total > 0
    ///     o.Total > 0
    /// </code>
    /// Neither form loses the comment and both are fixed points, so no safety gate objects — this
    /// is the comment-<em>position</em> blind spot, and it is only visible by reading the output.
    /// Found by diffing against the mssql extension's formatter, which places these correctly.
    /// <para>Only comments the author wrote <em>before</em> the operator are hoisted. A comment
    /// written after it — <c>AND</c>, newline, <c>/* not a named instance */</c>, newline, the
    /// operand — stays after it, because moving a comment to the far side of an operator is the very
    /// defect this fixes, and it is no better in that direction. So the rule is not "put comments
    /// before the operator" but "keep each comment on the side of the operator it was written on".
    /// </para>
    /// <para>Marking the node hoisted makes <see cref="Print"/> skip the comments it would otherwise
    /// emit again, and <see cref="SeparatorBefore"/> stop breaking after the operator. Both read the
    /// mark before <see cref="Print"/> consumes it, so this must be called before either.</para>
    /// </remarks>
    private Doc? HoistLeadingBefore(TSqlFragment node, int operatorFrom, int operatorTo)
    {
        var leading = _comments.Leading(node);
        if (leading.Count == 0 || !leading[0].AloneOnLine)
        {
            return null;
        }

        var operatorIndex = FirstCodeToken(operatorFrom, operatorTo);

        // All of them or none: a group split across the operator would have to be emitted in two
        // places, and the ordering that keeps both halves on their original side is not worth the
        // machinery for a case no corpus file exhibits.
        if (operatorIndex < 0 || leading.Any(comment => comment.TokenIndex > operatorIndex))
        {
            return null;
        }

        foreach (var comment in leading)
        {
            _hoistedComments.Add(comment);
        }

        return CommentDocs.AllLeading(leading);
    }

    /// <summary>
    /// Leading comments of a batch's first statement — or of the batch itself — that were written
    /// before <paramref name="fromIndex"/>..<paramref name="toIndex"/>.
    /// </summary>
    /// <remarks>
    /// Both owners are asked because which one the attacher picked depends on whether the batch has
    /// a statement for the comment to lead. An empty batch — everything before the first <c>GO</c>
    /// of a file that opens with one — has none, so the comment lands on the following batch.
    /// </remarks>
    private Doc? HoistBatchLead(TSqlBatch batch, int fromIndex, int toIndex)
    {
        if (batch.Statements.Count > 0
            && HoistLeadingBefore(batch.Statements[0], fromIndex, toIndex) is { } fromStatement)
        {
            return fromStatement;
        }

        return HoistLeadingBefore(batch, fromIndex, toIndex);
    }

    /// <summary>Whichever of a batch or its first statement the attacher gave leading comments to.</summary>
    private TSqlFragment CommentOwnerOf(TSqlBatch batch) =>
        batch.Statements.Count > 0 && _comments.Leading(batch.Statements[0]).Count > 0
            ? batch.Statements[0]
            : batch;

    /// <summary>The node's leading comment sitting at <paramref name="tokenIndex"/>, or null.</summary>
    private Comment? LeadingCommentAt(TSqlFragment node, int tokenIndex)
    {
        foreach (var comment in _comments.Leading(node))
        {
            if (comment.TokenIndex == tokenIndex)
            {
                return comment;
            }
        }

        return null;
    }

    /// <summary>A node's leading comments, less any already emitted ahead of it.</summary>
    private IReadOnlyList<Comment> RemainingLeading(TSqlFragment node) => Remaining(_comments.Leading(node));

    /// <summary>
    /// <paramref name="comments"/> less any a handler has already emitted elsewhere.
    /// </summary>
    /// <remarks>
    /// Applied to trailing comments as well as leading ones, because a handler that relocates a
    /// comment does not get to choose how the attacher classified it: the comment inside an empty
    /// <c>CATCH</c> attaches as <em>trailing</em> to the try block, and emitting it in the catch
    /// block without suppressing it there would print it twice.
    /// </remarks>
    private IReadOnlyList<Comment> Remaining(IReadOnlyList<Comment> comments)
    {
        if (comments.Count == 0 || _hoistedComments.Count == 0)
        {
            return comments;
        }

        List<Comment>? remaining = null;
        for (var i = 0; i < comments.Count; i++)
        {
            if (_hoistedComments.Contains(comments[i]))
            {
                remaining ??= [.. comments.Take(i)];
                continue;
            }

            remaining?.Add(comments[i]);
        }

        return remaining ?? comments;
    }

    /// <summary>Records that a handler has emitted this comment, so no node emits it again.</summary>
    private void MarkEmitted(Comment comment) => _hoistedComments.Add(comment);

    /// <summary>Comments whose tokens fall inside a range, in source order.</summary>
    private List<Comment> CommentsIn(int fromIndex, int toIndex)
    {
        var found = new List<Comment>();
        foreach (var comment in _comments.All)
        {
            if (comment.TokenIndex >= fromIndex && comment.TokenIndex <= toIndex)
            {
                found.Add(comment);
            }
        }

        return found;
    }

    /// <summary>Index of the first token in a range that is neither whitespace nor a comment, or -1.</summary>
    private int FirstCodeToken(int fromIndex, int toIndex)
    {
        for (var i = Math.Max(0, fromIndex); i <= Math.Min(toIndex, _tokens.Count - 1); i++)
        {
            if (!_tokens[i].IsTrivia())
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Index of the last token in a range that is neither whitespace nor a comment, or -1.</summary>
    private int LastCodeToken(int fromIndex, int toIndex)
    {
        for (var i = Math.Min(toIndex, _tokens.Count - 1); i >= Math.Max(0, fromIndex); i--)
        {
            if (!_tokens[i].IsTrivia())
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether the range holds an end-of-line comment — the kind
    /// <see cref="CommentDocs.Trailing"/> defers to the end of its line as a line suffix.
    /// </summary>
    /// <remarks>
    /// The other half of the operator problem, and the half no amount of hoisting reaches. A comment
    /// written <em>after</em> the left operand on the same line attaches to it as trailing, and a
    /// trailing end-of-line comment is emitted as a deferred suffix — so an operator appended to
    /// that same line is printed <em>before</em> the comment that was written before it:
    /// <code>
    ///     END /*End non-columnstore case */     END + /*End non-columnstore case */
    ///     + CASE …                              CASE …
    /// </code>
    /// A <see cref="Doc.LineSuffixBoundary"/> in front of the operator flushes the comment first.
    /// Restricted to comments that end their line, because those are exactly the ones deferred, and
    /// the boundary is a no-op for any other kind — which would leave the operator's spacing to
    /// chance.
    /// </remarks>
    private bool DeferredCommentIn(int fromIndex, int toIndex)
    {
        foreach (var comment in _comments.All)
        {
            if (comment.TokenIndex >= fromIndex
                && comment.TokenIndex <= toIndex
                && comment.EndsLine
                && !comment.AloneOnLine)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Wraps a hand-built doc with the comments attached to <paramref name="node"/>.
    /// </summary>
    /// <remarks>
    /// For the handlers that cannot route a node through <see cref="Print"/> because they need
    /// custom layout of its children — <c>CREATE TABLE</c> interleaves four child lists back into
    /// source order, so it prints the children itself and never prints the <c>TableDefinition</c>.
    /// That intermediate node can still carry comments: a <c>-- Column level tests</c> line between
    /// the opening parenthesis and the first column attaches to it, and bypassing the dispatcher
    /// dropped it. This is the same mistake as calling <c>PrintStatements</c> instead of
    /// <c>Print</c> on a <c>StatementList</c> — the third instance of it — so it now has a name.
    /// </remarks>
    private Doc WithComments(TSqlFragment node, Doc body)
    {
        var leading = _comments.Leading(node);
        IReadOnlyList<Comment> trailing = _comments.Trailing(node);
        var dangling = _comments.Dangling(node);

        if (leading.Count == 0 && trailing.Count == 0 && dangling.Count == 0)
        {
            return body;
        }

        return Doc.Concat(
            CommentDocs.AllLeading(leading),
            dangling.Count > 0 ? Doc.Concat(CommentDocs.Dangling(dangling), Doc.HardLine) : Doc.Empty,
            body,
            CommentDocs.AllTrailing(trailing));
    }

    /// <summary>Comma placement, per the <see cref="FormatOptions.LeadingCommas"/> option.</summary>
    private Doc ListSeparator() => _options.LeadingCommas
        ? Doc.Concat(Doc.SoftLine, Doc.Text(", "))
        : Doc.Concat(Doc.Text(","), Doc.Line);

    /// <summary>
    /// Joins the items of a list with the configured comma style.
    /// </summary>
    /// <remarks>
    /// Leading commas exist so that a line can be commented out, and that only reads properly if the
    /// items line up: every line but the first begins with the separator, so the first is padded by
    /// the separator's width and the names sit in one column with the commas hanging to their left.
    /// Without the pad the first item was two columns left of every other, which is the one thing a
    /// comma-first list is meant to avoid.
    /// <para>
    /// The pad is inside an <c>IfBreak</c> because a list that fits stays on one line, where a
    /// leading pad would be two stray spaces after <c>SELECT</c>.
    /// </para>
    /// <para>
    /// The commas sit at the list's own indent rather than hanging into the gutter to its left.
    /// Outdenting would need the layout engine to subtract from the current indentation, and under
    /// <c>useTabs</c> there is nothing coherent to subtract — a tab is one character of unknown
    /// width, so removing part of one is not a thing that can be done.
    /// </para>
    /// <para>
    /// Takes nodes rather than printed docs because of the comments: an item's own-line leading
    /// comment has to be emitted <em>before</em> the separator, which cannot be done once the item
    /// has already been printed. See the loop below.
    /// </para>
    /// </remarks>
    private Doc JoinList(IEnumerable<TSqlFragment> nodes)
    {
        var items = nodes as IReadOnlyList<TSqlFragment> ?? [.. nodes];

        if (!_options.LeadingCommas)
        {
            return Doc.Join(ListSeparator(), items.Select(Print));
        }

        if (items.Count == 0)
        {
            return Doc.Empty;
        }

        var parts = new List<Doc> { Doc.IfBreak(Doc.Text("  ")), Doc.Align(2, Print(items[0])) };

        for (var i = 1; i < items.Count; i++)
        {
            var node = items[i];
            var leading = _comments.Leading(node);
            parts.Add(Doc.SoftLine);

            // A comment that had its own line is emitted in front of the comma, at the items' own
            // column, rather than after it.
            //
            // Otherwise the comma lands to its left — `, -- note` — and on the next pass that
            // comment has code before it on the line, so it reclassifies as end-of-line, attaches to
            // the *previous* item and moves up. The file never reaches a fixed point, and `--check`
            // reports it forever. Twelve corpus files did exactly this. Same reasoning as
            // SeparatorBefore, which solves the identical problem for a separator in front of a
            // node; this is the list-separator half of it.
            if (leading.Count > 0 && leading[0].AloneOnLine)
            {
                foreach (var comment in leading)
                {
                    _hoistedComments.Add(comment);
                }

                // The pad puts the comment in the items' column; the hard line that AllLeading ends
                // it with then returns to the list's own indent, which is where the comma goes. No
                // Align around it, or that hard line would indent too and carry the comma with it.
                parts.Add(Doc.Text("  "));
                parts.Add(CommentDocs.AllLeading(leading));
            }

            parts.Add(Doc.Text(", "));
            parts.Add(Doc.Align(2, Print(node)));
        }

        return Doc.Concat(parts);
    }
}
