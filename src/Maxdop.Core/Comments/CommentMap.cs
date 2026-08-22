using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Comments;

/// <summary>
/// The result of the comment pre-pass: every comment in the file, assigned to an AST node as
/// leading, trailing, or dangling. Node handlers consult this while building the doc IR.
/// </summary>
/// <remarks>
/// Comments arrive in source order within each bucket. Losing one is a silent data-loss bug
/// rather than a formatting nit, so <see cref="Unattached"/> is exposed for callers to assert
/// on rather than left implicit.
/// </remarks>
public sealed class CommentMap
{
    private static readonly IReadOnlyList<Comment> None = [];

    private readonly Dictionary<TSqlFragment, List<Comment>> _leading;
    private readonly Dictionary<TSqlFragment, List<Comment>> _trailing;
    private readonly Dictionary<TSqlFragment, List<Comment>> _dangling;
    private readonly Dictionary<Comment, TSqlFragment> _owners;

    internal CommentMap(
        IReadOnlyList<Comment> all,
        IReadOnlyList<Comment> unattached,
        Dictionary<TSqlFragment, List<Comment>> leading,
        Dictionary<TSqlFragment, List<Comment>> trailing,
        Dictionary<TSqlFragment, List<Comment>> dangling)
    {
        All = all;
        Unattached = unattached;
        _leading = leading;
        _trailing = trailing;
        _dangling = dangling;

        _owners = [];
        foreach (var bucket in new[] { leading, trailing, dangling })
        {
            foreach (var (node, comments) in bucket)
            {
                foreach (var comment in comments)
                {
                    _owners[comment] = node;
                }
            }
        }
    }

    public static CommentMap Empty { get; } = new(
        [],
        [],
        new Dictionary<TSqlFragment, List<Comment>>(ReferenceEqualityComparer.Instance),
        new Dictionary<TSqlFragment, List<Comment>>(ReferenceEqualityComparer.Instance),
        new Dictionary<TSqlFragment, List<Comment>>(ReferenceEqualityComparer.Instance));

    /// <summary>Every comment in the file, in token order.</summary>
    public IReadOnlyList<Comment> All { get; }

    /// <summary>
    /// Comments the pass could not place. Must be empty for a normally parsed script; a
    /// non-empty list means output would silently drop a comment, so the caller should refuse
    /// to format rather than proceed.
    /// </summary>
    public IReadOnlyList<Comment> Unattached { get; }

    public bool IsEmpty => All.Count == 0;

    /// <summary>Comments printed before <paramref name="node"/>.</summary>
    public IReadOnlyList<Comment> Leading(TSqlFragment node) =>
        _leading.TryGetValue(node, out var list) ? list : None;

    /// <summary>Comments printed after <paramref name="node"/>.</summary>
    public IReadOnlyList<Comment> Trailing(TSqlFragment node) =>
        _trailing.TryGetValue(node, out var list) ? list : None;

    /// <summary>
    /// Comments inside <paramref name="node"/> with no sibling to hang off — an empty
    /// <c>BEGIN … END</c> or an empty parameter list. A handler for a construct that can be
    /// empty must emit these or they are lost.
    /// </summary>
    public IReadOnlyList<Comment> Dangling(TSqlFragment node) =>
        _dangling.TryGetValue(node, out var list) ? list : None;

    /// <summary>
    /// The node a comment was assigned to, or null when the pass could not place it.
    /// </summary>
    /// <remarks>
    /// The buckets answer "what does this node carry"; this answers the inverse, which is what a
    /// caller emitting a region of raw tokens needs. A keyword slice reproduces its tokens as text
    /// without dispatching anything inside it, so a comment in that range is emitted by nobody —
    /// unless its owner is a node that <em>is</em> printed, in which case emitting it from the slice
    /// as well would double it. Only the owner's range can tell those apart.
    /// </remarks>
    public TSqlFragment? Owner(Comment comment) =>
        comment is not null && _owners.TryGetValue(comment, out var node) ? node : null;

    /// <summary>True if <paramref name="node"/> has any comment in any position.</summary>
    public bool HasAny(TSqlFragment node) =>
        _leading.ContainsKey(node) || _trailing.ContainsKey(node) || _dangling.ContainsKey(node);
}
