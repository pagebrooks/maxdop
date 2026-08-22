using System.Text;

namespace Maxdop.Cli.Tests;

/// <summary>
/// The ratchet that lets an existing codebase adopt a formatter without one enormous commit.
/// </summary>
/// <remarks>
/// The property that has to hold is one-directional: a baseline may forgive a file that is unchanged
/// since it was recorded, and must stop forgiving it the moment anyone edits it. A baseline that kept
/// forgiving an edited file would be an exclusion list wearing a ratchet's name, and the count would
/// never go down.
/// </remarks>
public sealed class BaselineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("maxdop-baseline-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Path_(string relative) =>
        System.IO.Path.Combine(_root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private string Write(string relative, string content)
    {
        var path = Path_(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string BaselineFile => Path_(".maxdop-baseline");

    private static byte[] Bytes(string path) => File.ReadAllBytes(path);

    private Baseline Load()
    {
        Assert.True(Baseline.TryLoad(BaselineFile, out var baseline, out var error), error);
        return baseline;
    }

    // --- the ratchet ----------------------------------------------------------------------

    [Fact]
    public void AnUnchangedFileStaysCovered()
    {
        var file = Write("src/a.sql", "select    1");
        Assert.True(Baseline.TryWrite(BaselineFile, [(file, Bytes(file))], out var error), error);

        Assert.True(Load().Covers(file, Bytes(file)));
    }

    [Fact]
    public void EditingAFileEndsItsExemption()
    {
        var file = Write("src/a.sql", "select    1");
        Assert.True(Baseline.TryWrite(BaselineFile, [(file, Bytes(file))], out var error), error);

        File.WriteAllText(file, "select    1, 2");

        // The whole point. Touch it and you own it.
        Assert.False(Load().Covers(file, Bytes(file)));
    }

    [Fact]
    public void AFileThatWasNeverRecordedIsNotCovered()
    {
        var recorded = Write("src/a.sql", "select    1");
        var other = Write("src/b.sql", "select    2");
        Assert.True(Baseline.TryWrite(BaselineFile, [(recorded, Bytes(recorded))], out var error), error);

        Assert.False(Load().Covers(other, Bytes(other)));
    }

    [Fact]
    public void EntriesNothingAskedAboutAreReportedAsUnused()
    {
        var a = Write("src/a.sql", "select    1");
        var b = Write("src/b.sql", "select    2");
        Assert.True(Baseline.TryWrite(BaselineFile, [(a, Bytes(a)), (b, Bytes(b))], out var error), error);

        var baseline = Load();
        baseline.Covers(a, Bytes(a));

        // b was never consulted — it has been formatted or deleted since. Harmless, but the list has
        // to shrink or it stops meaning anything.
        Assert.Single(baseline.Unused);
        Assert.Contains("src/b.sql", baseline.Unused);
    }

    // --- the file itself ------------------------------------------------------------------

    [Fact]
    public void AMissingBaselineLoadsAsEmptyRatherThanFailing()
    {
        // So the first `--check --baseline` in a repository behaves exactly like a run without one.
        Assert.True(Baseline.TryLoad(BaselineFile, out var baseline, out var error), error);
        Assert.Null(error);
        Assert.Equal(0, baseline.Count);
    }

    [Fact]
    public void EntriesAreSortedByPathSoRegeneratingProducesAReadableDiff()
    {
        // Sorting the composed "<hash>  <path>" line instead would sort by hash, and one edited file
        // would reshuffle the entire baseline and bury the real change in the review.
        var c = Write("src/c.sql", "select    3");
        var a = Write("src/a.sql", "select    1");
        var b = Write("src/b.sql", "select    2");

        Assert.True(
            Baseline.TryWrite(BaselineFile, [(c, Bytes(c)), (a, Bytes(a)), (b, Bytes(b))], out var error),
            error);

        var paths = File.ReadAllLines(BaselineFile)
            .Where(l => !l.StartsWith('#') && l.Length > 0)
            .Select(l => l[(l.IndexOf("  ", StringComparison.Ordinal) + 2)..]);

        Assert.Equal(["src/a.sql", "src/b.sql", "src/c.sql"], paths);
    }

    [Fact]
    public void PathsAreStoredWithForwardSlashes()
    {
        // A baseline written on Windows has to match on a Linux runner, or the gate is per-platform.
        var file = Write("db/dbo/a.sql", "select    1");
        Assert.True(Baseline.TryWrite(BaselineFile, [(file, Bytes(file))], out var error), error);

        Assert.Contains("  db/dbo/a.sql", File.ReadAllText(BaselineFile));
        Assert.DoesNotContain('\\', File.ReadAllText(BaselineFile));
    }

    [Fact]
    public void TheFileUsesLineFeedsOnEveryPlatform()
    {
        var file = Write("src/a.sql", "select    1");
        Assert.True(Baseline.TryWrite(BaselineFile, [(file, Bytes(file))], out var error), error);

        // Committed file: if regeneration flipped line endings it would conflict with itself.
        Assert.DoesNotContain('\r', File.ReadAllText(BaselineFile));
    }

    [Fact]
    public void ItIsReadableBySha256sum()
    {
        var file = Write("src/a.sql", "select    1");
        Assert.True(Baseline.TryWrite(BaselineFile, [(file, Bytes(file))], out var error), error);

        var entry = File.ReadAllLines(BaselineFile).First(l => !l.StartsWith('#'));
        var hash = entry[..entry.IndexOf("  ", StringComparison.Ordinal)];

        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(char.IsAsciiDigit(c) || (c is >= 'a' and <= 'f')));
    }

    [Fact]
    public void CommentsAndBlankLinesAreIgnoredWhenReading()
    {
        var file = Write("src/a.sql", "select    1");
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Bytes(file)));
        File.WriteAllText(BaselineFile, $"# a note\n\n{hash}  src/a.sql\n\n");

        Assert.True(Load().Covers(file, Bytes(file)));
    }

    [Fact]
    public void APathContainingSpacesSurvivesTheRoundTrip()
    {
        var file = Write("src/two words.sql", "select    1");
        Assert.True(Baseline.TryWrite(BaselineFile, [(file, Bytes(file))], out var error), error);

        Assert.True(Load().Covers(file, Bytes(file)));
    }

    [Fact]
    public void AMalformedLineIsReportedWithItsNumber()
    {
        File.WriteAllText(BaselineFile, "# fine\nthis is not an entry\n");

        Assert.False(Baseline.TryLoad(BaselineFile, out _, out var error));
        Assert.Contains("line 2", error);
    }

    [Fact]
    public void HashIsOfBytesNotDecodedText()
    {
        // Two encodings of the same text are different files as far as a byte-level tool is
        // concerned, and re-encoding one is an edit that should end its exemption.
        var file = Path_("src/a.sql");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, Encoding.UTF8.GetBytes("select    1"));
        Assert.True(Baseline.TryWrite(BaselineFile, [(file, Bytes(file))], out var error), error);

        File.WriteAllBytes(file, Encoding.Unicode.GetBytes("select    1"));
        Assert.False(Load().Covers(file, Bytes(file)));
    }
}
