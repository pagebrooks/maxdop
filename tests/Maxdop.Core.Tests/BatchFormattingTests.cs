using Maxdop.Core.Formatting;

namespace Maxdop.Core.Tests;

/// <summary>
/// Formatting a file one <c>GO</c>-separated batch at a time, when it does not parse as a whole.
/// </summary>
/// <remarks>
/// The case this exists for is migrations: multi-batch by necessity, and routinely carrying one
/// batch of sqlcmd syntax that no T-SQL parser accepts. Before this, that one batch cost the whole
/// file its formatting.
/// </remarks>
public class BatchFormattingTests
{
    private static FormatResult Format(string sql, FormatOptions? options = null) =>
        SqlFormatter.Format(sql, options ?? FormatOptions.Default);

    [Fact]
    public void OneUnparseableBatchNoLongerCostsTheOthersTheirFormatting()
    {
        var result = Format(
            "create table dbo.A (id int);\nGO\nraiserror 50001 'legacy';\nGO\ncreate table dbo.B (id int);\nGO\n");

        // Exit code 1 still: the input has a problem and a human should look at it. What changed is
        // that the file is no longer left untouched while they do.
        Assert.Equal(FormatStatus.ParseFailed, result.Status);
        Assert.True(result.Changed);
        Assert.Equal(
            """
            CREATE TABLE dbo.A (
                id INT
            );
            GO
            raiserror 50001 'legacy';
            GO
            CREATE TABLE dbo.B (
                id INT
            );
            GO

            """,
            result.Output);
    }

    [Fact]
    public void TheUnparseableBatchIsCopiedThroughByteForByte()
    {
        const string odd = "raiserror 50001    'legacy'   ;";
        var result = Format($"select 1;\nGO\n{odd}\nGO\n");

        Assert.Contains(odd, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlcmdMigrationsFormatEverythingButTheSqlcmdBatch()
    {
        // `:setvar` is the reason this feature exists. It is not T-SQL and never will be, and it
        // used to take the whole migration down with it.
        var result = Format(":setvar DatabaseName \"Prod\"\nGO\ncreate table dbo.C (id int);\nGO\n");

        Assert.Equal(FormatStatus.ParseFailed, result.Status);
        Assert.StartsWith(":setvar DatabaseName \"Prod\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE dbo.C (", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ABatchRepeatCountMakesTheFileFormatCompletely()
    {
        // `GO 5` is a sqlcmd client feature, not T-SQL, so the whole-file parse fails on it — but
        // every batch either side is fine. Nothing is left unformatted, so this is a plain success
        // rather than "look at your file".
        var result = Format("select 1;\nGO 5\nselect 2;\n");

        Assert.Equal(FormatStatus.Formatted, result.Status);
        Assert.Equal("SELECT 1;\nGO 5\nSELECT 2;\n", result.Output);
    }

    [Fact]
    public void TheSeparatorKeywordFollowsTheConfiguredCase()
    {
        Assert.Contains("\ngo\n", Format(
            "select 1;\nGO\n:setvar x \"y\"\nGO\n",
            FormatOptions.Default with { KeywordCase = KeywordCase.Lower }).Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BlankLinesAroundASeparatorAreLeftExactlyAsWritten()
    {
        // The whitespace either side of a separator belongs to the separator, not to a batch, so no
        // layout decision is ever applied to it.
        var result = Format("select 1;\n\n\nGO\n\n\n:setvar x \"y\"\nGO\n");

        Assert.Contains("SELECT 1;\n\n\nGO\n\n\n:setvar", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void GoInsideAStringIsNotABatchSeparator()
    {
        // Batch boundaries come from the lexer — the same one the parser uses — so this needs no
        // rule of its own here.
        var result = Format("select 'GO' as x;\nGO\n:setvar a \"b\"\nGO\n");

        Assert.Contains("SELECT 'GO' AS x;", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ACommentBetweenBatchesSurvives()
    {
        var result = Format("select 1;\nGO\n-- between batches\n:setvar a \"b\"\nGO\n");

        Assert.Contains("-- between batches", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ASingleUnparseableBatchIsStillReturnedUntouched()
    {
        // Nothing to split, so nothing to gain: the file comes back exactly as it was, which is the
        // behaviour that predates batching.
        const string sql = "raiserror 50001 'legacy';\n";
        var result = Format(sql);

        Assert.Equal(FormatStatus.ParseFailed, result.Status);
        Assert.False(result.Changed);
        Assert.Equal(sql, result.Output);
    }

    [Fact]
    public void AFileWhereNoBatchParsesIsReturnedUntouched()
    {
        const string sql = ":setvar a \"b\"\nGO\n:setvar c \"d\"\nGO\n";
        var result = Format(sql);

        Assert.Equal(FormatStatus.ParseFailed, result.Status);
        Assert.False(result.Changed);
        Assert.Equal(sql, result.Output);
    }

    [Fact]
    public void BatchFormattingIsAFixedPoint()
    {
        var once = Format("create table dbo.A (id int);\nGO\n:setvar a \"b\"\nGO\ncreate table dbo.B (id int);\nGO\n");
        var twice = Format(once.Output);

        Assert.Equal(once.Output, twice.Output);
    }

    [Fact]
    public void AFileThatParsesWholeNeverTakesTheBatchPath()
    {
        // The guarantee that makes this change safe to add: everything that formats today is
        // untouched by it. Same output as a single-batch equivalent, with GO handled as always.
        var result = Format("select 1;\nGO\nselect 2;\nGO\n");

        Assert.Equal(FormatStatus.Formatted, result.Status);
        Assert.Equal("SELECT 1;\nGO\nSELECT 2;\nGO\n", result.Output);
    }

    [Theory]
    [InlineData("SELECT 1;\nGO\nGO\nnot valid sql (")]
    [InlineData("SELECT 1;\nGO\n\nGO\nnot valid sql (")]
    [InlineData("GO\nGO\nGO\nnot valid sql (")]
    public void ConsecutiveSeparatorsDoNotCrashTheSplitter(string sql)
    {
        // Both separators claimed the whitespace between them — the first going forward, the second
        // going back — so the second piece started before the cursor and sliced backwards. An
        // ArgumentOutOfRangeException, out of the one component whose promise is not doing that.
        // Needs a file that does not parse as a whole, since that is the only thing that engages the
        // batch splitter at all; AdventureWorks and Northwind's install scripts both trip it.
        var result = SqlFormatter.Format(sql);

        Assert.NotEqual(FormatStatus.Refused, result.Status);
        Assert.Equal(
            sql.Count(c => c == 'G'),
            result.Output.Count(c => c == 'G'));
    }
}
