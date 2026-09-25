using Maxdop.Core.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Comments;

/// <summary>
/// The comment pre-pass. Walks the token stream, recovers every comment with its placement and
/// surrounding blank lines, and assigns each to the AST node it belongs with.
/// </summary>
/// <remarks>
/// Comments are the hardest part of formatting T-SQL and the thing existing tools get wrong —
/// Poor Man's T-SQL Formatter's habit of shuffling them is a named reason this project exists.
/// The rule set below is deliberately small, and the ordering of its cases is the whole design.
/// </remarks>
public static class CommentAttacher
{
    /// <param name="root">A fragment from <c>TSqlParser.Parse</c>, carrying its token stream.</param>
    public static CommentMap Attach(TSqlFragment root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var tokens = root.ScriptTokenStream;
        if (tokens is null || tokens.Count == 0)
        {
            return CommentMap.Empty;
        }

        var comments = CollectComments(tokens);
        if (comments.Count == 0)
        {
            return CommentMap.Empty;
        }

        var tree = FragmentTree.Build(root, tokens);
        if (tree is null)
        {
            // Parsed to nothing usable (e.g. a file of only comments). Every comment is
            // reported unattached so the caller passes the file through untouched rather
            // than emitting a formatted file with the comments missing.
            return new CommentMap(comments, comments, NewMap(), NewMap(), NewMap());
        }

        var leading = NewMap();
        var trailing = NewMap();
        var dangling = NewMap();
        var unattached = new List<Comment>();

        foreach (var comment in comments)
        {
            var enclosing = tree.FindDeepestContaining(comment.TokenIndex);
            if (enclosing is null)
            {
                unattached.Add(comment);
                continue;
            }

            var (preceding, following) = enclosing.Neighbours(comment.TokenIndex);

            // Case order matters more than the cases themselves:
            //
            // 1. A comment with code to its left belongs to that code, when (c) holds and either
            //    (a) or (b) does:
            //    (a) it ends its line — it annotates what precedes it, and attaching it to what
            //        follows would move it down a line and change what it appears to describe; or
            //    (b) nothing but whitespace separates it from the preceding node — it sits on that
            //        node's side of whatever separator comes next;
            //    (c) and in either case, only when what stands between them is punctuation. A
            //        *word* in that gap is a keyword, and a keyword there introduces what comes
            //        next rather than closing off what came before.
            //        `ELSE /* No instance installed */` describes the else branch; attaching it to
            //        the preceding node printed it after that branch's `END`, where it read as a
            //        remark about the end of the *then* branch. Measured at 56 occurrences in one
            //        corpus file, and invisible to every safety gate, because a comment that moves
            //        loses nothing and still round-trips.
            //    Test (b) exists because separators are not nodes. In `a /* c */, b` the comment
            //    precedes the comma and in `a, /* c */ b` it follows one, and only the token
            //    positions distinguish them. Getting this wrong walks a comment across an operator:
            //    `1 /* c */ + 2` came out as `1 + /* c */ 2`.
            // 2. Otherwise prefer the following node: an own-line comment introduces what comes
            //    next, and a comment after a separator belongs on that separator's far side.
            // 3. Nothing follows, so it trails the last thing in the enclosing construct.
            // 4. Nothing on either side: the construct is empty and must emit it itself.
            var belongsToPreceding = preceding is not null
                && comment.Placement != CommentPlacement.OwnLine
                && NoWordBetween(tokens, preceding.Last + 1, comment.TokenIndex - 1)
                && (comment.EndsLine || OnlyTriviaBetween(tokens, preceding.Last + 1, comment.TokenIndex - 1));

            if (belongsToPreceding)
            {
                Add(trailing, preceding!.Fragment, comment);
            }
            else if (following is not null)
            {
                Add(leading, following.Fragment, comment);
            }
            else if (preceding is not null)
            {
                // A comment sitting *past* the enclosing construct's terminator does not belong to the
                // last thing inside it. ScriptDom folds the defensive semicolon of the `;WITH` idiom
                // into the previous statement's range, so a section header written between the two
                // terminators lands inside that statement with nothing following it — and case 3 would
                // hand it to the final declaration or clause. The printer then emits it *before* the
                // semicolon it introduced, and for a `--` comment that puts the semicolon inside the
                // comment, which silently removes a statement terminator.
                //
                // Giving it to the enclosing construct instead is the "between two children" case the
                // attacher previously could not express, and it is what lets those statements format at
                // all rather than being passed through whole.
                var owner = BeyondTerminator(tokens, enclosing.Last, comment.TokenIndex)
                    ? enclosing.Fragment
                    : preceding.Fragment;

                Add(trailing, owner, comment);
            }
            else
            {
                Add(dangling, enclosing.Fragment, comment);
            }
        }

        return new CommentMap(comments, unattached, leading, trailing, dangling);
    }

    private static Dictionary<TSqlFragment, List<Comment>> NewMap() =>
        new(ReferenceEqualityComparer.Instance);

    private static void Add(Dictionary<TSqlFragment, List<Comment>> map, TSqlFragment node, Comment comment)
    {
        if (!map.TryGetValue(node, out var list))
        {
            list = [];
            map[node] = list;
        }

        list.Add(comment);
    }

    private static List<Comment> CollectComments(IList<TSqlParserToken> tokens)
    {
        var comments = new List<Comment>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.IsComment())
            {
                continue;
            }

            var text = token.Text ?? string.Empty;
            var (placement, endsLine) = Classify(tokens, i);
            comments.Add(new Comment(
                tokenIndex: i,
                text: text,
                isBlockComment: token.TokenType == TSqlTokenType.MultilineComment,
                line: token.Line,
                column: token.Column,
                placement: placement,
                endsLine: endsLine,
                blankLineBefore: BlankLineBefore(tokens, i),
                blankLineAfter: BlankLineAfter(tokens, i)));
        }

        return comments;
    }

    /// <summary>
    /// Classifies a comment by what shares its line.
    /// </summary>
    /// <remarks>
    /// Deliberately reasons about line numbers and about T-SQL itself, never about whether a
    /// comment token's text includes its own terminating newline — that is an undocumented
    /// ScriptDom representation detail, and depending on it would make placement silently
    /// wrong if it ever changed. A <c>--</c> comment runs to end of line by definition, so
    /// nothing can follow it on its line and no measurement is needed to know that.
    /// </remarks>
    private static (CommentPlacement Placement, bool EndsLine) Classify(IList<TSqlParserToken> tokens, int index)
    {
        var comment = tokens[index];

        var before = PreviousSignificant(tokens, index);
        var newLineBefore = before < 0 || EndLine(tokens[before]) < comment.Line;

        // A `--` comment runs to end of line by definition, so nothing can follow it there.
        bool newLineAfter;
        if (comment.TokenType == TSqlTokenType.SingleLineComment)
        {
            newLineAfter = true;
        }
        else
        {
            var after = NextSignificant(tokens, index);
            newLineAfter = after < 0 || tokens[after].Line > EndLine(comment);
        }

        var placement = newLineBefore
            ? CommentPlacement.OwnLine
            : newLineAfter ? CommentPlacement.EndOfLine : CommentPlacement.Remaining;

        // The two facts are returned independently: whether the comment starts its line is
        // encoded in the placement, and whether it ends its line is the separate signal that
        // decides if a break may follow it.
        return (placement, newLineAfter);
    }

    /// <summary>
    /// Line the token ends on. Only block comments and string literals span lines, and both
    /// carry their newlines in their own text.
    /// </summary>
    private static int EndLine(TSqlParserToken token) =>
        token.Line + SqlTokens.CountNewLines(token.Text);

    /// <summary>
    /// Whether a comment sits after the first of the terminating semicolons that end a construct.
    /// </summary>
    /// <remarks>
    /// Self-limiting to the doubled-terminator case without needing to count: a construct's range ends
    /// at its last semicolon, so for a comment to be *inside* the range and *after* the first one there
    /// must be a second. With a single terminator the comment falls outside the construct entirely and
    /// never reaches here.
    /// </remarks>
    private static bool BeyondTerminator(IList<TSqlParserToken> tokens, int lastIndex, int commentIndex)
    {
        var first = -1;
        for (var i = Math.Min(lastIndex, tokens.Count - 1); i >= 0; i--)
        {
            if (tokens[i].IsTrivia())
            {
                continue;
            }

            if (tokens[i].TokenType != TSqlTokenType.Semicolon)
            {
                break;
            }

            first = i;
        }

        return first >= 0 && commentIndex > first;
    }

    /// <summary>
    /// True when nothing between the two indices is a word — a keyword or an identifier.
    /// </summary>
    /// <remarks>
    /// The distinction this draws is between a separator and an introducer. A comma, a semicolon or
    /// a parenthesis closes off what came before it, so a comment after one still belongs to the
    /// left. A keyword introduces what comes after it, so a comment after one belongs to the right:
    /// <c>ELSE /* … */</c> is about the else branch, not about the end of the then branch.
    /// <para>Deliberately a token-shape test rather than a list of keywords. Naming the introducers
    /// would mean maintaining a list that is wrong the moment T-SQL grows another one, and the
    /// property that matters — punctuation separates, words introduce — holds without it.</para>
    /// </remarks>
    private static bool NoWordBetween(IList<TSqlParserToken> tokens, int fromIndex, int toIndex)
    {
        var to = Math.Min(toIndex, tokens.Count - 1);
        for (var i = Math.Max(0, fromIndex); i <= to; i++)
        {
            var text = tokens[i].Text;
            if (!tokens[i].IsTrivia() && text?.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the token range contains nothing but whitespace and comments — i.e. no separator,
    /// operator or punctuation sits in it.
    /// </summary>
    private static bool OnlyTriviaBetween(IList<TSqlParserToken> tokens, int fromIndex, int toIndex)
    {
        var to = Math.Min(toIndex, tokens.Count - 1);
        for (var i = Math.Max(0, fromIndex); i <= to; i++)
        {
            if (!tokens[i].IsTrivia())
            {
                return false;
            }
        }

        return true;
    }

    private static int PreviousSignificant(IList<TSqlParserToken> tokens, int index)
    {
        var i = index - 1;
        while (i >= 0 && tokens[i].IsWhiteSpace())
        {
            i--;
        }

        return i;
    }

    private static int NextSignificant(IList<TSqlParserToken> tokens, int index)
    {
        var i = index + 1;
        while (i < tokens.Count && tokens[i].IsWhiteSpace())
        {
            i++;
        }

        return i < tokens.Count && tokens[i].TokenType != TSqlTokenType.EndOfFile ? i : -1;
    }

    // Blank-line detection is line arithmetic for the same reason: a gap of two or more lines
    // between two constructs means at least one wholly empty line sat between them, regardless
    // of how the newlines are distributed across tokens.
    private static bool BlankLineBefore(IList<TSqlParserToken> tokens, int index)
    {
        var before = PreviousSignificant(tokens, index);

        // Nothing above means nothing to be separated from.
        return before >= 0 && tokens[index].Line - EndLine(tokens[before]) >= 2;
    }

    private static bool BlankLineAfter(IList<TSqlParserToken> tokens, int index)
    {
        var after = NextSignificant(tokens, index);
        return after >= 0 && tokens[after].Line - EndLine(tokens[index]) >= 2;
    }
}
