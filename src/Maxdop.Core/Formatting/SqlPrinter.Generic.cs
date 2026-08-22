using Maxdop.Core.Printing;
using Maxdop.Core.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Formatting;

/// <summary>
/// The generic fallback: formats a construct this printer has no hand-written handler for, by
/// discovering its immediate children and emitting them in token order.
/// </summary>
/// <remarks>
/// <para>T-SQL's administrative surface is enormous and mostly shaped the same way — a keyword run, a
/// name, and a parenthesised or comma-separated run of options. <c>GRANT SELECT ON dbo.t TO [role]</c>,
/// <c>ALTER INDEX ix ON t REBUILD</c>, <c>BACKUP DATABASE d TO DISK = 'x'</c>,
/// <c>CREATE EVENT SESSION …</c>: ScriptDom gives each its own node type, several hundred of them, and
/// hand-writing a handler apiece is neither feasible nor useful when the layout decision is the same
/// every time.</para>
/// <para>So this does what <see cref="PrintPartsInTokenOrder"/> does for column definitions, but works
/// out the child list at runtime instead of having it written down. Everything between and around the
/// children is read from the token stream, which means <b>the safety property is unchanged</b>: every
/// token in the range ends up either inside a printed child or inside a slice.</para>
/// <para>What this buys is not clever layout — it is normalised spacing, keyword casing, and above all
/// <em>descent</em>: a <c>CREATE EVENT SESSION</c> whose predicate contains a real expression now has
/// that expression formatted, and a statement that merely mentions a <c>SELECT</c> gets the query
/// formatted properly. What it does not do is invent line breaks it cannot justify, so these statements
/// stay on one line unless a child breaks internally.</para>
/// <para>Deliberately <em>not</em> applied to expressions and clauses, only to statements and the
/// option/definition nodes beneath them. An expression's layout carries meaning — operator precedence,
/// where a line may break — and getting that wrong looks worse than leaving it alone, whereas an
/// administrative statement has no interesting shape to get wrong.</para>
/// </remarks>
public sealed partial class SqlPrinter
{
    /// <summary>
    /// Emits a construct by discovering its immediate children, or passes it through when the shape
    /// cannot be accounted for.
    /// </summary>
    private Doc PrintGeneric(TSqlFragment node)
    {
        // A construct whose range is unset cannot be sliced, and one already being passed through
        // must not be re-entered.
        if (node.FirstTokenIndex < 0 || node.LastTokenIndex < node.FirstTokenIndex)
        {
            return Passthrough(node);
        }

        var children = ImmediateChildren(node);

        // A comment in the *tail* — after the last child — would be dropped: slices strip comments, and
        // with no child following it there is nothing for the attacher to have given it to. Comments in
        // the head or between children are safe, because they attach to the child that follows and that
        // child is printed. This asymmetry was measured across 41 hand-written cases, not assumed.
        var end = node is TSqlStatement statement ? RangeEndBeforeTerminators(statement) : node.LastTokenIndex;
        var tailFrom = children.Count > 0 ? children[^1].LastTokenIndex + 1 : node.FirstTokenIndex;

        if (!NoCommentsIn(tailFrom, end)
            || !ChildrenAreInOrder(node, children)
            || HasLexicallySignificantSpacing(node.FirstTokenIndex, end))
        {
            return Passthrough(node);
        }

        // Indented, so that a child which breaks internally — a TableDefinition inside
        // `CREATE TYPE … AS TABLE (…)`, a SELECT inside a statement that embeds one — lines its
        // continuation up under the statement instead of starting at column zero.
        return Doc.Indent(PrintParts(node, pureKeywords: false, [.. children], preserveCase: true));
    }

    /// <summary>
    /// Whether the discovered children run strictly in order, with no two overlapping.
    /// </summary>
    /// <remarks>
    /// A written-down child list is vetted by whoever wrote it; a discovered one is not. Where two
    /// children overlap, the gap between them computes to an inverted range, which reads as empty — so
    /// every token between them is dropped in silence. That is how <c>CREATE DATABASE … FILEGROUP
    /// SalesGroup1(…), (…)</c> lost the word <c>FILEGROUP</c>.
    /// <para>Compared against <see cref="EffectiveFirstToken"/> rather than the raw index, because that
    /// is the boundary the gap is measured from.</para>
    /// </remarks>
    private bool ChildrenAreInOrder(TSqlFragment node, List<TSqlFragment> children)
    {
        var cursor = node.FirstTokenIndex;

        foreach (var child in children.OrderBy(c => c.FirstTokenIndex))
        {
            if (EffectiveFirstToken(child) < cursor)
            {
                return false;
            }

            cursor = child.LastTokenIndex + 1;
        }

        return true;
    }

    /// <summary>
    /// Whether a range contains a number next to a dot, where whitespace changes how the text lexes.
    /// </summary>
    /// <remarks>
    /// Normalising whitespace is safe almost everywhere, and dangerous here. An IPv4 address may be
    /// written <c>1.2 .3 . 4</c>, which lexes as <c>Numeric("1.2")</c>, <c>Numeric(".3")</c>,
    /// <c>Dot</c>, <c>Integer("4")</c> — the token boundaries depend on the spaces. Re-emitting the
    /// parts with one space each regroups the characters, and the result lexes into different tokens:
    /// the verifier reported <c>expected Dot ".", got Numeric ".4"</c>.
    /// <para>The same family as the unary-operator rule that keeps <c>- -1</c> from becoming
    /// <c>--1</c>, a line comment. Rather than name the node types that can contain an address, the
    /// hazard itself is detected: a dot adjacent to a number.</para>
    /// </remarks>
    private bool HasLexicallySignificantSpacing(int fromIndex, int toIndex)
    {
        var previous = -1;

        for (var i = Math.Max(0, fromIndex); i <= Math.Min(toIndex, _tokens.Count - 1); i++)
        {
            if (_tokens[i].IsTrivia())
            {
                continue;
            }

            if (previous >= 0 && (IsDotBesideNumber(previous, i) || IsDotBesideNumber(i, previous)))
            {
                return true;
            }

            previous = i;
        }

        return false;
    }

    /// <summary>Whether the first index is a dot and the second a numeric literal.</summary>
    private bool IsDotBesideNumber(int dot, int number) =>
        _tokens[dot].TokenType == TSqlTokenType.Dot
        && _tokens[number].TokenType is TSqlTokenType.Integer or TSqlTokenType.Numeric or TSqlTokenType.Real;

    /// <summary>
    /// A construct's immediate children: every descendant whose range is not contained in another
    /// descendant's.
    /// </summary>
    /// <remarks>
    /// ScriptDom exposes children only through typed properties, with no generic enumeration, so they
    /// are recovered from a visitor over all descendants by keeping the maximal ranges. That is the same
    /// trick <c>TableElements</c> uses to drop a column constraint that also appears in the table's
    /// constraint list, and it is exact rather than approximate: a node contained in another is by
    /// definition not an immediate child of this one.
    /// </remarks>
    private static List<TSqlFragment> ImmediateChildren(TSqlFragment node)
    {
        var collector = new ChildCollector(node);
        node.Accept(collector);

        // Nodes sharing an identical range are collapsed to the outermost, which is the one visited
        // first because ScriptDom's visitor is pre-order. This matters only here: `Contains` is *strictly*
        // contains, deliberately, so that two nodes with equal ranges do not each eliminate the other —
        // which is right for a written-down child list and wrong for a discovered one. A single-identifier
        // `SchemaObjectName` has exactly its Identifier's range, so both survived the filter and
        // `ALTER INDEX ALL ON t1` came out as `ON t1 t1`. Sixty-nine files stopped parsing.
        var deduped = new List<TSqlFragment>();
        var seen = new HashSet<(int First, int Last)>();

        foreach (var fragment in collector.Found)
        {
            if (seen.Add((fragment.FirstTokenIndex, fragment.LastTokenIndex)))
            {
                deduped.Add(fragment);
            }
        }

        return [.. deduped.Where(f => !deduped.Any(other => Contains(other, f)))];
    }

    /// <summary>Collects every descendant of a node that carries a usable token range.</summary>
    private sealed class ChildCollector(TSqlFragment root) : TSqlFragmentVisitor
    {
        internal List<TSqlFragment> Found { get; } = [];

        public override void Visit(TSqlFragment fragment)
        {
            // Clamped to the root's own range, because a ScriptDom node can list a child that lies
            // *outside* it. `FileGroupDefinition` covers `FILEGROUP g(…)` but its FileDeclarations list
            // also holds the `, (…)` that follows, which the range stops short of. Unclamped, that
            // declaration was emitted twice — once by the filegroup and once by the statement, which
            // legitimately sees it as a sibling because it is not contained in the filegroup's range.
            if (!ReferenceEquals(fragment, root)
                && fragment.FirstTokenIndex >= root.FirstTokenIndex
                && fragment.LastTokenIndex <= root.LastTokenIndex
                && fragment.LastTokenIndex >= fragment.FirstTokenIndex)
            {
                Found.Add(fragment);
            }
        }
    }
}
