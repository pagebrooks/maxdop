using Maxdop.Core.Formatting;

namespace Maxdop.Core.Tests;

/// <summary>
/// Snapshot tests: format a fixture and compare against a committed expected output. The point
/// is that formatting changes show up as a reviewable diff rather than as a passing test suite.
/// </summary>
/// <remarks>
/// Set <c>MAXDOP_UPDATE_GOLDEN=1</c> to regenerate. A missing snapshot is written and the test
/// fails once, so a new golden always gets reviewed before it becomes the baseline.
/// </remarks>
public class GoldenTests
{
    private static readonly bool UpdateGoldens =
        Environment.GetEnvironmentVariable("MAXDOP_UPDATE_GOLDEN") is "1" or "true";

    [Theory]
    [InlineData("gnarly.sql")]
    public void FixtureFormatsToItsGolden(string name)
    {
        var input = TestFiles.Read(name);
        var result = SqlFormatter.Format(input);

        if (result.Status != FormatStatus.Formatted)
        {
            Assert.Fail($"{name}: {result.Status}: {string.Join(" | ", result.Diagnostics)}");
        }

        var directory = Path.Combine(TestFiles.SourceFixturesDirectory, "expected");
        Directory.CreateDirectory(directory);
        var goldenPath = Path.Combine(directory, name);

        if (UpdateGoldens || !File.Exists(goldenPath))
        {
            File.WriteAllText(goldenPath, result.Output);
            if (!UpdateGoldens)
            {
                Assert.Fail($"Golden for {name} did not exist and has been written to {goldenPath}. Review it, then re-run.");
            }

            return;
        }

        Assert.Equal(File.ReadAllText(goldenPath), result.Output);
    }

    [Theory]
    [InlineData("gnarly.sql")]
    public void FixtureIsIdempotent(string name)
    {
        var once = SqlFormatter.Format(TestFiles.Read(name));
        Assert.Equal(FormatStatus.Formatted, once.Status);

        var twice = SqlFormatter.Format(once.Output);
        Assert.Equal(FormatStatus.Formatted, twice.Status);
        Assert.Equal(once.Output, twice.Output);
    }
}
