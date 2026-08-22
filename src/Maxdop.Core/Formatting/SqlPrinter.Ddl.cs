using Maxdop.Core.Printing;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Formatting;

/// <summary>
/// Declarations and table structure: <c>CREATE TABLE</c>, table definitions, column definitions,
/// <c>DECLARE</c> of variables and table variables, and procedure parameters.
/// </summary>
/// <remarks>
/// These belong together because they are grammatically the same thing at different scales — a name,
/// a type, and a run of optional modifiers — and they share one emission strategy,
/// <see cref="PrintPartsInTokenOrder"/>. <c>ProcedureParameter</c> is literally a
/// <c>DeclareVariableElement</c> subclass, so one handler covers both.
/// <para>Structure is formatted (one element per line, consistent parentheses and indentation) while
/// each element goes through the normal dispatch.</para>
/// </remarks>
public sealed partial class SqlPrinter
{
    // --- column definitions -----------------------------------------------------------

    /// <summary>
    /// One column of a table definition: <c>b NVARCHAR(50) COLLATE X NULL CONSTRAINT DF_b DEFAULT
    /// (N'')</c>.
    /// </summary>
    /// <remarks>
    /// The widest-spread gap in the corpus — 5,666 definitions across 102 of 980 files, the largest
    /// remaining block of verbatim text once the DML statements were handled.
    /// <para>A column definition has fifteen optional properties (identity, collation, computed,
    /// masking, encryption, generated-always, storage, sparse, rowguidcol…) and several of them are
    /// <em>flags rather than nodes</em>: <c>PERSISTED</c>, <c>FILESTREAM</c> and <c>ROWGUIDCOL</c>
    /// appear in the token range with no AST node to print. Enumerating the properties would
    /// therefore drop them, and every future addition to the list besides.</para>
    /// <para>So this prints the parts that <em>are</em> nodes, in token order, and reads whatever
    /// sits between them straight from the token stream. Nothing can be dropped, because every token
    /// in the range is either inside a printed child or inside a gap slice — and unlike a single
    /// verbatim slice it descends into the parts that contain real expressions, which is where a
    /// <c>DEFAULT (…)</c> or a computed column actually needs formatting.</para>
    /// </remarks>
    private Doc PrintColumnDefinition(ColumnDefinition column) => PrintParts(
        column,
        pureKeywords: false,
        [
            column.ColumnIdentifier,
            column.DataType,
            column.IdentityOptions,
            column.Collation,
            column.ComputedColumnExpression,
            column.DefaultConstraint,
            column.Index,
            column.Encryption,
            column.StorageOptions,
            column.MaskingFunction,
            .. column.Constraints,
        ],
        GeneratedAlwaysKeywords(column));

    /// <summary>
    /// The token indices of a column's <c>GENERATED ALWAYS AS … START|END [HIDDEN]</c> run, which are
    /// keyword positions inside a region that is otherwise not.
    /// </summary>
    /// <remarks>
    /// <c>GENERATED</c>, <c>ALWAYS</c>, <c>ROW</c>, <c>START</c> and <c>HIDDEN</c> are all non-reserved,
    /// so they lex as identifiers and the column's gap slices left them exactly as written. That was
    /// invisible while temporal table definitions were emitted verbatim; once they formatted, a
    /// lower-case script produced <c>sysstart DATETIME2 generated always AS row start NOT NULL</c> —
    /// keyword casing applied to two words out of six in the same line.
    /// <para>The whole run is safe to recase because <b>every word in it is determined by the
    /// <c>GeneratedAlways</c> enum and the <c>IsHidden</c> flag</b>: the parser has already decided this
    /// is <c>RowStart</c> or <c>SuserSidStart</c>, so no part of it can be an object name. Recasing the
    /// column's gaps wholesale would not be safe — the same region carries
    /// <c>COLLATE &lt;collation&gt;</c>, <c>CONSTRAINT &lt;name&gt;</c> and
    /// <c>REFERENCES dbo.t (id)</c>.</para>
    /// <para>Anchored on the token after the data type, where the grammar puts this clause, and matched
    /// on the opening <c>GENERATED ALWAYS AS</c>. If that sequence is not there the set is empty and
    /// nothing is recased — so a constraint that happens to be named <c>generated</c> cannot be caught
    /// by it.</para>
    /// </remarks>
    private HashSet<int>? GeneratedAlwaysKeywords(ColumnDefinition column)
    {
        if (column.GeneratedAlways is null || column.DataType is null)
        {
            return null;
        }

        var from = FirstSignificantToken(column.DataType.LastTokenIndex + 1, column.LastTokenIndex);
        if (from < 0 || !MatchesWords(from, "GENERATED", "ALWAYS", "AS"))
        {
            return null;
        }

        // The run ends on the START or END that closes it — `ROW START`, `TRANSACTION_ID START`,
        // `SUSER_SID END`. Everything up to and including that word is grammar.
        var positions = new HashSet<int>();

        for (var i = from; i >= 0; i = FirstSignificantToken(i + 1, column.LastTokenIndex))
        {
            positions.Add(i);

            if (!IsWord(i, "START") && !IsWord(i, "END"))
            {
                continue;
            }

            // `HIDDEN` follows the run when the flag is set, and is the same kind of word.
            var hidden = FirstSignificantToken(i + 1, column.LastTokenIndex);
            if (column.IsHidden && hidden >= 0 && IsWord(hidden, "HIDDEN"))
            {
                positions.Add(hidden);
            }

            return positions;
        }

        // No terminating START or END, so this is not the shape described above.
        return null;
    }

    /// <summary>Whether the significant tokens starting at an index are exactly these words.</summary>
    private bool MatchesWords(int fromIndex, params string[] words)
    {
        var index = fromIndex;

        foreach (var word in words)
        {
            if (index < 0 || index >= _tokens.Count || !IsWord(index, word))
            {
                return false;
            }

            index = FirstSignificantToken(index + 1, _tokens.Count - 1);
        }

        return true;
    }

    /// <summary>Whether a token is the given word, whatever case it was written in.</summary>
    private bool IsWord(int index, string word) =>
        index >= 0
        && index < _tokens.Count
        && string.Equals(_tokens[index].Text, word, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A variable declaration or a procedure parameter: <c>@a INT</c>,
    /// <c>@b NVARCHAR(50) = N'x'</c>, <c>@c dbo.MyType READONLY</c>, <c>@d INT OUTPUT</c>.
    /// </summary>
    /// <remarks>
    /// One handler for both, because <c>ProcedureParameter</c> derives from
    /// <c>DeclareVariableElement</c> — and the two extras it adds, <c>Modifier</c> (<c>OUTPUT</c>) and
    /// <c>IsVarying</c>, are flags with no node, so they arrive through the tail slice exactly as
    /// <c>PERSISTED</c> does on a column. Between them these were 18% of the text still left verbatim
    /// in real-world scripts, across more files than any other pair.
    /// </remarks>
    private Doc PrintDeclareVariableElement(DeclareVariableElement element) => PrintKeywordParts(
        element,
        [element.VariableName, element.DataType, element.Nullable, element.Value]);

    /// <summary>
    /// Emits a construct as its child nodes in token order, reading everything between and after them
    /// straight from the token stream.
    /// </summary>
    /// <remarks>
    /// <para>The strategy for constructs whose grammar is "a name, a type, and a pile of optional
    /// modifiers". Two facts force it. First, the source order of those modifiers is the author's
    /// choice and several orderings are legal, so emitting them property by property would silently
    /// rewrite the declaration. Second — and this is the one that decides it — several options are
    /// <em>flags with no AST node at all</em>: <c>PERSISTED</c>, <c>FILESTREAM</c>, <c>ROWGUIDCOL</c>,
    /// <c>SPARSE</c>, <c>OUTPUT</c>, <c>READONLY</c>. Enumerating the properties drops every one of
    /// them, and every future addition to the list besides.</para>
    /// <para>Nothing can be lost, because every token in the range ends up either inside a printed
    /// child or inside a slice. And unlike emitting the whole construct verbatim, the printer still
    /// descends into the parts that hold real expressions — a <c>DEFAULT (…)</c>, a computed column,
    /// a parameter's default value.</para>
    /// </remarks>
    private Doc PrintPartsInTokenOrder(TSqlFragment node, params TSqlFragment?[] candidates) =>
        PrintParts(node, pureKeywords: false, candidates);

    /// <summary>
    /// As <see cref="PrintPartsInTokenOrder"/>, for constructs whose scaffolding cannot contain an
    /// object name — so the non-reserved words in it are recased as the keywords they are.
    /// </summary>
    /// <remarks>
    /// Callers must be able to say why no name can appear; see <see cref="Keywords"/> for the two
    /// traps that make "it looks like all keywords" an unsafe basis for the judgement.
    /// </remarks>
    private Doc PrintKeywordParts(TSqlFragment node, params TSqlFragment?[] candidates) =>
        PrintParts(node, pureKeywords: true, candidates);

    private Doc PrintParts(
        TSqlFragment node,
        bool pureKeywords,
        TSqlFragment?[] candidates,
        IReadOnlySet<int>? extraKeywordPositions = null,
        bool preserveCase = false)
    {
        var present = candidates
            .Where(p => p is not null && p.FirstTokenIndex >= 0)
            .Select(p => p!)
            .OrderBy(p => p.FirstTokenIndex)
            .ToList();

        // A column-level constraint appears both nested inside its ColumnDefinition and in the
        // table's constraint list, so a part strictly containing another is dropped — see
        // PrintCreateTable, where the same overlap made consecutive elements look unseparated.
        var parts = present.Where(p => !present.Any(other => Contains(other, p))).ToList();

        // A statement's range covers its terminator, which the dispatcher emits separately; slicing
        // to LastTokenIndex would produce `;;`.
        var end = node is TSqlStatement statement ? RangeEndBeforeTerminators(statement) : node.LastTokenIndex;

        // No child nodes at all, which is most of ScriptDom's small statements: `SET NOCOUNT ON`,
        // `BREAK`, `COMMIT TRANSACTION`, `SET STATISTICS IO ON`. Their options are enums and bools, so
        // there is nothing to descend into and the whole construct is one keyword run. Emitting it
        // still normalises spacing and keyword case, which is the entire job for these.
        if (parts.Count == 0)
        {
            return NoCommentsIn(EffectiveFirstToken(node), end)
                ? Slice(EffectiveFirstToken(node), end, pureKeywords, extraKeywordPositions, preserveCase)
                : Passthrough(node);
        }

        var docs = new List<Doc>();

        // Whatever introduces the construct, when that is not the first child: `DECLARE`, `RETURN`,
        // `DROP TABLE`, `THROW`, `PRIMARY KEY CLUSTERED (`.
        //
        // From EffectiveFirstToken, not FirstTokenIndex: several node ranges begin *after* the keyword
        // that introduces them — a `ForClause` can start past `FOR XML` — and slicing from the raw
        // index would drop it.
        var start = EffectiveFirstToken(node);
        var head = SignificantTextBetween(start, EffectiveFirstToken(parts[0]) - 1);

        if (head.Length > 0)
        {
            docs.Add(Slice(start, EffectiveFirstToken(parts[0]) - 1, pureKeywords, extraKeywordPositions, preserveCase));
            docs.Add(NoSpaceAfter(head) ? Doc.Empty : Doc.Text(" "));
        }

        docs.Add(Print(parts[0]));

        for (var i = 1; i < parts.Count; i++)
        {
            // A `--` comment trailing the previous part is deferred through LineSuffix, so anything
            // appended to that line lands *inside* it. `ALTER TABLE t1 DROP cs3, -- a` … with four such
            // comments emitted them all onto one line, where the first swallowed the other three: five
            // comments in, two out. The boundary forces a break when — and only when — a line suffix is
            // pending, so it costs nothing in the ordinary case.
            docs.Add(Doc.LineSuffixBoundary);

            // The gap carries whatever separates the parts — `COLLATE`, `CONSTRAINT`, `=`, `BETWEEN`,
            // `AND`, `MASKED WITH (FUNCTION =`, or a bare comma. Cased per token so a constraint name
            // or collation keeps the casing that identifies it.
            var gap = SignificantTextBetween(parts[i - 1].LastTokenIndex + 1, EffectiveFirstToken(parts[i]) - 1);

            if (gap.Length > 0)
            {
                docs.Add(NoSpaceBefore(gap) ? Doc.Empty : Doc.Text(" "));
                docs.Add(Slice(
                    parts[i - 1].LastTokenIndex + 1,
                    EffectiveFirstToken(parts[i]) - 1,
                    pureKeywords,
                    extraKeywordPositions,
                    preserveCase));
                docs.Add(NoSpaceAfter(gap) ? Doc.Empty : Doc.Text(" "));
            }
            else
            {
                docs.Add(Doc.Text(" "));
            }

            docs.Add(Print(parts[i]));
        }

        // The tail is the flags with no node of their own, plus any closing parenthesis a head or gap
        // slice opened: `PERSISTED`, `NOT FOR REPLICATION`, `SPARSE`, `OUTPUT`, `READONLY`, `)`.
        var tail = SignificantTextBetween(parts[^1].LastTokenIndex + 1, end);
        if (tail.Length > 0)
        {
            docs.Add(Doc.LineSuffixBoundary);
            docs.Add(NoSpaceBefore(tail) ? Doc.Empty : Doc.Text(" "));
            docs.Add(Slice(parts[^1].LastTokenIndex + 1, end, pureKeywords, extraKeywordPositions, preserveCase));
        }

        return Doc.Concat(docs);
    }

    // --- DECLARE ----------------------------------------------------------------------

    /// <summary>
    /// <c>DECLARE @a INT, @b NVARCHAR(50) = N'x';</c>
    /// </summary>
    /// <remarks>
    /// Laid out like an <c>UPDATE</c>'s <c>SET</c> list — keyword, then an indented one-per-line list
    /// when it does not fit — so a multi-variable declaration reads the same way every other list in
    /// the formatter does.
    /// </remarks>
    private Doc PrintDeclareVariable(DeclareVariableStatement statement)
    {
        var declarations = statement.Declarations;
        if (declarations.Count == 0
            || !SeparatedBy(declarations)
            || !SignificantTextBetween(statement.FirstTokenIndex, declarations[0].FirstTokenIndex - 1)
                .Equals("DECLARE", StringComparison.OrdinalIgnoreCase)
            || !NothingAfter(declarations[^1], statement))
        {
            return Passthrough(statement);
        }

        return Doc.Group(Doc.Concat(
            Keyword("DECLARE"),
            Doc.Indent(Doc.Concat(Doc.Line, JoinList(declarations)))));
    }

    /// <summary><c>DECLARE @t TABLE (…);</c></summary>
    private Doc PrintDeclareTableVariable(DeclareTableVariableStatement statement)
    {
        var body = statement.Body;
        if (body is null
            || !SignificantTextBetween(statement.FirstTokenIndex, body.FirstTokenIndex - 1)
                .Equals("DECLARE", StringComparison.OrdinalIgnoreCase)
            || !NothingAfter(body, statement))
        {
            return Passthrough(statement);
        }

        return Doc.Concat(Keyword("DECLARE"), Doc.Text(" "), Print(body));
    }

    /// <summary><c>@t TABLE (a INT NOT NULL, b NVARCHAR(10))</c></summary>
    /// <remarks>
    /// Shares its table layout with <c>CREATE TABLE</c> through <see cref="PrintTableDefinition"/>,
    /// which is the point of that node having its own handler: a table variable and a real table
    /// should not be formatted differently just because ScriptDom reaches them by different routes.
    /// <para>The <c>TABLE (</c> between the name and the definition, and the closing parenthesis after
    /// it, belong to no node and are read from the gaps.</para>
    /// </remarks>
    private Doc PrintDeclareTableVariableBody(DeclareTableVariableBody body)
    {
        var definition = body.Definition;
        if (body.VariableName is null
            || definition is null
            || !Compact(SignificantTextBetween(body.VariableName.LastTokenIndex + 1, definition.FirstTokenIndex - 1))
                .Equals("TABLE(", StringComparison.OrdinalIgnoreCase)
            || SignificantTextBetween(definition.LastTokenIndex + 1, body.LastTokenIndex) != ")")
        {
            return Passthrough(body);
        }

        return Doc.Concat(
            Print(body.VariableName),
            Doc.Text(" "),
            CasedTokensBetween(body.VariableName, definition),
            Doc.Indent(Doc.Concat(Doc.HardLine, Print(definition))),
            Doc.HardLine,
            Doc.Text(")"));
    }

    // --- table definitions ------------------------------------------------------------

    /// <summary>
    /// The inside of a table's parentheses: columns, constraints, indexes and the system-time period,
    /// one element per line.
    /// </summary>
    /// <remarks>
    /// Always broken, whatever the width: a table definition with several columns on one line is
    /// unreadable, and one element per line keeps a schema change to a one-line diff.
    /// <para>Its own handler rather than inlined into <c>CREATE TABLE</c>, so <c>DECLARE @t TABLE</c>
    /// gets the identical layout, and so the node's own attached comments are emitted by the
    /// dispatcher instead of needing a <c>WithComments</c> at each call site.</para>
    /// </remarks>
    private Doc PrintTableDefinition(TableDefinition definition)
    {
        var elements = TableElements(definition);

        return elements.Count == 0 || !SeparatedBy(elements)
            ? Passthrough(definition)
            : Doc.Join(Doc.Concat(Doc.Text(","), Doc.HardLine), elements.Select(Print));
    }

    /// <summary>
    /// The index of a comma sitting between a table definition's last element and its closing
    /// parenthesis, or -1 when there is none.
    /// </summary>
    /// <remarks>
    /// <c>CREATE TABLE #t (a INT, b INT,)</c> parses, and First Responder Kit writes it — it was four
    /// of the five real-world <c>CREATE TABLE</c> statements the corpus still declined, because the
    /// tail guard requires the region after the last column to begin with <c>)</c> and this one begins
    /// with <c>,</c>.
    /// <para>The comma belongs to no node: a <c>TableDefinition</c>'s range ends at its last element.
    /// Only <c>CREATE TABLE</c> can have one — the parser rejects it in both other constructs that
    /// share this table layout (<c>DECLARE @t TABLE (a INT,)</c> and a multi-statement function's
    /// <c>RETURNS @r TABLE (a INT,)</c> are syntax errors). Checked, not assumed: the first version of
    /// this change carried matching code in <c>PrintDeclareTableVariableBody</c> that no input could
    /// reach.</para>
    /// <para><b>Both uses live in one handler on purpose.</b> Skipping the comma in the tail and
    /// emitting it are two halves of one decision, and the first version of this change split them
    /// across <c>PrintCreateTable</c> and <see cref="PrintTableDefinition"/> — where they could
    /// disagree. They did: on a column list the definition handler declined, the tail skipped a comma
    /// that nothing then emitted, and <c>AlwaysEncryptedTests130.sql</c> lost a token. Emitting it here,
    /// straight after whatever <c>Print</c> returns for the definition, is correct whether that is a
    /// formatted list or a verbatim one.</para>
    /// <para>Requiring <c>)</c> immediately after is what makes this precise rather than a guess: if
    /// anything else follows the comma it is a separator for an element that
    /// <see cref="TableElements"/> failed to account for, and inventing a trailing comma there would
    /// move real syntax.</para>
    /// </remarks>
    private int TrailingCommaIndex(TableDefinition definition)
    {
        var comma = FirstSignificantToken(definition.LastTokenIndex + 1, _tokens.Count - 1);
        if (comma < 0 || _tokens[comma].TokenType != TSqlTokenType.Comma)
        {
            return -1;
        }

        var next = FirstSignificantToken(comma + 1, _tokens.Count - 1);

        return next >= 0 && _tokens[next].TokenType == TSqlTokenType.RightParenthesis ? comma : -1;
    }

    /// <summary>
    /// <c>PERIOD FOR SYSTEM_TIME (start_column, end_column)</c> — the temporal table's period
    /// declaration.
    /// </summary>
    /// <remarks>
    /// Worth its own handler only because of the range quirk described on
    /// <see cref="EffectiveFirstToken"/>: uncorrected, the four tokens naming the period fell into the
    /// gap between table elements, <c>SeparatedBy</c> saw <c>, PERIOD FOR SYSTEM_TIME (</c> instead of
    /// <c>,</c> and declined the <b>entire table definition</b> — which made this the single largest
    /// coverage gap in the corpus at 6,935 tokens across 11 files, all of it ordinary columns that had
    /// nothing wrong with them.
    /// <para>The two column names are Printed rather than sliced, so they keep their exact spelling;
    /// only the words around them are recased.</para>
    /// </remarks>
    private Doc PrintSystemTimePeriod(SystemTimePeriodDefinition period)
    {
        var start = period.StartTimeColumn;
        var end = period.EndTimeColumn;
        var first = EffectiveFirstToken(period);

        // An uncorrected start means the word sequence was not there, so this is a shape the handler
        // has not seen and the keyword slice below would be wrong.
        if (start is null || end is null || first == period.FirstTokenIndex)
        {
            return Passthrough(period);
        }

        return Doc.Concat(
            Keywords(first, EffectiveFirstToken(start) - 1),
            Print(start),
            CasedTokensBetween(start, end),
            Doc.Text(" "),
            Print(end),
            CasedTokens(end.LastTokenIndex + 1, period.LastTokenIndex));
    }

    /// <summary>
    /// A table's elements in token order, with anything nested inside another element removed.
    /// </summary>
    /// <remarks>
    /// Columns, constraints, indexes and the system-time period arrive in four separate ScriptDom
    /// lists but interleave freely in the source, so they are sorted back into token order —
    /// re-emitting them list by list would silently reorder a schema.
    /// <para>The containment filter is what makes inline constraints work: <c>a1 INT UNIQUE</c> puts
    /// its constraint node both inside the <c>ColumnDefinition</c> and in the table's constraint list,
    /// so leaving it in makes consecutive elements overlap instead of being comma-separated — and
    /// would emit it twice.</para>
    /// </remarks>
    private static List<TSqlFragment> TableElements(TableDefinition definition)
    {
        var candidates = definition.ColumnDefinitions.Select(c => (TSqlFragment)c)
            .Concat(definition.TableConstraints)
            .Concat(definition.Indexes)
            .Concat(definition.SystemTimePeriod is null ? [] : new TSqlFragment[] { definition.SystemTimePeriod })
            .Where(e => e.FirstTokenIndex >= 0)
            .OrderBy(e => e.FirstTokenIndex)
            .ToList();

        return [.. candidates.Where(e => !candidates.Any(other => Contains(other, e)))];
    }

    /// <summary><c>NULL</c> / <c>NOT NULL</c>.</summary>
    /// <remarks>
    /// Nothing to decide, but worth a handler all the same: without one it is a verbatim subtree root
    /// inside otherwise-formatted output, so <c>NOT     NULL</c> kept its spacing and the coverage
    /// figures counted 1,700 of them as unformatted work.
    /// </remarks>
    private Doc PrintNullableConstraint(NullableConstraintDefinition constraint)
    {
        var text = SignificantTextBetween(constraint.FirstTokenIndex, constraint.LastTokenIndex);

        // Sliced rather than rebuilt from the text this checked: SignificantTextBetween ignores
        // comments, so re-emitting `NOT NULL` from it dropped a comment written between the two
        // words. The text still decides whether the shape is one this models; the slice is what puts
        // it on the page, and the slice carries the comment.
        return text.Equals("NULL", StringComparison.OrdinalIgnoreCase)
            || text.Equals("NOT NULL", StringComparison.OrdinalIgnoreCase)
                ? Keywords(constraint.FirstTokenIndex, constraint.LastTokenIndex)
                : Passthrough(constraint);
    }

    /// <summary>
    /// Whether a slice of punctuation binds tightly to what precedes it, so no space goes in front.
    /// </summary>
    /// <remarks>
    /// The parts helper joins children with spaces, which is right for the keyword runs that separate
    /// most of them — <c>COLLATE</c>, <c>CONSTRAINT</c>, <c>BETWEEN</c> — and wrong for punctuation.
    /// Without these two rules the same helper produces <c>THROW 50000 , 'msg'</c>,
    /// <c>@x . modify ( N'…' )</c> and <c>PRIMARY KEY ( a)</c>. Stated once here rather than at each
    /// call site, because the list only grows as more constructs go through the helper.
    /// </remarks>
    private static bool NoSpaceBefore(string gap) =>
        gap.StartsWith(',') || gap.StartsWith('.') || gap.StartsWith('(') || gap.StartsWith(')')
        || gap.StartsWith("::", StringComparison.Ordinal);

    /// <summary>Whether a slice binds tightly to what follows it, so no space goes after.</summary>
    private static bool NoSpaceAfter(string gap) =>
        gap.EndsWith('(') || gap.EndsWith('.') || gap.EndsWith("::", StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="outer"/> strictly contains <paramref name="inner"/>.
    /// </summary>
    /// <remarks>
    /// Strict on purpose: two nodes with identical ranges would otherwise each "contain" the other
    /// and both be discarded.
    /// </remarks>
    private static bool Contains(TSqlFragment outer, TSqlFragment inner) =>
        outer.FirstTokenIndex <= inner.FirstTokenIndex
        && outer.LastTokenIndex >= inner.LastTokenIndex
        && (outer.FirstTokenIndex < inner.FirstTokenIndex || outer.LastTokenIndex > inner.LastTokenIndex);

    /// <summary>
    /// <c>CREATE TYPE dbo.MyTableType AS TABLE (a INT NOT NULL, b NVARCHAR(50))</c>.
    /// </summary>
    /// <remarks>
    /// Given a real handler rather than left to the generic fallback because table types are ordinary
    /// application code — a table-valued parameter needs one — and because the fallback emits the whole
    /// statement on one line, which reads badly the moment the definition breaks internally.
    /// <para>The layout is <c>CREATE TABLE</c>'s, through the same <see cref="PrintTableDefinition"/>, so
    /// a table type and the table it mirrors are formatted identically.</para>
    /// </remarks>
    private Doc PrintCreateTypeTable(CreateTypeTableStatement statement)
    {
        var name = statement.Name;
        var definition = statement.Definition;

        if (name is null || definition is null)
        {
            return Passthrough(statement);
        }

        var end = RangeEndBeforeTerminators(statement);
        var trailingComma = TrailingCommaIndex(definition);
        var tailFrom = trailingComma >= 0 ? trailingComma + 1 : definition.LastTokenIndex + 1;

        // `AS TABLE (` between the name and the definition, and the closing parenthesis after it, belong
        // to no node and are read from the gaps — the same shape as `DECLARE @t TABLE (…)`.
        if (!Compact(SignificantTextBetween(name.LastTokenIndex + 1, EffectiveFirstToken(definition) - 1))
                .Equals("ASTABLE(", StringComparison.OrdinalIgnoreCase)
            || SignificantTextBetween(tailFrom, end) != ")")
        {
            return Passthrough(statement);
        }

        return Doc.Concat(
            Keywords(statement.FirstTokenIndex, name.FirstTokenIndex - 1),
            Doc.Text(" "),
            Print(name),
            Doc.Text(" "),
            Keywords(name.LastTokenIndex + 1, EffectiveFirstToken(definition) - 1),
            Doc.Indent(Doc.Concat(
                Doc.HardLine,
                Print(definition),
                trailingComma >= 0 ? Doc.Text(",") : Doc.Empty)),
            Doc.HardLine,
            Doc.Text(")"));
    }

    // --- CREATE TABLE -----------------------------------------------------------------

    private Doc PrintCreateTable(CreateTableStatement statement)
    {
        var definition = statement.Definition;
        if (statement.SchemaObjectName is null
            || definition is null
            || statement.SelectStatement is not null   // CTAS has an entirely different shape
            || statement.CloneSource is not null
            || statement.FederationScheme is not null
            || statement.AsFileTable
            || statement.AsEdge
            || statement.AsNode)
        {
            return Passthrough(statement);
        }

        // The element list is only needed here to locate the table's parentheses; the layout inside
        // them belongs to PrintTableDefinition, which DECLARE @t TABLE shares.
        var elements = TableElements(definition);
        if (elements.Count == 0)
        {
            return Passthrough(statement);
        }

        var head = CasedTokens(statement.FirstTokenIndex, statement.SchemaObjectName.FirstTokenIndex - 1);

        // The gap before the first element must be exactly the opening parenthesis.
        if (SignificantTextBetween(
                statement.SchemaObjectName.LastTokenIndex + 1,
                elements[0].FirstTokenIndex - 1) != "(")
        {
            return Passthrough(statement);
        }

        // Everything after the last element: the closing parenthesis, plus any table-level clauses —
        // `WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = …))`, `ON [PRIMARY]`, `TEXTIMAGE_ON`,
        // `FILESTREAM_ON`. Emitted as one token slice, which is what the earlier version of this
        // handler declined to do; it deferred the whole statement instead, at a cost of 4% of all
        // declined text across 32 files.
        //
        // The reason it was declined was that a naive slice had silently dropped a temporal table's
        // entire `SYSTEM_VERSIONING` clause. The three checks below are what make the slice safe
        // rather than hopeful:
        //
        //  - the tail must *start* with the closing parenthesis, so the slice is the table's tail and
        //    not something the element list failed to cover;
        //  - every option node must end inside the range being sliced. `SystemVersioningTableOption`
        //    stops at the history table name, two closing parentheses short of its own clause, so the
        //    slice has to run to the statement's end rather than to any option's — and this confirms
        //    the statement's end really is beyond them;
        //  - no comments in the slice, because CasedTokens strips them and the option nodes are never
        //    Printed, so a comment there would attach to a node nothing emits.
        //
        // A trailing comma is emitted with the element list below and the tail resumes past it —
        // otherwise the tail would start with `,` and this handler would decline a perfectly ordinary
        // temp table. Dropping it instead is not an option: the verifier compares token sequences, so
        // removing a comma is a refusal rather than a tidy-up.
        var end = RangeEndBeforeTerminators(statement);
        var trailingComma = TrailingCommaIndex(definition);
        var tailFrom = trailingComma >= 0 ? trailingComma + 1 : elements[^1].LastTokenIndex + 1;

        var options = new TSqlFragment?[]
            {
                statement.OnFileGroupOrPartitionScheme, statement.TextImageOn, statement.FileStreamOn,
            }
            .Concat(statement.Options)
            .Where(o => o is not null)
            .ToList();

        if (!SignificantTextBetween(tailFrom, end).StartsWith(')')
            || options.Any(o => o!.LastTokenIndex > end)
            || !NoCommentsIn(tailFrom, end))
        {
            return Passthrough(statement);
        }

        // Through Print, not by emitting the elements here: the TableDefinition's own attached
        // comments — a `-- Column level tests` line between the opening parenthesis and the first
        // column — are then handled by the dispatcher rather than needing a WithComments at each call
        // site, which is the mistake this codebase has now made four times.
        return Doc.Concat(
            head,
            Doc.Text(" "),
            Print(statement.SchemaObjectName),
            Doc.Text(" ("),
            Doc.Indent(Doc.Concat(
                Doc.HardLine,
                Print(definition),
                trailingComma >= 0 ? Doc.Text(",") : Doc.Empty)),
            Doc.HardLine,
            CasedTokens(tailFrom, end));
    }
}
