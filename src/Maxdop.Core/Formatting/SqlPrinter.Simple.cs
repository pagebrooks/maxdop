using Maxdop.Core.Printing;
using Maxdop.Core.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Formatting;

/// <summary>
/// The small statements: session settings, transactions, cursor control, flow control, and the
/// single-object DDL.
/// </summary>
/// <remarks>
/// Individually trivial, collectively the widest-spread gap left in the corpus — `SET NOCOUNT ON`
/// alone appeared in 31 of 36 real-world files. None of them needs a bespoke handler; they are all
/// either a bare keyword run or a keyword run with one or two children, which
/// <see cref="PrintPartsInTokenOrder"/> already covers.
/// <para>The leverage here is in ScriptDom's <em>base</em> classes rather than its leaf types. One
/// entry for <c>SetOnOffStatement</c> covers <c>SET NOCOUNT</c>, <c>SET ANSI_NULLS</c>,
/// <c>SET STATISTICS IO</c> and the rest; one for <c>TransactionStatement</c> covers
/// <c>BEGIN</c>/<c>COMMIT</c>/<c>ROLLBACK</c>/<c>SAVE</c>; one for <c>CursorStatement</c> covers
/// <c>OPEN</c>/<c>CLOSE</c>/<c>DEALLOCATE</c>; and one for <c>DropObjectsStatement</c> covers every
/// <c>DROP</c> of a named object. Dispatching on the leaves instead would have meant twenty entries
/// and twenty chances to miss one.</para>
/// </remarks>
public sealed partial class SqlPrinter
{
    /// <summary>
    /// A statement with no child nodes worth descending into — its options are enums and flags, so
    /// the whole thing is one keyword run.
    /// </summary>
    /// <remarks>
    /// <c>SET NOCOUNT ON</c>, <c>BREAK</c>, <c>CONTINUE</c>, <c>SET TRANSACTION ISOLATION LEVEL READ
    /// UNCOMMITTED</c>. Normalising their spacing and keyword case is the entire job.
    /// </remarks>
    /// <remarks>
    /// The slices here are pure keywords, so the non-reserved words in them are recased —
    /// <c>set nocount on</c> becomes <c>SET NOCOUNT ON</c>. Which statements may use this is a
    /// judgement per type, not a property of "looks like all keywords": <c>SetIdentityInsertStatement</c>
    /// derives from <c>SetOnOffStatement</c> and carries a table name, and <c>LabelStatement</c> holds
    /// its label in a plain string with no AST node at all. Both are routed elsewhere.
    /// </remarks>
    private Doc PrintKeywordStatement(TSqlStatement statement) => PrintKeywordParts(statement);

    /// <summary>
    /// A statement whose scaffolding may contain a name, so its identifiers are left as written.
    /// </summary>
    /// <remarks>
    /// <c>LabelStatement</c> is the reason this exists. Its label is a <c>string</c> property rather
    /// than a node, so nothing in the AST marks those tokens as a name — and recasing them would
    /// rename the label while leaving every <c>GOTO</c> that targets it untouched, which is broken
    /// code rather than a cosmetic change.
    /// </remarks>
    private Doc PrintNameBearingStatement(TSqlStatement statement) => PrintPartsInTokenOrder(statement);

    /// <summary><c>SET IDENTITY_INSERT dbo.t ON</c></summary>
    /// <remarks>
    /// Split out from its <c>SetOnOffStatement</c> siblings because it is the one that carries an object
    /// name. Passing the table as a part prints it through the dispatcher, which both formats it and
    /// leaves the surrounding slices — <c>SET IDENTITY_INSERT</c> and <c>ON</c> — safely pure.
    /// </remarks>
    private Doc PrintSetIdentityInsert(SetIdentityInsertStatement statement) =>
        PrintKeywordParts(statement, statement.Table);

    /// <summary><c>RETURN</c>, <c>RETURN 0</c>, <c>RETURN @rc</c></summary>
    private Doc PrintReturn(ReturnStatement statement) =>
        PrintKeywordParts(statement, statement.Expression);

    /// <summary><c>THROW</c>, <c>THROW 50000, 'message', 1</c></summary>
    private Doc PrintThrow(ThrowStatement statement) =>
        PrintKeywordParts(statement, statement.ErrorNumber, statement.Message, statement.State);

    /// <summary><c>WAITFOR DELAY '00:00:05'</c>, <c>WAITFOR (RECEIVE …), TIMEOUT 1000</c></summary>
    private Doc PrintWaitFor(WaitForStatement statement) =>
        PrintKeywordParts(statement, statement.Parameter, statement.Statement, statement.Timeout);

    /// <summary><c>GOTO ErrorHandler</c></summary>
    // The label itself is an Identifier *node* and so is printed as a child, never recased — only
    // the `GOTO` keyword in front of it is claimed.
    private Doc PrintGoTo(GoToStatement statement) =>
        PrintKeywordParts(statement, statement.LabelName);

    /// <summary><c>BEGIN TRANSACTION</c>, <c>COMMIT TRAN</c>, <c>ROLLBACK TRANSACTION @name</c></summary>
    /// <remarks>
    /// One entry for the whole family via the abstract base, so no spelling of a transaction statement
    /// can be missed. The keyword run is read rather than reconstructed, which keeps <c>TRAN</c> from
    /// being silently expanded to <c>TRANSACTION</c>.
    /// </remarks>
    private Doc PrintTransaction(TransactionStatement statement) =>
        PrintKeywordParts(statement, statement.Name);

    /// <summary><c>OPEN c</c>, <c>CLOSE c</c>, <c>DEALLOCATE c</c></summary>
    private Doc PrintCursorStatement(CursorStatement statement) =>
        PrintKeywordParts(statement, statement.Cursor);

    /// <summary><c>DROP TABLE #a, #b</c>, <c>DROP TABLE IF EXISTS dbo.t</c>, <c>DROP SYNONYM s</c></summary>
    /// <remarks>
    /// The base type covers every <c>DROP</c> of a named object, and <c>IF EXISTS</c> is a bool with no
    /// node — so it can only arrive through the keyword slice in front of the first object, which is
    /// exactly what the parts helper emits.
    /// </remarks>
    private Doc PrintDropObjects(DropObjectsStatement statement) =>
        SeparatedBy(statement.Objects)
            ? PrintKeywordParts(statement, [.. statement.Objects])
            : Passthrough(statement);

    /// <summary><c>TRUNCATE TABLE dbo.t</c></summary>
    private Doc PrintTruncateTable(TruncateTableStatement statement) =>
        statement.PartitionRanges.Count > 0
            ? Passthrough(statement)
            : PrintKeywordParts(statement, statement.TableName);

    /// <summary><c>UPDATE STATISTICS dbo.t ix WITH FULLSCAN</c></summary>
    private Doc PrintUpdateStatistics(UpdateStatisticsStatement statement) =>
        PrintKeywordParts(
            statement,
            [statement.SchemaObjectName, .. statement.SubElements, .. statement.StatisticsOptions]);

    /// <summary><c>ALTER TABLE dbo.t ADD c INT NULL</c></summary>
    /// <remarks>
    /// Reuses <see cref="PrintTableDefinition"/>, so a column added later is laid out the same way as
    /// one declared in the original <c>CREATE TABLE</c>. Grouped, so adding a single column stays on
    /// one line while a list of several breaks one per line under the statement.
    /// </remarks>
    private Doc PrintAlterTableAddTableElement(AlterTableAddTableElementStatement statement)
    {
        var name = statement.SchemaObjectName;
        var definition = statement.Definition;

        if (name is null || definition is null || definition.FirstTokenIndex < 0)
        {
            return Passthrough(statement);
        }

        var end = RangeEndBeforeTerminators(statement);
        if (SignificantTextBetween(definition.LastTokenIndex + 1, end).Length > 0)
        {
            return Passthrough(statement);
        }

        return Doc.Group(Doc.Concat(
            CasedTokens(statement.FirstTokenIndex, name.FirstTokenIndex - 1),
            Doc.Text(" "),
            Print(name),
            Doc.Text(" "),

            // `ADD`, and for a constraint list the opening parenthesis too.
            Keywords(name.LastTokenIndex + 1, EffectiveFirstToken(definition) - 1),
            Doc.Indent(Doc.Concat(Doc.Line, Print(definition)))));
    }

    // --- cursors ----------------------------------------------------------------------

    /// <summary><c>DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT …</c></summary>
    private Doc PrintDeclareCursor(DeclareCursorStatement statement) =>
        PrintKeywordParts(statement, statement.Name, statement.CursorDefinition);

    /// <summary>The <c>[options] FOR &lt;select&gt;</c> half of a cursor declaration.</summary>
    /// <remarks>
    /// The options and the <c>FOR</c> are read as one slice rather than reassembled: <c>CursorOption</c>
    /// covers a dozen spellings (<c>LOCAL</c>, <c>FORWARD_ONLY</c>, <c>STATIC</c>, <c>READ_ONLY</c>,
    /// <c>TYPE_WARNING</c>…) and the <c>FOR</c> belongs to no node at all. The query then starts on its
    /// own line, which is how cursor declarations are conventionally written and keeps a long query
    /// from being pushed off the right margin by the options in front of it.
    /// </remarks>
    private Doc PrintCursorDefinition(CursorDefinition definition)
    {
        var select = definition.Select;
        if (select is null
            || !NoCommentsIn(definition.FirstTokenIndex, select.FirstTokenIndex - 1)
            || SignificantTextBetween(select.LastTokenIndex + 1, definition.LastTokenIndex).Length > 0)
        {
            return Passthrough(definition);
        }

        return Doc.Concat(
            // `LOCAL FAST_FORWARD FOR` — cursor options are enums and the FOR belongs to no node, so
            // nothing here can be a name.
            Keywords(definition.FirstTokenIndex, select.FirstTokenIndex - 1),
            Doc.HardLine,
            Print(select));
    }

    /// <summary><c>FETCH NEXT FROM c INTO @a, @b</c></summary>
    /// <remarks>
    /// Needs its own entry ahead of <c>CursorStatement</c>, which it derives from: the base handler
    /// knows only about the cursor name, so the <c>INTO</c> variables would have come out as an
    /// unformatted tail slice rather than as printed children.
    /// </remarks>
    private Doc PrintFetchCursor(FetchCursorStatement statement) =>
        SeparatedBy(statement.IntoVariables)
            ? PrintKeywordParts(statement, [statement.Cursor, .. statement.IntoVariables])
            : Passthrough(statement);

    // --- XML --------------------------------------------------------------------------

    /// <summary><c>FOR XML PATH('row'), ROOT('rows')</c></summary>
    /// <remarks>
    /// A <c>ForClause</c>'s range can begin anywhere from its own <c>FOR</c> to past <c>FOR XML</c>,
    /// which is why the parts helper slices from <see cref="EffectiveFirstToken"/> — from the raw index
    /// the <c>FOR XML</c> would simply disappear.
    /// </remarks>
    private Doc PrintXmlForClause(XmlForClause clause) =>
        SeparatedBy(clause.Options)
            ? PrintKeywordParts(clause, [.. clause.Options])
            : Passthrough(clause);

    /// <summary>The declarations inside <c>WITH XMLNAMESPACES(…)</c>.</summary>
    private Doc PrintXmlNamespaces(XmlNamespaces namespaces) =>
        SeparatedBy(namespaces.XmlNamespacesElements)
            ? PrintKeywordParts(namespaces, [.. namespaces.XmlNamespacesElements])
            : Passthrough(namespaces);

    // --- constraints and indexes ------------------------------------------------------

    /// <summary><c>PRIMARY KEY CLUSTERED (a ASC)</c>, <c>UNIQUE NONCLUSTERED (a, b)</c></summary>
    /// <remarks>
    /// Whether it is a primary key, whether it is clustered, and the index type are all enums and bools
    /// with no token range, so they can only come from the keyword run in front of the column list —
    /// which is exactly what the parts helper emits. Trailing <c>WITH (…)</c> options and
    /// <c>ON [filegroup]</c> arrive through the tail slice the same way.
    /// </remarks>
    // Whether it is a primary key, whether it is clustered and the index type are enums, and the
    // constraint name, columns, filegroup and options are all printed as children — so every slice
    // here is keywords.
    private Doc PrintUniqueConstraint(UniqueConstraintDefinition constraint) =>
        SeparatedBy(constraint.Columns)
            ? PrintKeywordParts(
                constraint,
                [constraint.ConstraintIdentifier, .. constraint.Columns, constraint.OnFileGroupOrPartitionScheme, constraint.FileStreamOn, .. constraint.IndexOptions])
            : Passthrough(constraint);

    /// <summary><c>CONSTRAINT DF_x DEFAULT (0)</c>, <c>DEFAULT (0) FOR col</c></summary>
    private Doc PrintDefaultConstraint(DefaultConstraintDefinition constraint) => PrintKeywordParts(
        constraint,
        constraint.ConstraintIdentifier,
        constraint.Expression,
        constraint.Column);

    /// <summary><c>IDENTITY(1, 1)</c>, <c>IDENTITY(1, 1) NOT FOR REPLICATION</c></summary>
    private Doc PrintIdentityOptions(IdentityOptions options) =>
        PrintKeywordParts(options, options.IdentitySeed, options.IdentityIncrement);

    /// <summary><c>INDEX ix NONCLUSTERED (a, b) INCLUDE (c) WHERE d = 1 WITH (…)</c></summary>
    private Doc PrintIndexDefinition(IndexDefinition definition) =>
        SeparatedBy(definition.Columns) && SeparatedBy(definition.IncludeColumns)
            ? PrintKeywordParts(
                definition,
                [definition.Name, .. definition.Columns, .. definition.IncludeColumns, definition.FilterPredicate, definition.OnFileGroupOrPartitionScheme, definition.FileStreamOn, .. definition.IndexOptions])
            : Passthrough(definition);

    /// <summary><c>PATH('row')</c>, <c>ROOT('rows')</c>, <c>TYPE</c>, <c>ELEMENTS</c></summary>
    /// <remarks>
    /// Some of these carry a literal and some are a bare keyword; the parts helper covers both, since
    /// with no child node it falls back to emitting the whole range as one cased slice.
    /// </remarks>
    private Doc PrintXmlForClauseOption(XmlForClauseOption option) =>
        PrintKeywordParts(option, option.Value);

    /// <summary><c>'http://…' AS p</c>, and the <c>DEFAULT 'http://…'</c> form.</summary>
    private Doc PrintXmlNamespacesElement(XmlNamespacesElement element) => PrintPartsInTokenOrder(
        element,
        element.String,
        element is XmlNamespacesAliasElement alias ? alias.Identifier : null);

    /// <summary><c>CREATE NONCLUSTERED INDEX ix ON dbo.t (a, b) INCLUDE (c) WHERE d = 1</c></summary>
    private Doc PrintCreateIndex(CreateIndexStatement statement) =>
        SeparatedBy(statement.Columns) && SeparatedBy(statement.IncludeColumns)
            ? PrintKeywordParts(
                statement,
                [statement.Name, statement.OnName, .. statement.Columns, .. statement.IncludeColumns, statement.FilterPredicate, statement.OnFileGroupOrPartitionScheme, statement.FileStreamOn, .. statement.IndexOptions])
            : Passthrough(statement);

    /// <summary><c>(VALUES (1, 2), (3, 4)) AS t (c1, c2)</c></summary>
    /// <remarks>
    /// A <c>VALUES</c> list standing in for a table. The opening <c>(VALUES</c> and the parenthesis that
    /// closes the whole construct belong to no node, and the rows are the same <c>RowValue</c> nodes an
    /// <c>INSERT … VALUES</c> uses, so they get the identical layout.
    /// </remarks>
    private Doc PrintInlineDerivedTable(InlineDerivedTable table)
    {
        if (table.RowValues.Count == 0 || !SeparatedBy(table.RowValues) || !SeparatedBy(table.Columns))
        {
            return Passthrough(table);
        }

        // The construct's closing parenthesis sits after the last row; nested parentheses cannot
        // confuse the search because each row's own are inside its range.
        var closeParen = FirstSignificantToken(table.RowValues[^1].LastTokenIndex + 1, table.LastTokenIndex);
        var headEnd = table.RowValues[0].FirstTokenIndex - 1;

        if (closeParen < 0
            || SignificantTextBetween(closeParen, closeParen) != ")"
            || !Compact(SignificantTextBetween(table.FirstTokenIndex, headEnd))
                .Equals("(VALUES", StringComparison.OrdinalIgnoreCase))
        {
            return Passthrough(table);
        }

        var rows = Doc.Join(Doc.Concat(Doc.Text(","), Doc.Line), table.RowValues.Select(Print));

        // The layout PrintValuesInsertSource gives the same RowValue nodes, which the remark above
        // has always claimed this shares and did not: one row stays on the VALUES line, several
        // break one per line and indent.
        //
        // Without the indent the rows broke to column zero, and the closing parenthesis stayed
        // welded to the last of them — so the last row's trailing comment had no line break to
        // flush at until after `) AS v (…)`, and slid out of the list to read as a note about the
        // alias. That is how a missing Indent surfaced as a moved comment; sp_Blitz has one.
        var body = table.RowValues.Count == 1
            ? Doc.Concat(Doc.Text(" "), rows, Doc.Text(")"))
            : Doc.Group(
                Doc.Concat(Doc.Indent(Doc.Concat(Doc.Line, rows)), Doc.Line, Doc.Text(")")),
                shouldBreak: true);

        var docs = new List<Doc>
        {
            Keywords(table.FirstTokenIndex, headEnd),
            body,
        };

        // `AS t`, then the optional column-rename list — dropping which would silently change every
        // name the surrounding query refers to.
        if (table.Alias is not null)
        {
            docs.Add(Doc.Text(" "));
            if (SignificantTextBetween(closeParen + 1, table.Alias.FirstTokenIndex - 1).Length > 0)
            {
                docs.Add(CasedTokens(closeParen + 1, table.Alias.FirstTokenIndex - 1));
                docs.Add(Doc.Text(" "));
            }

            docs.Add(Print(table.Alias));
        }

        var afterAlias = (table.Alias?.LastTokenIndex ?? closeParen) + 1;

        if (table.Columns.Count > 0)
        {
            if (SignificantTextBetween(afterAlias, table.Columns[0].FirstTokenIndex - 1) != "("
                || SignificantTextBetween(table.Columns[^1].LastTokenIndex + 1, table.LastTokenIndex) != ")")
            {
                return Passthrough(table);
            }

            docs.Add(Doc.Group(Doc.Concat(
                Doc.Text(" ("),
                Doc.Indent(Doc.Concat(Doc.SoftLine, JoinList(table.Columns))),
                Doc.SoftLine,
                Doc.Text(")"))));
        }
        else if (SignificantTextBetween(afterAlias, table.LastTokenIndex).Length > 0)
        {
            return Passthrough(table);
        }

        return Doc.Group(Doc.Concat(docs));
    }

    /// <summary><c>a</c>, <c>a ASC</c>, <c>a DESC</c> inside an index or constraint column list.</summary>
    /// <remarks>
    /// The sort order is an enum with no token range, so it arrives through the tail slice. Small, but
    /// there is one of these per indexed column, which makes it one of the widest-spread leaves left.
    /// </remarks>
    private Doc PrintColumnWithSortOrder(ColumnWithSortOrder column) =>
        PrintKeywordParts(column, column.Column);

    /// <summary>A cursor name, optionally <c>GLOBAL</c>.</summary>
    private Doc PrintCursorId(CursorId cursor) => PrintKeywordParts(cursor, cursor.Name);

    /// <summary><c>-x</c>, <c>+x</c>, <c>~x</c></summary>
    /// <remarks>
    /// The operator is read from the tokens, and whether a space follows it is taken from the source
    /// rather than chosen. That is not cosmetic: <c>- -1</c> printed without the space becomes
    /// <c>--1</c>, which is a line comment and would swallow the rest of the line. Copying the
    /// author's spacing cannot produce that, because <c>--</c> could not have been two operators in
    /// the input either.
    /// </remarks>
    private Doc PrintUnaryExpression(UnaryExpression expression)
    {
        var operand = expression.Expression;
        if (operand is null)
        {
            return Passthrough(expression);
        }

        var operatorEnd = EffectiveFirstToken(operand) - 1;
        if (SignificantTextBetween(expression.FirstTokenIndex, operatorEnd).Length == 0)
        {
            return Passthrough(expression);
        }

        var spaced = false;
        for (var i = expression.FirstTokenIndex; i <= operatorEnd && i < _tokens.Count; i++)
        {
            spaced |= _tokens[i].IsWhiteSpace();
        }

        return Doc.Concat(
            CasedTokens(expression.FirstTokenIndex, operatorEnd),
            spaced ? Doc.Text(" ") : Doc.Empty,
            Print(operand));
    }

    /// <summary><c>a BETWEEN b AND c</c>, <c>a NOT BETWEEN b AND c</c></summary>
    /// <remarks>
    /// Both keywords live in the gaps between the three operands, so reading them keeps the
    /// <c>NOT</c> without this handler needing to know it exists.
    /// </remarks>
    private Doc PrintBooleanTernary(BooleanTernaryExpression expression) => PrintKeywordParts(
        expression,
        expression.FirstExpression,
        expression.SecondExpression,
        expression.ThirdExpression);
}
