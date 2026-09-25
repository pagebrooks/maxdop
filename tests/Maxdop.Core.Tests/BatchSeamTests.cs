using Maxdop.Core.Formatting;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Tests;

/// <summary>
/// The seam check that guards batch assembly, and the paths into <see cref="BatchFormatter"/> that a
/// working assembler never reaches.
/// </summary>
/// <remarks>
/// <para>Batch formatting is the one place output is <em>assembled</em> rather than printed. Each
/// batch is verified in full on its own, so the residual risk is entirely at the boundaries: a batch
/// whose trailing newline went missing welds <c>END</c> to the <c>GO</c> after it, and no per-batch
/// check can see across that.</para>
/// <para>Mutation testing put <see cref="BatchFormatter"/> at the bottom of the five gates, and
/// coverage independently showed why: every diagnostic in the seam check was unexecuted. Both were
/// pointing at the same thing — the assembler works, so nothing it produces trips its own guard, so
/// the guard was never run.</para>
/// </remarks>
public class BatchSeamTests
{
    private static (TSqlParser Parser, IList<TSqlParserToken> Tokens) Prepare(string sql)
    {
        var parser = ParserFactory.Create(FormatOptions.Default);
        using var reader = new StringReader(sql);
        var tokens = parser.GetTokenStream(reader, out var errors);

        Assert.Empty(errors);
        return (parser, tokens);
    }

    private const string Sql = "CREATE TABLE dbo.A (id INT); -- note\nGO\nSELECT id FROM dbo.A;\nGO\n";

    // --- the control ------------------------------------------------------------------

    [Fact]
    public void UnchangedTextHoldsTheSeam()
    {
        var (parser, tokens) = Prepare(Sql);

        // Without this the failure tests below prove nothing: a check that always refused would pass
        // every one of them.
        Assert.True(BatchFormatter.SeamsHold(parser, Sql, tokens, out var diagnostic));
        Assert.Empty(diagnostic);
    }

    [Fact]
    public void ReCasingAloneHoldsTheSeam()
    {
        var (parser, tokens) = Prepare(Sql);

        // Deliberately case-insensitive. The exact comparison already ran per batch, with the
        // printer's keyword claims — which are indices into a single batch's token stream and mean
        // nothing at file level. This check is looking for tokens that moved, merged or vanished.
        Assert.True(
            BatchFormatter.SeamsHold(parser, Sql.Replace("SELECT", "select", StringComparison.Ordinal), tokens, out _));
    }

    // --- the failures ------------------------------------------------------------------

    [Fact]
    public void OutputThatNoLongerTokenisesIsRejected()
    {
        var (parser, tokens) = Prepare(Sql);

        Assert.False(BatchFormatter.SeamsHold(parser, "SELECT 'unterminated\n", tokens, out var diagnostic));
        Assert.Contains("no longer tokenises", diagnostic, StringComparison.Ordinal);
        Assert.Contains("maxdop bug", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void ATokenLostAcrossTheSeamIsRejected()
    {
        var (parser, tokens) = Prepare(Sql);

        // The failure the seam check exists for: `GO` welded to what follows it, which lexes as one
        // identifier and drops the batch separator entirely.
        var welded = Sql.Replace("GO\nSELECT", "GOSELECT", StringComparison.Ordinal);

        Assert.False(BatchFormatter.SeamsHold(parser, welded, tokens, out var diagnostic));
        Assert.Contains("token count", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void ATokenChangedAcrossTheSeamIsRejected()
    {
        var (parser, tokens) = Prepare(Sql);

        Assert.False(
            BatchFormatter.SeamsHold(parser, Sql.Replace("dbo.A", "dbo.B", StringComparison.Ordinal), tokens, out var diagnostic));

        // Named, with a line number, because the alternative is diffing two whole files by hand.
        Assert.Contains("changed a token near line", diagnostic, StringComparison.Ordinal);
        Assert.Contains("\"A\"", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void ACommentLostBetweenPiecesIsRejected()
    {
        var (parser, tokens) = Prepare(Sql);

        // Comments are stripped before the token comparison, so this is invisible to every check
        // above it. Assembly is the one place a comment can fall into a gap *between* pieces rather
        // than into a batch, which is why the count is compared separately.
        Assert.False(
            BatchFormatter.SeamsHold(parser, Sql.Replace(" -- note", string.Empty, StringComparison.Ordinal), tokens, out var diagnostic));

        Assert.Contains("comment count", diagnostic, StringComparison.Ordinal);
    }

    // --- the guards that decline to split ----------------------------------------------

    [Fact]
    public void AFileThatWillNotTokeniseIsNotSplitAtAll()
    {
        // An unterminated string is a lex error, not a parse error. Splitting on `GO` tokens the
        // lexer never produced would be guesswork, so the whole-file parse error stands.
        var result = SqlFormatter.Format("SELECT 'abc\nGO\nSELECT 1;\n");

        Assert.Equal(FormatStatus.ParseFailed, result.Status);

        // Untouched, and *not* partially formatted — the batch path declined rather than doing its
        // best. The second batch is perfectly good SQL, so a splitter that ignored the lex error
        // would have formatted it and reported a partial success.
        Assert.False(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("batches", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePartialResultCountsTheBatchesItFormatted()
    {
        var result = SqlFormatter.Format(
            "create table dbo.A (id int);\nGO\nraiserror 50001 'legacy';\nGO\ncreate table dbo.B (id int);\nGO\n");

        Assert.Equal(FormatStatus.ParseFailed, result.Status);

        // Two of three formatted, one left alone. The counts are the only signal the caller gets
        // about how much of the file was actually improved, and nothing was pinning them.
        var summary = result.Diagnostics[0];
        Assert.Contains("formatted 2 of 3 batches", summary, StringComparison.Ordinal);
        Assert.Contains("1 left unchanged", summary, StringComparison.Ordinal);

        // The underlying parse error travels with it, so the reader knows which batch and why.
        Assert.Contains(result.Diagnostics.Skip(1), d => d.Contains("raiserror", StringComparison.OrdinalIgnoreCase)
            || d.Contains("50001", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryBatchFormattingLeavesNoPartialReport()
    {
        // `GO 5` is a client feature rather than T-SQL, so the whole-file parse fails while every
        // batch is fine. Nothing is left unformatted, so there is nothing to report.
        var result = SqlFormatter.Format("SELECT 1;\nGO 5\nSELECT 2;\nGO\n");

        Assert.Equal(FormatStatus.Formatted, result.Status);
        Assert.Empty(result.Diagnostics);
    }
}
