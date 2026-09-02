using Maxdop.Core.Comments;
using Maxdop.Core.Formatting;
using Maxdop.Core.Printing;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Tests;

/// <summary>
/// Drives <see cref="SqlFormatter.VerifyOrRefuse"/> with output that is deliberately wrong, and
/// asserts the formatter hands back the input rather than the damage.
/// </summary>
/// <remarks>
/// <para>Every other suite proves the gates <em>pass</em> on good output. Nothing proved what
/// happens when one fails, because nothing could: a working printer cannot produce output that trips
/// them, and the constructs that used to are all fixed — <see cref="ZeroRefusalTests"/> is the record
/// of that, and every case in it now takes the happy path by design. The refusal machinery lost its
/// coverage as the bugs were fixed.</para>
/// <para>That left the largest promise the project makes — <b>a maxdop bug never modifies a
/// file</b> — as the one behaviour no test executed. A refusal that returned the rejected text
/// instead of the input would have kept every suite green while doing precisely the thing the whole
/// design exists to prevent.</para>
/// <para>Each gate is tripped on its own, which also proves they are independent: a dropped comment
/// is invisible to the round-trip check, and a changed token is invisible to the comment check, so
/// a test that tripped both at once would not show which gate caught it.</para>
/// </remarks>
public class RefusalPathTests
{
    /// <summary>
    /// Reproduces what <see cref="SqlFormatter.Format"/> does up to the point of verification, so the
    /// gates are handed exactly what they would see in production.
    /// </summary>
    private static Prepared Prepare(string sql)
    {
        var options = FormatOptions.Default;
        var parser = ParserFactory.Create(options);
        using var reader = new StringReader(sql);
        var root = parser.Parse(reader, out var errors);

        Assert.Empty(errors);

        var comments = CommentAttacher.Attach(root);
        var printer = new SqlPrinter(root, comments, options);
        var formatted = DocPrinter.Print(printer.Print(root), options.Print);

        return new Prepared(sql, formatted, root, comments, options, printer.KeywordCasedTokens);
    }

    private sealed record Prepared(
        string Sql,
        string Formatted,
        TSqlFragment Root,
        CommentMap Comments,
        FormatOptions Options,
        IReadOnlySet<int> Keywords)
    {
        /// <summary>Runs the gates over <paramref name="candidate"/> instead of the real output.</summary>
        internal FormatResult Verify(string candidate) =>
            SqlFormatter.VerifyOrRefuse(Sql, candidate, Root, Comments, Options, Keywords);
    }

    // --- the control ------------------------------------------------------------------

    [Fact]
    public void RealOutputPassesEveryGate()
    {
        var prepared = Prepare("select a, b from dbo.t where a > 1; -- note\n");

        var result = prepared.Verify(prepared.Formatted);

        // Without this the refusal tests below prove nothing: a harness that always refuses would
        // pass all of them.
        Assert.Equal(FormatStatus.Formatted, result.Status);
        Assert.Equal(prepared.Formatted, result.Output);
        Assert.Null(result.RejectedOutput);
    }

    // --- gate 1: the output must re-parse ---------------------------------------------

    [Fact]
    public void OutputThatNoLongerParsesIsRefused()
    {
        var prepared = Prepare("SELECT a FROM dbo.t;\n");

        var result = prepared.Verify("SELECT FROM WHERE ((;\n");

        Assert.Equal(FormatStatus.Refused, result.Status);
        Assert.Contains("no longer parses", Assert.Single(result.Diagnostics), StringComparison.Ordinal);
        Assert.Contains("maxdop bug", Assert.Single(result.Diagnostics), StringComparison.Ordinal);
    }

    // --- gate 2: the output must mean what the input meant -----------------------------

    [Fact]
    public void OutputWithAChangedLiteralIsRefused()
    {
        var prepared = Prepare("SELECT 1;\n");

        var result = prepared.Verify("SELECT 2;\n");

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(FormatStatus.Refused, result.Status);
        Assert.Contains("token text changed", diagnostic, StringComparison.Ordinal);

        // The diagnostic must name both tokens. A refusal that says only "something changed" sends
        // the reader back to diffing two files by hand, which is the state this project exists to
        // improve on — and mutation testing showed nothing was holding the message to it.
        Assert.Contains("expected \"1\"", diagnostic, StringComparison.Ordinal);
        Assert.Contains("got \"2\"", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputWithAChangedTokenTypeIsRefused()
    {
        var prepared = Prepare("SELECT a, b FROM dbo.t;\n");

        // Dropping `, b` is caught as a *type* change rather than a short output: the comparison
        // walks positionally, so the comma meets FROM long before the counts are compared.
        var result = prepared.Verify("SELECT a FROM dbo.t;\n");

        Assert.Equal(FormatStatus.Refused, result.Status);
        Assert.Contains("token type changed", Assert.Single(result.Diagnostics), StringComparison.Ordinal);
    }

    [Fact]
    public void OutputMissingATrailingTokenIsRefused()
    {
        var prepared = Prepare("SELECT 1;\n");

        // Every shared position matches and the output simply stops early, which is the only way to
        // reach the short-output arm. Losing a statement terminator is the case that matters.
        var result = prepared.Verify("SELECT 1\n");

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(FormatStatus.Refused, result.Status);
        Assert.Contains("missing 1 token", diagnostic, StringComparison.Ordinal);

        // Anchored at the point the output ran out, naming the token that should have been there.
        Assert.Contains("expected \";\"", diagnostic, StringComparison.Ordinal);
        Assert.Contains("<end of output>", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputWithAnExtraTokenIsRefused()
    {
        var prepared = Prepare("SELECT 1;\n");

        var result = prepared.Verify("SELECT 1;;\n");

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(FormatStatus.Refused, result.Status);
        Assert.Contains("extra token", diagnostic, StringComparison.Ordinal);
        Assert.Contains("<end of input>", diagnostic, StringComparison.Ordinal);
        Assert.Contains("got \";\"", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void CaseChangeOnAnIdentifierIsRefused()
    {
        // The negotiated exception is per token position and this one was never claimed, so an
        // identifier's case is still compared exactly. Under a case-sensitive collation [Foo] and
        // [foo] are different objects.
        var prepared = Prepare("SELECT [Foo] FROM dbo.t;\n");

        var result = prepared.Verify("SELECT [foo] FROM dbo.t;\n");

        Assert.Equal(FormatStatus.Refused, result.Status);
    }

    // --- gate 3: the comments must survive ---------------------------------------------

    [Fact]
    public void OutputThatDroppedACommentIsRefused()
    {
        var prepared = Prepare("SELECT a FROM dbo.t; -- keep me\n");

        // Comments are trivia, so this is invisible to the round-trip check — it is caught only
        // because comment survival is checked separately rather than assumed.
        var result = prepared.Verify("SELECT a FROM dbo.t;\n");

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(FormatStatus.Refused, result.Status);
        Assert.Contains("comment count changed", diagnostic, StringComparison.Ordinal);

        // Naming the comment that went missing is the whole point of the diagnostic: a bare count
        // leaves grepping the file as the only way forward.
        Assert.Contains("keep me", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputThatAlteredACommentIsRefused()
    {
        var prepared = Prepare("SELECT a FROM dbo.t; -- original\n");

        // Same count, different text — the per-comment comparison rather than the count check.
        var result = prepared.Verify("SELECT a FROM dbo.t; -- tampered\n");

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(FormatStatus.Refused, result.Status);
        Assert.Contains("changed or moved", diagnostic, StringComparison.Ordinal);
        Assert.Contains("original", diagnostic, StringComparison.Ordinal);
    }

    // --- the promise ------------------------------------------------------------------

    [Theory]
    [InlineData("SELECT FROM WHERE ((;\n")]              // does not parse
    [InlineData("SELECT 2 FROM dbo.t; -- keep me\n")]    // token changed
    [InlineData("SELECT a FROM dbo.t;\n")]               // comment dropped
    public void EveryRefusalReturnsTheInputByteForByte(string damaged)
    {
        const string Sql = "SELECT 1 FROM dbo.t; -- keep me\r\n";
        var prepared = Prepare(Sql);

        var result = prepared.Verify(damaged);

        Assert.Equal(FormatStatus.Refused, result.Status);

        // The claim in FormatResult's own summary: Output is always safe to write, and on failure it
        // is the original input byte for byte. CRLF included — a refusal that normalised line endings
        // would still be a refusal that modified the file.
        Assert.Equal(Sql, result.Output);
        Assert.False(result.Changed);
    }

    [Fact]
    public void ARefusalKeepsTheRejectedOutputForDiagnosis()
    {
        const string Damaged = "SELECT 2;\n";
        var prepared = Prepare("SELECT 1;\n");

        var result = prepared.Verify(Damaged);

        // Diagnostics only, and never written to a file — but a refusal you cannot inspect is
        // painful to fix, because the diagnostic's line number refers to text the caller does not
        // otherwise have.
        Assert.Equal(Damaged, result.RejectedOutput);
        Assert.NotEqual(result.Output, result.RejectedOutput);
    }

    [Fact]
    public void RefusalIsReportedSeparatelyFromAParseFailure()
    {
        // Exit code 1 is the input's problem, 2 is maxdop's. A refusal reported as ParseFailed would
        // tell a pipeline to look at its own SQL for a bug that is not in it.
        var refused = Prepare("SELECT 1;\n").Verify("SELECT FROM WHERE ((;\n");
        var unparseable = SqlFormatter.Format("SELECT FROM WHERE ((;\n");

        Assert.Equal(FormatStatus.Refused, refused.Status);
        Assert.Equal(FormatStatus.ParseFailed, unparseable.Status);
    }
}
