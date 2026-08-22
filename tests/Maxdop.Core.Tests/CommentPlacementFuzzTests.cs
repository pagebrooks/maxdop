using Maxdop.Core.Formatting;
using Maxdop.Core.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Tests;

/// <summary>
/// Inserts a comment at every token boundary of the committed construct corpus and asserts it comes
/// back between the same two code tokens.
/// </summary>
/// <remarks>
/// Comment <em>position</em> is the formatter's second structural blind spot, and the one no safety
/// gate can see: a comment that moves is still present, still round-trips, and still re-parses, so
/// the output is accepted. Twelve separate placement defects were found by eye or by diffing against
/// another formatter, one construct at a time. This finds them by construction instead.
/// <para>The corpus files are the same ones every other test uses, so the fuzz surface grows
/// automatically whenever a construct is added to them — no fixture of its own to keep in step.</para>
/// </remarks>
public class CommentPlacementFuzzTests
{
    /// <summary>
    /// Text unlikely to collide with a real comment, so it can be found by value in the output.
    /// </summary>
    private const string Marker = "/*<fz>*/";

    private static IEnumerable<string> CorpusFileNames()
    {
        foreach (var path in Directory.EnumerateFiles(
            Path.Combine(AppContext.BaseDirectory, "corpus"),
            "*.sql",
            SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            yield return Path.GetFileName(path);
        }
    }

    /// <summary>
    /// Sites where inserting a comment makes the formatter refuse its own output.
    /// </summary>
    /// <remarks>
    /// Every one of these has the same cause: a keyword slice is emitted by concatenating its
    /// significant tokens, so a comment written *inside* the run — `NOT /* … */ NULL`,
    /// `WITH /* … */ SCHEMABINDING` — is dropped, the comment-survival gate sees the count fall, and
    /// the file is returned untouched. Safe, and still a defect: those files cannot be formatted at
    /// all. Ratcheted rather than asserted at zero so the number cannot grow while the fix is
    /// outstanding. That is now zero: <c>Slice</c> learned to emit them, and the handful of
    /// constructs left over — a passed-through node whose verbatim text already held the comment, a
    /// window frame and a name emitted as slices rather than printed, and a comment orphaned in front
    /// of a closing parenthesis — were fixed one at a time. Inserting a comment anywhere in the
    /// corpus can no longer make the formatter refuse its own output.
    private const int KnownRefusalSites = 0;

    /// <summary>
    /// Sites where the comment survives but ends up between different code than it was written
    /// between.
    /// </summary>
    /// <remarks>
    /// Mostly the same keyword-run cause seen from the other side: a comment inside a run that is
    /// emitted as one unit has nowhere to go but the end of it. Genuine placement defects hide in
    /// here too, which is why the number is pinned rather than waved away.
    /// </remarks>
    private const int KnownMovedSites = 2185;

    [Fact]
    public void CommentsInsertedAtEveryTokenBoundaryKeepTheirPlace()
    {
        var refused = 0;
        var moved = 0;
        var examples = new List<string>();

        foreach (var name in CorpusFileNames())
        {
            foreach (var insertion in new[] { $"\n{Marker}\n", $" {Marker} " })
            {
                var outcome = Fuzz(name, insertion);
                refused += outcome.Refused.Count;
                moved += outcome.Moved.Count;
                examples.AddRange(outcome.Refused.Concat(outcome.Moved).Take(2).Select(e => $"{name}: {e}"));
            }
        }

        Assert.True(
            refused <= KnownRefusalSites && moved <= KnownMovedSites,
            $"comment fuzzing: {refused} refusal site(s) (known {KnownRefusalSites}), "
                + $"{moved} moved site(s) (known {KnownMovedSites}).\n  "
                + string.Join("\n  ", examples.Take(12)));

        Assert.True(
            refused == KnownRefusalSites && moved == KnownMovedSites,
            $"comment fuzzing improved: {refused} refusals and {moved} moves, against known "
                + $"{KnownRefusalSites} and {KnownMovedSites}. Lower the constants so the ratchet holds.");
    }

    private static (List<string> Refused, List<string> Moved) Fuzz(string name, string insertion)
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "corpus", name));
        var tokens = Tokenise(sql);

        var moved = new List<string>();
        var refused = new List<string>();

        for (var i = 0; i < tokens.Count; i++)
        {
            // Only in front of real syntax. A boundary inside trivia is the same boundary reached
            // from the other side, and inserting before end-of-file appends past the last token.
            if (tokens[i].IsTrivia() || tokens[i].TokenType == TSqlTokenType.EndOfFile)
            {
                continue;
            }

            var fuzzed = Splice(tokens, i, insertion);
            var result = SqlFormatter.Format(fuzzed);

            switch (result.Status)
            {
                case FormatStatus.Refused:
                    // The gate rejected the output, so the file is safe — but a comment is trivia and
                    // must never be able to provoke that.
                    refused.Add($"line {tokens[i].Line} before {Describe(tokens[i])}");
                    continue;

                // A comment can push a construct onto the passthrough path, which is a coverage gap
                // rather than a placement defect, and it cannot lose or move anything.
                case not FormatStatus.Formatted:
                    continue;
            }

            var before = Neighbours(fuzzed);
            var after = Neighbours(result.Output);

            if (before != after)
            {
                moved.Add($"line {tokens[i].Line} before {Describe(tokens[i])}: "
                    + $"was [{before.Previous} _ {before.Next}], now [{after.Previous} _ {after.Next}]");
            }
        }

        return (refused, moved);
    }

    private static IList<TSqlParserToken> Tokenise(string sql)
    {
        var parser = ParserFactory.Create(FormatOptions.Default);
        using var reader = new StringReader(sql);
        var tokens = parser.GetTokenStream(reader, out var errors);

        Assert.NotNull(tokens);
        Assert.Empty(errors);
        return tokens;
    }

    /// <summary>The input with <paramref name="insertion"/> placed in front of token <paramref name="index"/>.</summary>
    /// <remarks>
    /// Rebuilt from the token stream rather than spliced by offset, because the stream covers every
    /// byte of the input — whitespace and comments included — so concatenating it reproduces the file
    /// exactly and the insertion point cannot land inside a token.
    /// </remarks>
    private static string Splice(IList<TSqlParserToken> tokens, int index, string insertion)
    {
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (i == index)
            {
                text.Append(insertion);
            }

            text.Append(tokens[i].Text);
        }

        return text.ToString();
    }

    /// <summary>
    /// The code tokens the marker sits between — the only definition of "where a comment is" that
    /// survives reformatting, since line and column both change legitimately.
    /// </summary>
    /// <remarks>
    /// Commas are skipped, and deliberately: which side of a list item its comma sits on is a
    /// formatting choice (<see cref="FormatOptions.LeadingCommas"/>), so re-styling a comma-led list
    /// moves every comma past its neighbouring comment without moving a comment at all. Comparison is
    /// case-insensitive for the same reason — <c>keywordCase</c> rewrites neighbours in place.
    /// </remarks>
    private static (string Previous, string Next) Neighbours(string sql)
    {
        var tokens = Tokenise(sql);
        var marker = -1;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].IsComment() && tokens[i].Text?.Contains(Marker, StringComparison.Ordinal) == true)
            {
                marker = i;
                break;
            }
        }

        Assert.True(marker >= 0, "the inserted comment is missing from the output");

        return (Code(tokens, marker, -1), Code(tokens, marker, 1));
    }

    private static string Code(IList<TSqlParserToken> tokens, int from, int step)
    {
        for (var i = from + step; i >= 0 && i < tokens.Count; i += step)
        {
            var token = tokens[i];
            if (token.IsTrivia() || token.TokenType == TSqlTokenType.Comma)
            {
                continue;
            }

            return token.TokenType == TSqlTokenType.EndOfFile
                ? "<eof>"
                : token.Text?.ToUpperInvariant() ?? string.Empty;
        }

        return step < 0 ? "<bof>" : "<eof>";
    }

    private static string Describe(TSqlParserToken token) =>
        $"\"{token.Text?.Replace("\n", "\\n", StringComparison.Ordinal)}\"";
}
