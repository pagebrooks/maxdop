using Maxdop.Core.Comments;
using Maxdop.Core.Formatting;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Tests;

/// <summary>
/// Tests for the safety net itself. A verifier that returned <c>true</c> unconditionally would
/// leave the whole formatter test suite green, so the tests that matter here are the ones proving
/// it rejects damage — including a systematic sweep over every possible single-token mutation.
/// </summary>
public class RoundTripVerifierTests
{
    private static TSqlFragment Parse(string sql, out IList<ParseError> errors)
    {
        var parser = new TSql180Parser(initialQuotedIdentifiers: false);
        using var reader = new StringReader(sql);
        return parser.Parse(reader, out errors);
    }

    /// <summary>
    /// "The printer claimed no keyword positions" — the right set for tests that compare two snippets
    /// directly rather than checking real formatter output.
    /// </summary>
    private static readonly IReadOnlySet<int> NoClaims = new HashSet<int>();

    private static TSqlFragment ParseOk(string sql)
    {
        var fragment = Parse(sql, out var errors);
        Assert.Empty(errors);
        return fragment;
    }

    private static bool Equivalent(string before, string after)
    {
        var result = RoundTripVerifier.Verify(ParseOk(before), ParseOk(after), out var diagnostic, NoClaims);
        if (!result)
        {
            Assert.Contains("round-trip verification failed", diagnostic, StringComparison.Ordinal);
        }

        return result;
    }

    // --- what the formatter is allowed to change ------------------------------------

    [Fact]
    public void IdenticalInputIsEquivalent()
    {
        Assert.True(Equivalent("SELECT a FROM t;", "SELECT a FROM t;"));
    }

    [Theory]
    [InlineData("SELECT a FROM t;", "SELECT   a\n  FROM   t ;")]
    [InlineData("SELECT a,b FROM t;", "SELECT\n    a,\n    b\nFROM t;")]
    [InlineData("SELECT a FROM t;", "\n\n\tSELECT a FROM t;\n\n")]
    public void WhitespaceAndLineBreaksAreIrrelevant(string before, string after)
    {
        Assert.True(Equivalent(before, after));
    }

    [Theory]
    [InlineData("select a from t where b is null;", "SELECT a FROM t WHERE b IS NULL;")]
    [InlineData("SELECT a FROM t LEFT OUTER JOIN u ON u.i = t.i;", "select a from t left outer join u on u.i = t.i;")]
    public void KeywordCasingIsPermitted(string before, string after)
    {
        // Recasing keywords is a requested transformation, so it must not trip the verifier.
        Assert.True(Equivalent(before, after));
    }

    [Fact]
    public void CommentChangesAreInvisibleHere()
    {
        // Comments are trivia. They are covered by SqlFormatter's separate comment-preservation
        // check, and this verifier deliberately ignores them.
        Assert.True(Equivalent("SELECT a /* one */ FROM t;", "SELECT a FROM t; -- different"));
        Assert.True(Equivalent("SELECT a FROM t;", "-- added\nSELECT a FROM t;"));
    }

    // --- what it must reject -------------------------------------------------------

    [Fact]
    public void IdentifierCaseChangeIsRejected()
    {
        // Under a case-sensitive collation these are different objects, so this is a semantic
        // change however harmless it looks.
        Assert.False(Equivalent("SELECT Amount FROM t;", "SELECT amount FROM t;"));
        Assert.False(Equivalent("SELECT [Foo] FROM t;", "SELECT [foo] FROM t;"));
    }

    [Fact]
    public void StringLiteralChangeIsRejected()
    {
        Assert.False(Equivalent("SELECT 'Yes';", "SELECT 'yes';"));
        Assert.False(Equivalent("SELECT 'a';", "SELECT 'b';"));
        Assert.False(Equivalent("SELECT N'a';", "SELECT 'a';"));
    }

    [Fact]
    public void OperatorChangeIsRejected()
    {
        // The failure a node-type fingerprint would miss: both sides are a
        // BooleanComparisonExpression with identical children.
        Assert.False(Equivalent("SELECT a FROM t WHERE x = 1;", "SELECT a FROM t WHERE x > 1;"));
        Assert.False(Equivalent("SELECT a FROM t WHERE x <> 1;", "SELECT a FROM t WHERE x = 1;"));
    }

    [Fact]
    public void LogicalOperatorSwapIsRejected()
    {
        Assert.False(Equivalent("SELECT a FROM t WHERE x = 1 AND y = 2;", "SELECT a FROM t WHERE x = 1 OR y = 2;"));
    }

    [Fact]
    public void NumericLiteralChangeIsRejected()
    {
        Assert.False(Equivalent("SELECT 1;", "SELECT 2;"));
        Assert.False(Equivalent("SELECT 1.50;", "SELECT 1.5;"));
    }

    [Fact]
    public void DroppedPredicateIsRejected()
    {
        Assert.False(Equivalent(
            "SELECT a FROM t WHERE x = 1 AND y = 2;",
            "SELECT a FROM t WHERE x = 1;"));
    }

    [Fact]
    public void DroppedColumnIsRejected()
    {
        Assert.False(Equivalent("SELECT a, b, c FROM t;", "SELECT a, b FROM t;"));
    }

    [Fact]
    public void AddedTokenIsRejected()
    {
        Assert.False(Equivalent("SELECT a FROM t;", "SELECT a FROM t WHERE 1 = 1;"));
        Assert.False(Equivalent("SELECT a FROM t;", "SELECT DISTINCT a FROM t;"));
    }

    [Fact]
    public void ReorderedColumnsAreRejected()
    {
        Assert.False(Equivalent("SELECT a, b FROM t;", "SELECT b, a FROM t;"));
    }

    [Fact]
    public void ChangedJoinTypeIsRejected()
    {
        Assert.False(Equivalent(
            "SELECT a FROM t INNER JOIN u ON u.i = t.i;",
            "SELECT a FROM t LEFT JOIN u ON u.i = t.i;"));
    }

    [Fact]
    public void RemovedSemicolonIsRejected()
    {
        // Not semantically meaningful in most contexts, but the formatter does not remove them,
        // so a removal would be an unrequested change and is treated as a defect.
        Assert.False(Equivalent("SELECT a FROM t;", "SELECT a FROM t"));
    }

    [Fact]
    public void ChangedParenthesisationIsRejected()
    {
        Assert.False(Equivalent(
            "SELECT a FROM t WHERE (x = 1 OR y = 2) AND z = 3;",
            "SELECT a FROM t WHERE x = 1 OR y = 2 AND z = 3;"));
    }

    // --- diagnostics ---------------------------------------------------------------

    [Fact]
    public void DiagnosticPointsAtTheInputLineAndColumn()
    {
        RoundTripVerifier.Verify(
            ParseOk("SELECT a\nFROM t\nWHERE Amount = 1;"),
            ParseOk("SELECT a\nFROM t\nWHERE amount = 1;"),
            out var diagnostic,
            NoClaims);

        Assert.Contains("line 3", diagnostic, StringComparison.Ordinal);
        Assert.Contains("Amount", diagnostic, StringComparison.Ordinal);
        Assert.Contains("amount", diagnostic, StringComparison.Ordinal);
        Assert.Contains("maxdop bug", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessProducesAnEmptyDiagnostic()
    {
        Assert.True(RoundTripVerifier.Verify(ParseOk("SELECT 1;"), ParseOk("select 1;"), out var diagnostic, NoClaims));
        Assert.Equal(string.Empty, diagnostic);
    }

    [Fact]
    public void VerifyRejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => RoundTripVerifier.Verify(null!, ParseOk("SELECT 1;"), out _, NoClaims));
        Assert.Throws<ArgumentNullException>(() => RoundTripVerifier.Verify(ParseOk("SELECT 1;"), null!, out _, NoClaims));
    }

    // --- systematic mutation sweeps ------------------------------------------------

    private const string Representative =
        "SELECT a, b FROM dbo.t AS x WHERE a = 1 AND b <> 'y' ORDER BY a DESC;";

    /// <summary>
    /// Runs a mutation sweep and reports how the damage was caught.
    /// </summary>
    /// <remarks>
    /// Splitting the two mechanisms apart matters for honesty. Many mutations make the text
    /// unparseable, and those are caught by the parse gate before verification is ever reached —
    /// so a sweep that only asserted "caught somehow" could pass with a verifier that did
    /// nothing. Every mutation that still parses is a real test of the verifier, and the caller
    /// asserts that enough of them exist for the sweep to mean something.
    /// </remarks>
    private static (int CaughtByParser, int CaughtByVerifier) Sweep(
        string original,
        IEnumerable<(string Damaged, string What)> mutations)
    {
        var reference = ParseOk(original);
        var caughtByParser = 0;
        var caughtByVerifier = 0;

        foreach (var (damaged, what) in mutations)
        {
            var reparsed = Parse(damaged, out var errors);
            if (errors.Count > 0)
            {
                caughtByParser++;
                continue;
            }

            Assert.False(
                RoundTripVerifier.Verify(reference, reparsed, out _, NoClaims),
                $"{what} still parsed and went undetected by the verifier");
            caughtByVerifier++;
        }

        return (caughtByParser, caughtByVerifier);
    }

    private static List<TSqlParserToken> SignificantTokens(string sql) =>
        [.. ParseOk(sql).ScriptTokenStream.Where(t =>
            t.TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.EndOfFile
                or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment))];

    [Fact]
    public void DeletingAnySingleTokenIsCaught()
    {
        var tokens = SignificantTokens(Representative);
        Assert.NotEmpty(tokens);

        var (byParser, byVerifier) = Sweep(
            Representative,
            tokens.Select(t => (
                Representative.Remove(t.Offset, t.Text.Length),
                $"deleting {t.TokenType} \"{t.Text}\" at offset {t.Offset}")));

        Assert.Equal(tokens.Count, byParser + byVerifier);

        // Most deletions produce a syntax error, which the parse gate handles. The sweep is only
        // evidence about the verifier for the ones that still parse, so require some.
        Assert.True(byVerifier >= 3, $"only {byVerifier} deletions still parsed; sweep barely exercised the verifier");
    }

    [Fact]
    public void DuplicatingAnySingleTokenIsCaught()
    {
        var tokens = SignificantTokens(Representative);

        var (byParser, byVerifier) = Sweep(
            Representative,
            tokens.Select(t => (
                Representative.Insert(t.Offset, t.Text + " "),
                $"duplicating {t.TokenType} \"{t.Text}\" at offset {t.Offset}")));

        Assert.Equal(tokens.Count, byParser + byVerifier);
        Assert.True(byVerifier >= 3, $"only {byVerifier} duplications still parsed; sweep barely exercised the verifier");
    }

    [Fact]
    public void RenamingAnyIdentifierIsCaughtByTheVerifierAlone()
    {
        var tokens = SignificantTokens(Representative)
            .Where(t => t.TokenType == TSqlTokenType.Identifier)
            .ToList();

        Assert.NotEmpty(tokens);

        var (byParser, byVerifier) = Sweep(
            Representative,
            tokens.Select(t => (
                Representative.Remove(t.Offset, t.Text.Length).Insert(t.Offset, "zzz"),
                $"renaming identifier \"{t.Text}\" at offset {t.Offset}")));

        // Renaming an identifier always leaves valid SQL, so the parse gate never fires here.
        // Every one of these is the verifier doing the work.
        Assert.Equal(0, byParser);
        Assert.Equal(tokens.Count, byVerifier);
    }

    [Fact]
    public void RecasingAnyIdentifierIsCaughtByTheVerifierAlone()
    {
        var tokens = SignificantTokens(Representative)
            .Where(t => t.TokenType == TSqlTokenType.Identifier && t.Text.Any(char.IsLower))
            .ToList();

        Assert.NotEmpty(tokens);

        var (byParser, byVerifier) = Sweep(
            Representative,
            tokens.Select(t => (
                Representative.Remove(t.Offset, t.Text.Length).Insert(t.Offset, t.Text.ToUpperInvariant()),
                $"recasing identifier \"{t.Text}\" at offset {t.Offset}")));

        Assert.Equal(0, byParser);
        Assert.Equal(tokens.Count, byVerifier);
    }

    [Fact]
    public void RecasingAnyKeywordIsPermitted()
    {
        // The complement of the sweeps above: keyword recasing must never be flagged, or the
        // formatter would refuse to do its own job on lowercase input.
        var lowered = Representative.Replace("SELECT", "select", StringComparison.Ordinal)
            .Replace("FROM", "from", StringComparison.Ordinal)
            .Replace("WHERE", "where", StringComparison.Ordinal)
            .Replace("AND", "and", StringComparison.Ordinal)
            .Replace("ORDER BY", "order by", StringComparison.Ordinal)
            .Replace("DESC", "desc", StringComparison.Ordinal)
            .Replace(" AS ", " as ", StringComparison.Ordinal);

        Assert.True(Equivalent(Representative, lowered));
    }

    // --- integration with the formatter -------------------------------------------

    [Fact]
    public void FormatterOutputAlwaysPassesItsOwnVerification()
    {
        // Every fixture that formats successfully has, by construction, already passed the
        // verifier inside SqlFormatter. This asserts it independently rather than trusting that.
        string[] inputs =
        [
            TestFiles.Read("gnarly.sql"),
            Representative,
            "with c as (select a from t) select a from c;",
            "select case when a < 0 then 'x' else 'y' end from t;",
            "select a from t union all select b from u;",
            "create procedure dbo.p @a int as begin select 1; end",
        ];

        foreach (var input in inputs)
        {
            var result = SqlFormatter.Format(input);
            Assert.Equal(FormatStatus.Formatted, result.Status);

            // Re-runs the printer rather than passing NoClaims, because real output legitimately
            // recases non-reserved words like `int` and `NVARCHAR`. That makes this a check on the
            // claims as well: if the printer recased an identifier it did not record, the raw
            // comparison here still fails.
            var root = ParseOk(input);
            var printer = new SqlPrinter(root, CommentAttacher.Attach(root), FormatOptions.Default);
            _ = printer.Print(root);

            Assert.True(
                RoundTripVerifier.Verify(root, ParseOk(result.Output), out var diagnostic, printer.KeywordCasedTokens),
                diagnostic);
        }
    }

    [Fact]
    public void RefusalReturnsTheInputByteForByte()
    {
        // The contract callers depend on: Output is always safe to write.
        const string sql = "SELECT FROM WHERE ((( ;";
        var result = SqlFormatter.Format(sql);

        Assert.NotEqual(FormatStatus.Formatted, result.Status);
        Assert.Equal(sql, result.Output);
        Assert.False(result.Changed);
    }
}
