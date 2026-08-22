using Maxdop.Core.Comments;
using Maxdop.Core.Printing;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Formatting;

/// <summary>
/// <c>IF</c>, <c>WHILE</c> and <c>TRY</c>/<c>CATCH</c>.
/// </summary>
/// <remarks>
/// Chosen from corpus data rather than intuition: these three held 57% of all text the
/// formatter was leaving verbatim. The reason is not that control flow is common but that it
/// <em>blocks</em> — passthrough is subtree-scoped, so an unhandled <c>IF</c> freezes its entire
/// body no matter how many other handlers exist. Handling them is what lets the printer descend.
/// <para>Structurally these are closer to the spine than to <c>SELECT</c>: hard lines and
/// indentation, plus a predicate the boolean-expression handlers already print.</para>
/// </remarks>
public sealed partial class SqlPrinter
{
    private Doc PrintIf(IfStatement statement)
    {
        if (statement.Predicate is null || statement.ThenStatement is null)
        {
            return Passthrough(statement);
        }

        var parts = new List<Doc>
        {
            PrintCondition("IF", statement.Predicate),
            PrintBranch(statement.ThenStatement),
        };

        if (statement.ElseStatement is not null)
        {
            // Comments written above the ELSE explain the branch it introduces — `--If @TableName is
            // NOT specified...` heads a fifteen-line else in sp_BlitzIndex — so they are emitted
            // above it. Left where they attached they printed *below* the ELSE, reading as a note
            // about the branch's first statement. A comment written after the ELSE, including
            // `ELSE -- No instance installed`, is past the keyword and so is left alone.
            var hoistedElse = HoistLeadingBefore(
                statement.ElseStatement,
                statement.ThenStatement.LastTokenIndex + 1,
                EffectiveFirstToken(statement.ElseStatement) - 1);

            parts.Add(Doc.HardLine);

            if (hoistedElse is not null)
            {
                parts.Add(hoistedElse);
            }

            parts.Add(Keyword("ELSE"));

            // `ELSE IF` is one construct to a reader but nested IfStatements to ScriptDom. Printing
            // the nested form as an ordinary else-branch indents each rung of the ladder one level
            // further, so a five-way chain marches off the right margin. Keeping the inner IF on
            // the ELSE line flattens it back to how it was written.
            if (statement.ElseStatement is IfStatement)
            {
                // SeparatorBefore, not a plain space: an own-line comment between ELSE and the
                // nested IF would otherwise be pulled onto the ELSE line, then reclassify as
                // end-of-line on the next pass and move again.
                parts.Add(SeparatorBefore(statement.ElseStatement));
                parts.Add(Print(statement.ElseStatement));
            }
            else
            {
                parts.Add(PrintBranch(statement.ElseStatement));
            }
        }

        return Doc.Concat(parts);
    }

    private Doc PrintWhile(WhileStatement statement) =>
        statement.Predicate is null || statement.Statement is null
            ? Passthrough(statement)
            : Doc.Concat(
                PrintCondition("WHILE", statement.Predicate),
                PrintBranch(statement.Statement));

    private Doc PrintTryCatch(TryCatchStatement statement)
    {
        // An empty block is legal T-SQL, contrary to what this handler used to assert: `BEGIN CATCH
        // /* if we cannot read it, skip */ END CATCH` is written in the First Responder Kit. The comment
        // in it is a *dangling* comment, which Print now emits, so the block no longer has to be
        // declined to keep it.
        if (statement.TryStatements is null || statement.CatchStatements is null)
        {
            return Passthrough(statement);
        }

        var tryBody = statement.TryStatements.Statements;
        var catchBody = statement.CatchStatements.Statements;

        // The dispatcher appends the statement terminator, so it must not also appear in the
        // trailing keyword slice.
        var end = RangeEndBeforeTerminators(statement);

        // With an empty block there is no statement to measure from, so the block keywords are located
        // by token type instead. The scans stay unambiguous because each is bounded by the nearest known
        // boundary: past the try body (so a nested `BEGIN … END` inside it cannot be seen) and before the
        // catch body — and when a block is empty it contains no BEGIN or END of its own to confuse them.
        var afterTryBody = tryBody.Count > 0 ? tryBody[^1].LastTokenIndex + 1 : statement.FirstTokenIndex + 1;
        var endCatch = LastTokenOfType(TSqlTokenType.End, afterTryBody, end);

        if (endCatch < 0)
        {
            return Passthrough(statement);
        }

        // `END TRY BEGIN CATCH` is one token run; split it at the BEGIN so each lands on its own
        // line.
        var beginCatch = LastTokenOfType(
            TSqlTokenType.Begin,
            afterTryBody,
            catchBody.Count > 0 ? catchBody[0].FirstTokenIndex - 1 : endCatch - 1);

        if (beginCatch < 0)
        {
            return Passthrough(statement);
        }

        var endTry = LastTokenOfType(TSqlTokenType.End, afterTryBody, beginCatch - 1);
        if (endTry < 0)
        {
            return Passthrough(statement);
        }

        // Where each body's text begins and ends. An empty block collapses to an empty region, which
        // still carries its dangling comment through Print.
        // EffectiveFirstToken, not FirstTokenIndex: a statement whose range excludes its own leading
        // keywords — `END CONVERSATION @handle` begins at the handle — would otherwise have them
        // swallowed by this block's keyword slice, printing `BEGIN CATCH END CONVERSATION` with the
        // handle stranded on the line below.
        var tryFirst = tryBody.Count > 0 ? EffectiveFirstToken(tryBody[0]) : endTry;
        var tryLast = tryBody.Count > 0 ? tryBody[^1].LastTokenIndex : endTry - 1;
        var catchFirst = catchBody.Count > 0 ? EffectiveFirstToken(catchBody[0]) : endCatch;
        var catchLast = catchBody.Count > 0 ? catchBody[^1].LastTokenIndex : endCatch - 1;

        if (tryFirst < 0 || catchFirst < 0)
        {
            return Passthrough(statement);
        }

        // An empty CATCH carrying a comment used to be declined, and the reason was ScriptDom's: an
        // empty StatementList has the range [-1..-1], so the attacher has no node there to own the
        // comment. It falls to the *try* block's list as a trailing comment instead, and emitting it
        // from there moved `/* if we cannot read it, skip */` out of the CATCH and onto the end of
        // the TRY body — preserved, but describing the code it was written to excuse.
        //
        // The block knows where the comment belongs even though no node does, so it emits the
        // comment itself and marks it emitted, which suppresses it at whatever the attacher chose.
        // Worth doing: this single decline held 68% of all text the First Responder Kit left
        // unformatted — seven procedures frozen whole over one comment apiece.
        var catchDangling = catchBody.Count == 0
            ? CommentsIn(beginCatch + 1, endCatch - 1)
            : [];

        foreach (var comment in catchDangling)
        {
            MarkEmitted(comment);
        }

        // The four keyword runs are read from the tokens rather than written out, because `TRY` and
        // `CATCH` are non-reserved words that lex as identifiers. These four regions hold nothing but
        // `BEGIN`, `END`, `TRY` and `CATCH`, so they are keyword positions and the whole run is recased
        // — `begin try` becomes `BEGIN TRY`.
        return Doc.Concat(
            Keywords(statement.FirstTokenIndex, tryFirst - 1),
            Doc.Indent(Doc.Concat(Doc.HardLine, Print(statement.TryStatements))),
            Doc.HardLine,
            Keywords(tryLast + 1, beginCatch - 1),
            Doc.HardLine,

            // A comment written above the BEGIN CATCH labels the catch block — `-- Catch body` in
            // ScriptDom's own test script — so it is emitted above it rather than swallowed into the
            // block. The TRY side already reads correctly, because a comment above BEGIN TRY is a
            // leading comment of the whole statement; only the catch half needed this.
            //
            // Two candidate owners, tried in order: an own-line comment there attaches to the catch
            // body's first statement when there is one, and to the statement list itself when the
            // list is what the attacher found first. Neither is guaranteed, so both are asked and the
            // range test decides — a comment written *after* the BEGIN CATCH is past the keyword and
            // hoists from neither.
            (catchBody.Count > 0 ? HoistLeadingBefore(catchBody[0], beginCatch, beginCatch) : null)
                ?? HoistLeadingBefore(statement.CatchStatements, beginCatch, beginCatch)
                ?? Doc.Empty,
            Keywords(beginCatch, catchFirst - 1),

            // No body line for an empty block, or `BEGIN CATCH … END CATCH` gains a blank line —
            // unless the block holds nothing but comments, which are its body for layout purposes.
            catchBody.Count == 0
                ? catchDangling.Count == 0
                    ? Doc.Empty
                    : Doc.Indent(Doc.Concat(Doc.HardLine, CommentDocs.Dangling(catchDangling)))
                : Doc.Indent(Doc.Concat(Doc.HardLine, Print(statement.CatchStatements))),
            Doc.HardLine,
            Keywords(catchLast + 1, end));
    }

    /// <summary>The first token of a given type in a range, or -1.</summary>
    private int FirstTokenOfType(TSqlTokenType type, int fromIndex, int toIndex)
    {
        for (var i = Math.Max(0, fromIndex); i <= Math.Min(toIndex, _tokens.Count - 1); i++)
        {
            if (_tokens[i].TokenType == type)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Index of the last token of the given type in a range, or -1.</summary>
    private int LastTokenOfType(TSqlTokenType type, int fromIndex, int toIndex)
    {
        for (var i = Math.Min(toIndex, _tokens.Count - 1); i >= Math.Max(0, fromIndex); i--)
        {
            if (_tokens[i].TokenType == type)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// <c>IF &lt;predicate&gt;</c> / <c>WHILE &lt;predicate&gt;</c>, with continuation lines aligned
    /// under where the predicate starts.
    /// </summary>
    /// <remarks>
    /// Aligned rather than indented, because the body that follows is itself indented one level: a
    /// predicate broken to the same depth would be indistinguishable from the statement it guards.
    /// <code>
    /// IF @a = 1
    ///    AND @b = 2      -- aligned under the predicate: clearly still the condition
    /// BEGIN
    /// </code>
    /// </remarks>
    private Doc PrintCondition(string keyword, BooleanExpression predicate) => Doc.Concat(
        Keyword(keyword),
        SeparatorBefore(predicate),
        Doc.Align(keyword.Length + 1, Print(predicate)));

    /// <summary>
    /// A branch body: indented when it is a bare statement, at the guard's own indent when it is a
    /// <c>BEGIN … END</c> block.
    /// </summary>
    /// <remarks>
    /// The block form carries its own visual nesting through <c>BEGIN</c>/<c>END</c>, and indenting
    /// it as well would double the depth — which is why the prevailing T-SQL convention puts
    /// <c>BEGIN</c> in the same column as its <c>IF</c>.
    /// </remarks>
    /// <remarks>
    /// <see cref="SeparatorBefore"/> rather than a plain hard line, so that a branch introduced by a
    /// keyword with a comment after it — <c>ELSE /* No instance installed */</c> — keeps the comment
    /// on the keyword's line, where the author put it. The comment is a *leading* comment of the
    /// branch, so without this it would be pushed to a line of its own.
    /// </remarks>
    /// <summary><c>END CONVERSATION @handle WITH CLEANUP</c></summary>
    /// <remarks>
    /// Exists for the range, not the layout. The node begins at its conversation handle, so its own
    /// keywords sit outside it — which means passthrough would drop them and an enclosing block
    /// would absorb them. <see cref="EffectiveFirstToken"/> claims them and this emits them, the
    /// same two-part treatment <c>PERIOD FOR SYSTEM_TIME</c> needs.
    /// <para>Everything after the handle — <c>WITH CLEANUP</c>, <c>WITH ERROR = … DESCRIPTION = …</c>
    /// — is a slice, and its span is checked rather than assumed, so a form this does not model
    /// falls back rather than losing tokens.</para>
    /// </remarks>
    private Doc PrintEndConversation(EndConversationStatement statement)
    {
        if (statement.Conversation is null)
        {
            return Passthrough(statement);
        }

        var head = EffectiveFirstToken(statement);
        if (head >= statement.FirstTokenIndex)
        {
            return Passthrough(statement);
        }

        var parts = new List<Doc>
        {
            Keywords(head, statement.FirstTokenIndex - 1),
            Doc.Text(" "),
            Print(statement.Conversation),
        };

        // The error code and description are literals inside the tail slice; only their keywords are
        // recased, which is what Keywords does and what the verifier is told about.
        var tailFrom = LastOf(statement.Conversation, statement.ErrorCode, statement.ErrorDescription).LastTokenIndex + 1;
        var end = RangeEndBeforeTerminators(statement);

        if (statement.ErrorCode is not null || statement.ErrorDescription is not null)
        {
            return Passthrough(statement);
        }

        if (tailFrom <= end && SignificantTextBetween(tailFrom, end).Length > 0)
        {
            parts.Add(Doc.Text(" "));
            parts.Add(Keywords(tailFrom, end));
        }

        return Doc.Concat(parts);
    }

    private Doc PrintBranch(TSqlStatement statement)
    {
        // A branch always starts on its own line — the inverse of SeparatorBefore's default, so it
        // cannot be reused here. The exception is a leading comment that shared the introducing
        // keyword's line in the source: `ELSE /* No instance installed */` keeps it there rather
        // than pushing it down, and Print supplies the break after it.
        var leading = _comments.Leading(statement);
        var separator = leading.Count > 0 && !leading[0].AloneOnLine ? Doc.Text(" ") : Doc.HardLine;

        return statement is BeginEndBlockStatement
            ? Doc.Concat(separator, Print(statement))
            : Doc.Indent(Doc.Concat(separator, Print(statement)));
    }
}
