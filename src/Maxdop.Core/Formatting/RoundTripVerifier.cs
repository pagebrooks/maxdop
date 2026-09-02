using Maxdop.Core.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Formatting;

/// <summary>
/// Proves that formatted output means the same thing as its input. This is safety invariant #1
/// and the claim the whole project rests on: "it cannot break your code."
/// </summary>
/// <remarks>
/// <para><b>Why token sequences rather than an AST walk.</b> The original design described this
/// as asserting AST equivalence. Comparing the <em>significant token sequences</em> delivers that guarantee and is
/// strictly stronger: the parser is a deterministic function of its token stream, so two inputs
/// with identical significant tokens necessarily produce identical ASTs. The converse does not
/// hold — different tokens can yield the same AST — which makes this the conservative direction.
/// </para>
/// <para>It is also the only version that is actually implementable here. ScriptDom exposes no
/// structural comparer, and writing one across 1,074 node types is not credible; the reflective
/// alternative is exactly the pattern NativeAOT forbids. A node-type fingerprint built from the
/// visitor would compile, but it would miss the failures that matter most — two
/// <c>BooleanComparisonExpression</c>s have the same shape whether the operator is <c>=</c> or
/// <c>&gt;</c>. Token comparison catches that.</para>
/// <para><b>What it permits.</b> Whitespace, line breaks, indentation, and comment movement are
/// invisible to it, which is precisely the formatter's job. Keyword recasing is permitted because
/// that is a requested transformation. Everything else is a defect.</para>
/// <para><b>The one negotiated exception.</b> T-SQL's non-reserved words lex as identifiers, so
/// blanket case-sensitivity for identifiers meant keyword casing could never reach <c>NVARCHAR</c>,
/// <c>NOCOUNT</c> or <c>CAST</c>. The printer may therefore <em>name specific token positions</em> as
/// keyword positions, and only those are compared case-insensitively. This is deliberately a
/// per-token permission rather than a relaxed rule: the printer grants it only from regions whose
/// grammar admits no object name, and every identifier it does not claim is still compared
/// exactly — so a handler that opts in wrongly is caught by the tokens around it.</para>
/// <para>Be precise about what this costs. For claimed positions, case-comparison is delegated to the
/// printer's grammar knowledge — the printer marks its own homework. What remains independently
/// verified even there is everything that matters more: that the token exists, that it is the same
/// token type, that no token was added or lost, and that the claim landed on an identifier at all.
/// A claim can relax case and nothing else. The set is passed in rather than defaulted so that no
/// caller can accidentally verify against a stricter rule than the printer actually followed, which
/// would report failures that are not real.</para>
/// <para><b>The gap it does not cover.</b> Comments are trivia and excluded here, so a dropped or
/// reordered comment is invisible to this check. That is why <see cref="SqlFormatter"/> runs a
/// separate comment-preservation check alongside it.</para>
/// </remarks>
public static class RoundTripVerifier
{
    /// <summary>
    /// Compares two parsed fragments for semantic equivalence.
    /// </summary>
    /// <param name="original">The input as parsed, before formatting.</param>
    /// <param name="formatted">The formatter's output, re-parsed.</param>
    /// <param name="diagnostic">On failure, where and how they diverge.</param>
    /// <param name="keywordPositions">
    /// Indices into the <em>original</em>'s token stream that the printer recased as keywords even
    /// though they lex as identifiers. Exactly these positions are compared case-insensitively.
    /// </param>
    /// <returns>True when the formatted fragment means the same as the original.</returns>
    public static bool Verify(
        TSqlFragment original,
        TSqlFragment formatted,
        out string diagnostic,
        IReadOnlySet<int> keywordPositions)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(formatted);
        ArgumentNullException.ThrowIfNull(keywordPositions);

        var before = Significant(original, out var indices);
        var after = Significant(formatted, out _);

        var shared = Math.Min(before.Count, after.Count);
        for (var i = 0; i < shared; i++)
        {
            if (before[i].TokenType != after[i].TokenType)
            {
                diagnostic = Describe(
                    "token type changed",
                    before[i],
                    $"{before[i].TokenType} {Quote(before[i].Text)}",
                    $"{after[i].TokenType} {Quote(after[i].Text)}");
                return false;
            }

            var claimed = keywordPositions.Contains(indices[i]);

            // A claim may only ever land on an identifier or a `@@` global variable. If the printer
            // has claimed a string literal, a quoted identifier or a local `@variable`, that is a
            // printer bug and relaxing the comparison would hide it — so it fails here instead,
            // loudly. Note the second case is a token-shape test, not a promise that the token names
            // a system variable: `DECLARE @@MyVar INT` parses, so only the printer can know that, and
            // SqlGlobalVariables is where it decides.
            if (claimed
                && before[i].TokenType != TSqlTokenType.Identifier
                && !before[i].IsGlobalVariable())
            {
                diagnostic = Describe(
                    $"printer claimed a {before[i].TokenType} token as a keyword position",
                    before[i],
                    Quote(before[i].Text),
                    Quote(after[i].Text));
                return false;
            }

            if (!TextMatches(before[i], after[i], claimed))
            {
                diagnostic = Describe(
                    "token text changed",
                    before[i],
                    Quote(before[i].Text),
                    Quote(after[i].Text));
                return false;
            }
        }

        if (before.Count > after.Count)
        {
            diagnostic = Describe(
                $"output is missing {before.Count - after.Count} token(s)",
                before[shared],
                Quote(before[shared].Text),
                "<end of output>");
            return false;
        }

        if (after.Count > before.Count)
        {
            diagnostic = Describe(
                $"output has {after.Count - before.Count} extra token(s)",
                before.Count > 0 ? before[^1] : after[0],
                "<end of input>",
                Quote(after[shared].Text));
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    /// <summary>
    /// Tokens the parser actually consumes: everything but whitespace, comments and end-of-file.
    /// </summary>
    /// <param name="fragment">The parsed fragment whose token stream is filtered.</param>
    /// <param name="indices">
    /// Each returned token's index in the full stream, so a caller holding token-index-keyed
    /// information can look it up. The comparison itself walks the two significant sequences
    /// positionally; the raw indices differ between input and output because whitespace does.
    /// </param>
    private static List<TSqlParserToken> Significant(TSqlFragment fragment, out List<int> indices)
    {
        indices = [];

        var tokens = fragment.ScriptTokenStream;
        if (tokens is null)
        {
            return [];
        }

        var result = new List<TSqlParserToken>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].IsIgnorable())
            {
                result.Add(tokens[i]);
                indices.Add(i);
            }
        }

        return result;
    }

    private static bool TextMatches(TSqlParserToken before, TSqlParserToken after, bool keywordPosition)
    {
        var left = before.Text ?? string.Empty;
        var right = after.Text ?? string.Empty;

        // Identifiers and strings are where a case change is a genuine semantic change: under a
        // case-sensitive collation [Foo] and [foo] are different objects, and 'Yes' is not 'yes'.
        // Everything else is a keyword or punctuation, where recasing is requested behaviour.
        // The classification is shared with the printer so the two cannot disagree.
        //
        // The exception is a token the printer has explicitly claimed as a keyword position. T-SQL's
        // non-reserved words — `NVARCHAR`, `NOCOUNT`, `APPLY`, `CAST`, `NOWAIT` — lex as identifiers,
        // so without this the formatter could never apply keyword casing to them and `declare @a int`
        // stayed half-cased. The claim is per token and comes only from regions where the grammar
        // guarantees no object name can appear, so `dbo.t_history` and
        // `COLLATE SQL_Latin1_General_CP1_CI_AS` — which sit in *similar* regions and were found there
        // in the corpus — are still compared exactly.
        return before.TokenType.CarriesValue() && !keywordPosition
            ? string.Equals(left, right, StringComparison.Ordinal)
            : string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a diagnostic anchored to the <em>input</em>'s line and column, since that is the
    /// file the user is looking at.
    /// </summary>
    private static string Describe(string what, TSqlParserToken at, string expected, string actual) =>
        $"round-trip verification failed near input line {at.Line}, column {at.Column}: "
        + $"{what} (expected {expected}, got {actual}). This is a maxdop bug; the input was left unchanged.";

    private static string Quote(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 40)
        {
            text = text[..40] + "…";
        }

        return "\"" + text.Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal) + "\"";
    }
}
