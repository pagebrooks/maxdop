using Maxdop.Core.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Comments;

/// <summary>
/// A containment tree over AST nodes, keyed by token-index range. ScriptDom exposes no child
/// collection, so this reconstructs the structure comment attachment needs.
/// </summary>
internal sealed class FragmentTree
{
    private readonly Node _root;

    private FragmentTree(Node root) => _root = root;

    internal Node Root => _root;

    /// <summary>
    /// Builds the tree from a parsed fragment. Three properties of ScriptDom's visitor, each
    /// confirmed empirically against a real proc, shape this:
    /// <list type="number">
    /// <item>
    /// The <c>Visit(TSqlFragment)</c> catch-all fires for every node, so no per-type visitor
    /// override is needed across 1,377 exported types.
    /// </item>
    /// <item>
    /// Some nodes carry an unset range of <c>[-1..-1]</c> — in practice <c>StatementList</c>,
    /// a grouping node ScriptDom never assigns tokens to. Those are dropped; their children
    /// then hang off the nearest real ancestor, which is where a comment should attach anyway.
    /// </item>
    /// <item>
    /// Visit order is <em>not</em> source order. Children are visited in property-declaration
    /// order, so e.g. a <c>SELECT</c>'s CTE clause is visited after the query body despite
    /// preceding it in the text. A stack build over raw visit order would therefore produce a
    /// wrong tree; sorting by range first makes the result order-independent.
    /// </item>
    /// </list>
    /// </summary>
    internal static FragmentTree? Build(TSqlFragment root, IList<TSqlParserToken> tokens)
    {
        var collector = new Collector();
        root.Accept(collector);

        var ranked = new List<Node>(collector.Fragments.Count);
        for (var i = 0; i < collector.Fragments.Count; i++)
        {
            var fragment = collector.Fragments[i];
            var first = fragment.FirstTokenIndex;
            var last = fragment.LastTokenIndex;
            if (first < 0 || last < first)
            {
                continue;
            }

            // A batch's terminating GO is outside its range, so a comment sitting on the same
            // line as the GO would otherwise look like it belonged to the *next* batch and get
            // relocated across the separator. Swallowing the GO keeps it where the author put it.
            if (fragment is TSqlBatch)
            {
                var terminator = SqlTokens.FindBatchTerminator(tokens, last);
                if (terminator >= 0)
                {
                    last = terminator;
                }
            }

            ranked.Add(new Node(fragment, first, last, i));
        }

        if (ranked.Count == 0)
        {
            return null;
        }

        // Outermost first at equal start, and original visit order breaks exact-range ties.
        // Wrapper chains such as SelectScalarExpression > ColumnReferenceExpression >
        // MultiPartIdentifier > Identifier share one range, and pre-order guarantees an
        // ancestor is always visited before its descendant, so visit index orders them
        // correctly even though their ranges are identical.
        ranked.Sort(static (a, b) =>
        {
            var byFirst = a.First.CompareTo(b.First);
            if (byFirst != 0)
            {
                return byFirst;
            }

            var byLast = b.Last.CompareTo(a.Last);
            return byLast != 0 ? byLast : a.VisitIndex.CompareTo(b.VisitIndex);
        });

        var stack = new List<Node>();
        foreach (var node in ranked)
        {
            while (stack.Count > 0 && !stack[^1].Contains(node))
            {
                stack.RemoveAt(stack.Count - 1);
            }

            if (stack.Count > 0)
            {
                stack[^1].AddChild(node);
            }

            stack.Add(node);
        }

        return new FragmentTree(ranked[0]);
    }

    /// <summary>
    /// The innermost node whose range contains <paramref name="tokenIndex"/>, or null if even
    /// the root does not. Descends the tree rather than scanning, so cost is proportional to
    /// depth rather than node count.
    /// </summary>
    internal Node? FindDeepestContaining(int tokenIndex)
    {
        if (!_root.ContainsIndex(tokenIndex))
        {
            return null;
        }

        var current = _root;
        while (true)
        {
            var child = current.ChildContaining(tokenIndex);
            if (child is null)
            {
                return current;
            }

            current = child;
        }
    }

    private sealed class Collector : TSqlFragmentVisitor
    {
        internal List<TSqlFragment> Fragments { get; } = [];

        public override void Visit(TSqlFragment fragment) => Fragments.Add(fragment);
    }

    internal sealed class Node(TSqlFragment fragment, int first, int last, int visitIndex)
    {
        private List<Node>? _children;

        internal TSqlFragment Fragment { get; } = fragment;

        internal int First { get; } = first;

        internal int Last { get; } = last;

        internal int VisitIndex { get; } = visitIndex;

        internal IReadOnlyList<Node> Children => _children ?? [];

        internal void AddChild(Node child) => (_children ??= []).Add(child);

        internal bool ContainsIndex(int tokenIndex) => tokenIndex >= First && tokenIndex <= Last;

        internal bool Contains(Node other) => other.First >= First && other.Last <= Last;

        /// <summary>Direct child whose range contains the index, if any.</summary>
        internal Node? ChildContaining(int tokenIndex)
        {
            var index = LastChildStartingAtOrBefore(tokenIndex);
            if (index < 0)
            {
                return null;
            }

            var candidate = _children![index];
            return candidate.ContainsIndex(tokenIndex) ? candidate : null;
        }

        /// <summary>
        /// The children immediately before and after <paramref name="tokenIndex"/>. Only
        /// meaningful when no child contains the index — which is guaranteed at the node
        /// <see cref="FindDeepestContaining"/> returns, since it descends as far as it can.
        /// </summary>
        internal (Node? Preceding, Node? Following) Neighbours(int tokenIndex)
        {
            if (_children is null)
            {
                return (null, null);
            }

            var index = LastChildStartingAtOrBefore(tokenIndex);
            var preceding = index >= 0 ? _children[index] : null;
            var following = index + 1 < _children.Count ? _children[index + 1] : null;
            return (preceding, following);
        }

        // Children are appended in sorted order during the build, so this can binary search.
        // Select lists in real T-SQL run to hundreds of columns, which makes it worth doing.
        private int LastChildStartingAtOrBefore(int tokenIndex)
        {
            if (_children is null)
            {
                return -1;
            }

            var lo = 0;
            var hi = _children.Count - 1;
            var best = -1;
            while (lo <= hi)
            {
                var mid = lo + ((hi - lo) / 2);
                if (_children[mid].First <= tokenIndex)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return best;
        }
    }
}
