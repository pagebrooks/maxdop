using Maxdop.Core.Formatting;
using Maxdop.Core.Printing;

namespace Maxdop.Core.Tests;

/// <summary>
/// Runs every file in <c>tests/corpus</c> through the formatter and asserts the three invariants.
/// </summary>
/// <remarks>
/// <para>The third-party corpus under <c>./corpus</c> is fetched, gitignored and never committed, so CI
/// cannot depend on it — and it has a shape: First Responder Kit and Ola Hallengren are stored procedures
/// almost exclusively, and ScriptDom's own test suite is one file per grammar feature. Between them they
/// contain remarkably little <em>plain application SQL</em>, which is how a views-and-functions file once
/// measured 2.5% while the corpus reported 99.2%.</para>
/// <para><c>tests/corpus</c> is the answer to that: one hand-written file per construct family, covering
/// at least one example of each — SELECT in all its shapes, INSERT/UPDATE/DELETE/MERGE, permanent and
/// temporary tables, table variables, the set operators, cursors, user-defined types, functions,
/// procedures, views, triggers, control flow, transactions, indexes, dynamic SQL, expressions and
/// permissions. Written from the documented syntax rather than copied from anywhere, so the repo stays
/// clean-room.</para>
/// <para>This is a gate, not a benchmark: it asserts that every file formats, that the result is a fixed
/// point, and that no comment is lost. Coverage is measured separately, by the harness in
/// <c>tools/Maxdop.Corpus</c>.</para>
/// </remarks>
public class ConstructCorpusTests
{
    public static TheoryData<string> CorpusFiles
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in Directory.EnumerateFiles(CorpusDirectory, "*.sql").OrderBy(p => p, StringComparer.Ordinal))
            {
                data.Add(Path.GetFileName(path));
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void EveryConstructFileFormats(string name)
    {
        var sql = File.ReadAllText(Path.Combine(CorpusDirectory, name));
        var result = SqlFormatter.Format(sql, Options);

        // Anything but Formatted means the file either did not parse — which would be a defect in the
        // corpus file, not the formatter — or the formatter declined its own output.
        Assert.Equal(FormatStatus.Formatted, result.Status);
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void EveryConstructFileIsAFixedPoint(string name)
    {
        var sql = File.ReadAllText(Path.Combine(CorpusDirectory, name));

        var once = SqlFormatter.Format(sql, Options);
        Assert.Equal(FormatStatus.Formatted, once.Status);

        var twice = SqlFormatter.Format(once.Output, Options);
        Assert.Equal(FormatStatus.Formatted, twice.Status);
        Assert.Equal(once.Output, twice.Output);
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void EveryConstructFileKeepsItsComments(string name)
    {
        var sql = File.ReadAllText(Path.Combine(CorpusDirectory, name));
        var result = SqlFormatter.Format(sql, Options);

        Assert.Equal(FormatStatus.Formatted, result.Status);
        Assert.Equal(CountComments(sql), CountComments(result.Output));
    }

    [Fact]
    public void TheCorpusCoversTheConstructFamiliesItClaimsTo()
    {
        // Named explicitly so deleting a file is a test failure rather than a silent loss of coverage.
        string[] expected =
        [
            "controlflow", "cursors", "delete", "dynamicsql", "expressions", "functions", "indexes",
            "insert", "merge", "procedures", "security", "select", "setops", "tables", "transactions",
            "triggers", "types", "update", "variables", "views",
        ];

        var actual = Directory.EnumerateFiles(CorpusDirectory, "*.sql")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    private static FormatOptions Options => FormatOptions.Default;

    private static string CorpusDirectory => Path.Combine(AppContext.BaseDirectory, "corpus");

    /// <summary>
    /// Counts comment tokens the way the preservation gate does, so a miscount here would be a bug in
    /// the test rather than a disagreement with the formatter.
    /// </summary>
    private static int CountComments(string sql)
    {
        using var reader = new StringReader(sql);
        var parser = new Microsoft.SqlServer.TransactSql.ScriptDom.TSql180Parser(initialQuotedIdentifiers: false);
        var fragment = parser.Parse(reader, out var errors);

        Assert.Empty(errors);

        return fragment.ScriptTokenStream!.Count(t =>
            t.TokenType is Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.SingleLineComment
                or Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.MultilineComment);
    }
}
