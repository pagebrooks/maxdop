namespace Maxdop.Core.Comments;

/// <summary>
/// Where a comment sat relative to code on its own line. Drives how it may be re-emitted.
/// </summary>
public enum CommentPlacement
{
    /// <summary>Nothing but whitespace precedes it on its line — it describes what follows.</summary>
    OwnLine,

    /// <summary>Code precedes it and nothing follows on its line — it annotates that code.</summary>
    EndOfLine,

    /// <summary>
    /// Code on both sides, e.g. <c>FROM t /* hint */ WHERE</c>. Only block comments can be
    /// in this position, and moving one changes meaning, so it must stay inline.
    /// </summary>
    Remaining,
}

/// <summary>
/// A comment recovered from the token stream, with everything a node handler needs to re-emit
/// it in the same place it came from.
/// </summary>
public sealed class Comment
{
    internal Comment(
        int tokenIndex,
        string text,
        bool isBlockComment,
        int line,
        int column,
        CommentPlacement placement,
        bool endsLine,
        bool blankLineBefore,
        bool blankLineAfter)
    {
        TokenIndex = tokenIndex;
        Text = text;
        IsBlockComment = isBlockComment;
        Line = line;
        Column = column;
        Placement = placement;
        EndsLine = endsLine;
        BlankLineBefore = blankLineBefore;
        BlankLineAfter = blankLineAfter;
    }

    /// <summary>Index into the fragment's <c>ScriptTokenStream</c>. Unique per comment.</summary>
    public int TokenIndex { get; }

    /// <summary>Full comment text including its <c>--</c> or <c>/* */</c> delimiters.</summary>
    public string Text { get; }

    /// <summary><c>/* ... */</c> rather than <c>-- ...</c>.</summary>
    public bool IsBlockComment { get; }

    /// <summary>1-based line in the source.</summary>
    public int Line { get; }

    /// <summary>1-based column in the source.</summary>
    public int Column { get; }

    public CommentPlacement Placement { get; }

    /// <summary>
    /// Nothing but whitespace followed the comment on its line.
    /// </summary>
    /// <remarks>
    /// This and <see cref="CommentPlacement"/> are the two independent facts about a comment's
    /// position, and conflating them causes comments to move. <c>/* note */</c> alone on a line
    /// and <c>/* note */ SELECT 1</c> are both <see cref="CommentPlacement.OwnLine"/> — neither
    /// has code to its left — but only the first ends its line. Re-emitting the second with a
    /// break after it pushes the <c>SELECT</c> down a line.
    /// <para>The rule this exists to express: a comment may be followed by content on the same
    /// line if and only if it was in the source.</para>
    /// </remarks>
    public bool EndsLine { get; }

    /// <summary>
    /// The comment had its line entirely to itself — no code before <em>or</em> after it.
    /// </summary>
    public bool AloneOnLine => Placement == CommentPlacement.OwnLine && EndsLine;

    /// <summary>At least one wholly blank line separated this comment from the code above it.</summary>
    public bool BlankLineBefore { get; }

    /// <summary>At least one wholly blank line separated this comment from the code below it.</summary>
    public bool BlankLineAfter { get; }

    /// <summary>
    /// True when this comment runs to end of line, so anything emitted after it on the same
    /// line would be swallowed. Every <c>--</c> comment qualifies, as does a block comment
    /// containing a newline. Such a comment must always be followed by a hard break.
    /// </summary>
    public bool SwallowsRestOfLine =>
        !IsBlockComment || Text.Contains('\n', StringComparison.Ordinal);

    public override string ToString() =>
        $"{Placement} @{Line}:{Column} (token {TokenIndex}) {Text.Split('\n')[0]}";
}
