using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Syntax;

/// <summary>
/// Token classification helpers. The spike confirmed <c>ScriptTokenStream</c> is lossless —
/// concatenating every token's text reproduces the input byte for byte — so trivia is fully
/// recoverable here and nothing needs to consult the original source string.
/// </summary>
internal static class SqlTokens
{
    internal static bool IsComment(this TSqlParserToken token) =>
        token.TokenType is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment;

    internal static bool IsWhiteSpace(this TSqlParserToken token) =>
        token.TokenType == TSqlTokenType.WhiteSpace;

    /// <summary>Whitespace or comment: present in the stream, absent from the AST.</summary>
    internal static bool IsTrivia(this TSqlParserToken token) =>
        token.IsWhiteSpace() || token.IsComment();

    /// <summary>
    /// Tokens that carry no syntax and should be skipped when looking for real code.
    /// <see cref="TSqlTokenType.EndOfFile"/> is included because its text is empty.
    /// </summary>
    internal static bool IsIgnorable(this TSqlParserToken token) =>
        token.IsTrivia() || token.TokenType == TSqlTokenType.EndOfFile;

    /// <summary>
    /// Token index of the <c>GO</c> that terminates the range ending at
    /// <paramref name="lastIndex"/>, or -1 if there is none.
    /// </summary>
    /// <remarks>
    /// Skips comments as well as whitespace, so everything between a batch's last statement and
    /// its <c>GO</c> falls inside the batch. Stopping at a comment instead would let an
    /// end-of-line comment on the final statement escape the batch entirely and re-attach to
    /// the batch itself — which then prints it after the <c>GO</c>, moving it two lines down.
    /// <para>Shared by the comment pre-pass and the printer on purpose: if the two disagreed
    /// about where a batch ends, comments would attach in one place and print in another.</para>
    /// </remarks>
    internal static int FindBatchTerminator(IList<TSqlParserToken> tokens, int lastIndex)
    {
        var i = lastIndex + 1;
        while (i < tokens.Count && (tokens[i].IsWhiteSpace() || tokens[i].IsComment()))
        {
            i++;
        }

        return i < tokens.Count && tokens[i].TokenType == TSqlTokenType.Go ? i : -1;
    }

    /// <summary>
    /// Tokens whose text is data rather than syntax: their case must never be rewritten.
    /// </summary>
    /// <remarks>
    /// The single source of truth for both <c>RoundTripVerifier</c> (which compares these
    /// exactly and everything else case-insensitively) and the printer (which recases only
    /// tokens outside this set). If the two disagreed, the printer would recase something the
    /// verifier then rejects — which is exactly what happened with <c>WITH ROLLUP</c>: `ROLLUP`
    /// is a non-reserved word and lexes as <see cref="TSqlTokenType.Identifier"/>, so
    /// uppercasing it was flagged as a semantic change. Keeping one classification makes that
    /// class of disagreement impossible.
    /// </remarks>
    internal static bool CarriesValue(this TSqlTokenType type) => type
        is TSqlTokenType.Identifier
        or TSqlTokenType.QuotedIdentifier
        or TSqlTokenType.AsciiStringOrQuotedIdentifier
        or TSqlTokenType.SqlCommandIdentifier
        or TSqlTokenType.Variable
        or TSqlTokenType.Label
        or TSqlTokenType.AsciiStringLiteral
        or TSqlTokenType.UnicodeStringLiteral
        or TSqlTokenType.Integer
        or TSqlTokenType.Numeric
        or TSqlTokenType.Real
        or TSqlTokenType.Money
        or TSqlTokenType.HexLiteral;

    internal static int CountNewLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                count++;
            }
        }

        return count;
    }
}
