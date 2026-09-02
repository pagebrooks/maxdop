using Maxdop.Core.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Formatting;

/// <summary>
/// Formats a file one <c>GO</c>-separated batch at a time, for files that do not parse as a whole.
/// </summary>
/// <remarks>
/// ScriptDom parses a file in one piece, so a single unparseable batch used to cost the whole file
/// its formatting. Migration scripts are the case that matters: they are multi-batch by necessity
/// (a <c>CREATE PROCEDURE</c> must begin its batch) and they routinely contain one batch of sqlcmd
/// syntax — <c>:setvar</c>, <c>$(DatabaseName)</c> — which is not T-SQL and never will be.
/// <para>
/// <b>This runs only when the whole-file parse has already failed.</b> A file that parses takes the
/// ordinary path and cannot be affected by anything here, which is what makes the change safe to
/// add: the thousand corpus files that format today never execute a line of it.
/// </para>
/// <para>
/// <b>The batches come from the lexer, not from a rule about lines.</b> <c>TSqlParser.GetTokenStream</c>
/// tokenises text the parser rejects — measured at zero lex errors on files that fail to parse — and
/// it is the same lexer the parser uses, so <c>GO</c> inside a string or a comment is not a batch
/// separator here for exactly the reason it is not one there. Nothing about batch splitting is
/// re-implemented.
/// </para>
/// </remarks>
internal static class BatchFormatter
{
    /// <summary>
    /// Formats what it can, or returns null to leave the caller's whole-file parse error as the
    /// answer.
    /// </summary>
    internal static FormatResult? TryFormat(string sql, FormatOptions options)
    {
        var parser = ParserFactory.Create(options);
        IList<TSqlParserToken> tokens;
        using (var reader = new StringReader(sql))
        {
            tokens = parser.GetTokenStream(reader, out var lexErrors);

            // A file that will not even tokenise has nothing to split on.
            if (tokens is null || lexErrors.Count > 0)
            {
                return null;
            }
        }

        var pieces = Split(sql, tokens);

        // One batch means splitting has nothing to offer: the single batch is the file, and the
        // caller has already established that it does not parse.
        if (pieces.Count < 2)
        {
            return null;
        }

        var output = new System.Text.StringBuilder(sql.Length);
        var formatted = 0;
        var unparsed = new List<string>();

        foreach (var piece in pieces)
        {
            if (piece.IsSeparator)
            {
                // The separator is copied through byte for byte apart from the keyword's own casing:
                // its surrounding whitespace, blank lines and any `GO 5` repeat count are the
                // author's. Casing `go` is the one thing done to it, so that a batch-formatted file
                // does not end up with `go` where the ordinary path produces `GO`.
                output.Append(piece.Text[..piece.KeywordStart]);
                output.Append(options.KeywordCase == KeywordCase.Lower ? "go" : "GO");
                output.Append(piece.Text[(piece.KeywordStart + piece.KeywordLength)..]);
                continue;
            }

            if (piece.Text.Trim().Length == 0)
            {
                output.Append(piece.Text);
                continue;
            }

            var result = SqlFormatter.Format(piece.Text, options);

            switch (result.Status)
            {
                case FormatStatus.Formatted:
                    formatted++;
                    output.Append(result.Output);
                    break;

                case FormatStatus.ParseFailed:
                    // The input's problem, and contained to this batch.
                    unparsed.Add(result.Diagnostics.Count > 0 ? result.Diagnostics[0] : "unparseable");
                    output.Append(piece.Text);
                    break;

                default:
                    // A refusal is a maxdop bug, and the promise that a maxdop bug never modifies a
                    // file outranks the convenience of formatting the batches either side of it.
                    // The whole file is declined, exactly as it would be without batching.
                    return FormatResult.Refuse(
                        sql,
                        result.Diagnostics.Count > 0 ? result.Diagnostics[0] : "refused",
                        result.RejectedOutput);
            }
        }

        // Nothing gained: let the caller report the whole-file parse error it already has.
        if (formatted == 0)
        {
            return null;
        }

        var text = output.ToString();

        // The seam check. Each batch has already been verified in full by SqlFormatter — token
        // sequence, comments and all — so what is left to prove is that assembling them lost or
        // gained nothing: a batch whose trailing newline went missing could weld `END` to the `GO`
        // that follows it, and no per-batch check can see across that boundary.
        //
        // Text is compared case-insensitively here *because* the exact comparison already ran per
        // batch, with the printer's keyword claims, which are indices into a single batch's token
        // stream and do not mean anything at file level. This check is looking for tokens that
        // moved, merged or vanished, not for casing.
        if (!SeamsHold(parser, text, tokens, out var seamDiagnostic))
        {
            return FormatResult.Refuse(sql, seamDiagnostic, text);
        }

        // Every batch formatted: the only thing the whole-file parse objected to was the separator
        // syntax itself, which is a client feature rather than T-SQL — `GO 5`, sqlcmd's repeat
        // count. Nothing was left unformatted, so nothing is left for the caller to look at.
        if (unparsed.Count == 0)
        {
            return FormatResult.Success(text, sql);
        }

        return FormatResult.PartiallyFormatted(
            text,
            sql,
            [
                $"formatted {formatted} of {pieces.Count(p => !p.IsSeparator && p.Text.Trim().Length > 0)} batches; "
                + $"{unparsed.Count} left unchanged because they do not parse.",
                .. unparsed,
            ]);
    }

    /// <summary>A batch of text, or the separator between two of them.</summary>
    private readonly record struct Piece(string Text, bool IsSeparator, int KeywordStart = 0, int KeywordLength = 0);

    /// <summary>
    /// Cuts the source at each <c>GO</c> token into alternating batches and separators.
    /// </summary>
    /// <remarks>
    /// Every character of the input lands in exactly one piece, and the pieces are concatenated in
    /// order — so if each batch is equivalent to its own text, the file is equivalent to its own
    /// text. That totality is the property the whole approach rests on, and it is why the offsets
    /// are walked rather than the text being re-searched for <c>GO</c>.
    /// <para>A repeat count — <c>GO 5</c>, sqlcmd's "run this batch five times" — belongs to the
    /// separator, not to the batch after it. Leaving it at the head of the next batch would make
    /// that batch unparseable and lose its formatting for no reason.</para>
    /// </remarks>
    private static List<Piece> Split(string sql, IList<TSqlParserToken> tokens)
    {
        var pieces = new List<Piece>();
        var cursor = 0;

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].TokenType != TSqlTokenType.Go)
            {
                continue;
            }

            // Captured before the count is absorbed below, which moves `i`. Using `i` afterwards
            // made the separator start *after* the GO and recased the repeat count instead of the
            // keyword: `GO 5` came out as `GO GO`. The seam check caught it.
            var goIndex = i;
            var last = i;

            // Absorb the optional `GO <count>` repeat count into the separator; see the remarks on
            // this method for why it does not belong to the batch that follows.
            for (var j = i + 1; j < tokens.Count; j++)
            {
                if (tokens[j].TokenType == TSqlTokenType.Integer)
                {
                    last = j;
                    i = j;
                    break;
                }

                if (!tokens[j].IsWhiteSpace() || (tokens[j].Text?.Contains('\n', StringComparison.Ordinal) ?? true))
                {
                    break;
                }
            }

            // The whitespace either side of the separator goes *with* the separator, not with the
            // batches. Two reasons, and the first is a correctness one:
            //
            // A batch's formatted output starts at its first statement, so a leading newline handed
            // to the formatter does not come back — and `GO` followed directly by `CREATE` lexes as
            // the single identifier `goCREATE`. The seam check caught exactly that, as a token count
            // that fell by one.
            //
            // The second is that blank lines around a batch separator are then preserved byte for
            // byte rather than being subject to any layout decision.
            var start = tokens[goIndex].Offset;
            for (var j = goIndex - 1; j >= 0 && tokens[j].IsWhiteSpace(); j--)
            {
                start = tokens[j].Offset;
            }

            // …but not back past the previous separator, which already took that whitespace going
            // forward. Two GO separators with nothing but whitespace between them both claim it, and
            // the second one's start then precedes the cursor — a backwards slice, and an
            // ArgumentOutOfRangeException out of a formatter whose whole promise is not doing that.
            // `GO` immediately followed by `GO` in a file that does not parse as a whole is all it
            // takes; AdventureWorks and Northwind's install scripts both do it.
            start = Math.Max(start, cursor);

            var end = tokens[last].Offset + (tokens[last].Text?.Length ?? 0);
            for (var j = last + 1; j < tokens.Count && tokens[j].IsWhiteSpace(); j++)
            {
                end = tokens[j].Offset + (tokens[j].Text?.Length ?? 0);
            }

            pieces.Add(new Piece(sql[cursor..start], IsSeparator: false));
            pieces.Add(new Piece(
                sql[start..end],
                IsSeparator: true,
                KeywordStart: tokens[goIndex].Offset - start,
                KeywordLength: tokens[goIndex].Text?.Length ?? 0));
            cursor = end;
        }

        pieces.Add(new Piece(sql[cursor..], IsSeparator: false));
        return pieces;
    }

    /// <summary>
    /// Re-tokenises input and output and compares the significant tokens ignoring case, then the
    /// comment counts.
    /// </summary>
    /// <remarks>
    /// <para>Each batch has already been verified in full by <see cref="SqlFormatter"/>, so what is
    /// left to prove is that assembling them lost or gained nothing across a boundary no per-batch
    /// check can see.</para>
    /// <para><b>Internal rather than private so the failure paths can be driven directly.</b> Nothing
    /// a working assembler produces can trip this — which is the point of the assembler, and which
    /// left every diagnostic below as code no test executed. Handing it deliberately damaged output
    /// is the only way to prove the seam check refuses rather than waving the damage through.</para>
    /// </remarks>
    /// <param name="parser">Parser to re-tokenise with; the same one that produced the input tokens.</param>
    /// <param name="formatted">The assembled output to check.</param>
    /// <param name="inputTokens">Token stream of the original file.</param>
    /// <param name="diagnostic">On failure, what diverged.</param>
    internal static bool SeamsHold(
        TSqlParser parser,
        string formatted,
        IList<TSqlParserToken> inputTokens,
        out string diagnostic)
    {
        IList<TSqlParserToken> outputTokens;
        using (var reader = new StringReader(formatted))
        {
            outputTokens = parser.GetTokenStream(reader, out var errors);
            if (outputTokens is null || errors.Count > 0)
            {
                diagnostic = "batch-formatted output no longer tokenises. This is a maxdop bug; "
                    + "the input was left unchanged.";
                return false;
            }
        }

        var before = Significant(inputTokens);
        var after = Significant(outputTokens);

        if (before.Count != after.Count)
        {
            diagnostic = $"batch assembly changed the token count from {before.Count} to {after.Count}. "
                + "This is a maxdop bug; the input was left unchanged.";
            return false;
        }

        for (var i = 0; i < before.Count; i++)
        {
            if (before[i].TokenType == after[i].TokenType
                && string.Equals(before[i].Text, after[i].Text, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            diagnostic = $"batch assembly changed a token near line {before[i].Line}: "
                + $"expected {before[i].TokenType} \"{before[i].Text}\", got {after[i].TokenType} \"{after[i].Text}\". "
                + "This is a maxdop bug; the input was left unchanged.";
            return false;
        }

        // Comments are compared here too, since the assembly is the one place a comment can fall
        // into a gap between pieces rather than into a batch.
        var commentsBefore = inputTokens.Count(t => t.IsComment());
        var commentsAfter = outputTokens.Count(t => t.IsComment());
        if (commentsBefore != commentsAfter)
        {
            diagnostic = $"batch assembly changed the comment count from {commentsBefore} to {commentsAfter}. "
                + "This is a maxdop bug; the input was left unchanged.";
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    private static List<TSqlParserToken> Significant(IList<TSqlParserToken> tokens)
    {
        var result = new List<TSqlParserToken>(tokens.Count);
        foreach (var token in tokens)
        {
            if (!token.IsWhiteSpace() && !token.IsComment() && token.TokenType != TSqlTokenType.EndOfFile)
            {
                result.Add(token);
            }
        }

        return result;
    }
}
