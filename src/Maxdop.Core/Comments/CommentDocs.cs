using Maxdop.Core.Printing;

namespace Maxdop.Core.Comments;

/// <summary>
/// Converts attached comments into doc IR. This is where the comment pass meets the printer,
/// and where one hard safety rule lives.
/// </summary>
/// <remarks>
/// <para><b>The rule:</b> a comment that runs to end of line must always be followed by a
/// forced break. A <c>--</c> comment swallows the remainder of its line, so emitting anything
/// after it without a newline does not merely look wrong — it comments out real code and
/// changes what the script does. The round-trip verifier would catch it and refuse to format,
/// but producing correct IR in the first place is what makes the formatter useful rather than
/// merely safe.</para>
/// <para>Blank-line preservation is deliberately not applied here. Whether a blank line
/// survives between two statements is a statement-separator decision belonging to the node
/// handler, which knows what it is joining; doing it here too would double up. The flags are
/// on <see cref="Comment"/> for handlers to read.</para>
/// </remarks>
public static class CommentDocs
{
    /// <summary>
    /// A comment printed before the node it belongs to, including the separator that keeps it
    /// away from that node.
    /// </summary>
    public static Doc Leading(Comment comment)
    {
        ArgumentNullException.ThrowIfNull(comment);

        // The governing rule, in both directions: a comment may be followed by content on the same
        // line if and only if it was in the source. `/* hint */ WHERE` keeps its WHERE; a comment
        // that ended its line still ends it.
        return comment.EndsLine
            ? Doc.Concat(Body(comment), Doc.HardLine)
            : Doc.Concat(Body(comment), Doc.Text(" "));
    }

    /// <summary>
    /// A comment printed after the node it belongs to, including the separator before it.
    /// </summary>
    public static Doc Trailing(Comment comment)
    {
        ArgumentNullException.ThrowIfNull(comment);

        // A comment that had its own line but attached as trailing — because nothing followed
        // it inside the enclosing construct — keeps that line. Deferring it to the end of the
        // previous line would move it up, which is precisely the comment-shuffling other
        // T-SQL formatters are criticised for.
        if (comment.AloneOnLine)
        {
            return Doc.Concat(Doc.HardLine, Body(comment));
        }

        // An end-of-line comment stays at the end of its line, whatever its style.
        //
        // This used to be conditional on SwallowsRestOfLine, on the reasoning that a block comment
        // cannot swallow anything so needs no deferral. That reasoning confused two things:
        // LineSuffix is about *position*, not about swallowing. Emitting `/* c */` as plain text
        // let it print before the separator comma that followed, turning
        // `@a INT = NULL, /* c */` into `@a INT = NULL /* c */,` — and on the next pass that
        // reclassifies as Remaining and moves again. Formatting was not a fixed point.
        //
        // BreakParent belongs here too: nothing followed this comment on its line in the source,
        // so the line must end here. Without it a flat group defers the comment to the next
        // newline, which can be far to the right of where it belongs.
        if (comment.EndsLine)
        {
            return Doc.Concat(
                Doc.LineSuffix(Doc.Concat(Doc.Text(" "), Body(comment))),
                Doc.BreakParent);
        }

        // Code followed it on its line, so it must stay inline where it was.
        return Doc.Concat(Doc.Text(" "), Body(comment));
    }

    /// <summary>
    /// Whether <see cref="Trailing"/> defers this comment to the end of the current line. Such
    /// a comment is overtaken by anything printed after it, which matters when ordering several.
    /// </summary>
    private static bool IsDeferred(Comment comment) =>
        comment.EndsLine && !comment.AloneOnLine;

    /// <summary>
    /// Comments inside an otherwise-empty construct. Emitted verbatim on their own lines, since
    /// there is no sibling to position them against.
    /// </summary>
    public static Doc Dangling(IReadOnlyList<Comment> comments)
    {
        ArgumentNullException.ThrowIfNull(comments);

        if (comments.Count == 0)
        {
            return Doc.Empty;
        }

        var parts = new List<Doc>(comments.Count * 2);
        for (var i = 0; i < comments.Count; i++)
        {
            if (i > 0)
            {
                parts.Add(Doc.HardLine);
            }

            parts.Add(Body(comments[i]));
        }

        return Doc.Concat(parts);
    }

    /// <summary>All of <paramref name="comments"/> as leading comments, in order.</summary>
    public static Doc AllLeading(IReadOnlyList<Comment> comments)
    {
        ArgumentNullException.ThrowIfNull(comments);
        return comments.Count == 0 ? Doc.Empty : Doc.Concat(comments.Select(Leading));
    }

    /// <summary>All of <paramref name="comments"/> as trailing comments, in order.</summary>
    /// <remarks>
    /// Mixing deferred and non-deferred comments needs care: a deferred one jumps to the end of
    /// the line, so a plain one emitted after it would appear <em>before</em> it in the output.
    /// A <see cref="Doc.LineSuffixBoundary"/> between them flushes the pending text first, which
    /// keeps source order intact.
    /// </remarks>
    public static Doc AllTrailing(IReadOnlyList<Comment> comments)
    {
        ArgumentNullException.ThrowIfNull(comments);

        if (comments.Count == 0)
        {
            return Doc.Empty;
        }

        var parts = new List<Doc>(comments.Count * 2);
        var anyDeferred = false;
        foreach (var comment in comments)
        {
            var deferred = IsDeferred(comment);
            if (anyDeferred && !deferred)
            {
                parts.Add(Doc.LineSuffixBoundary);
            }

            parts.Add(Trailing(comment));
            anyDeferred |= deferred;
        }

        return Doc.Concat(parts);
    }

    /// <summary>
    /// The comment text itself. Multi-line block comments go through <see cref="Doc.Verbatim"/>
    /// so their interior alignment is preserved rather than re-indented — re-flowing the inside
    /// of a comment is not the formatter's business.
    /// </summary>
    private static Doc Body(Comment comment) => Doc.Unmeasured(
        comment.Text.Contains('\n', StringComparison.Ordinal)
            ? Doc.Verbatim(comment.Text)
            : Doc.Text(comment.Text));
}
