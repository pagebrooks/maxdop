using System.Text;

namespace Maxdop.Cli.Tests;

/// <summary>
/// Turning the paths a caller gave into the files that actually get checked.
/// </summary>
/// <remarks>
/// The regression that motivates most of this: <c>maxdop --check src/**/*.sql</c> was the documented
/// command, and in a default bash <c>**</c> collapses to one level, so files two directories down
/// were never examined and CI passed anyway. Anything here that quietly returns fewer files than it
/// should reproduces that, so the assertions are about the exact set, never just the count.
/// </remarks>
public sealed class DiscoveryTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("maxdop-discovery-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, string content = "select 1")
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private void Config(string json) => File.WriteAllText(Path.Combine(_root, ".maxdop.json"), json);

    private static PathExpander Expand(params string[] paths)
    {
        var expander = new PathExpander(new ExcludeRules(null));

        foreach (var path in paths)
        {
            Assert.True(expander.TryAdd(path, named: true, out var error), error);
        }

        expander.Finish();
        return expander;
    }

    /// <summary>Discovered paths as repo-relative, forward-slashed strings, for readable assertions.</summary>
    private string[] Relative(PathExpander expander) =>
        [.. expander.Files
            .Select(f => Path.GetRelativePath(_root, Path.GetFullPath(f)).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)];

    // --- directory recursion --------------------------------------------------------------

    [Fact]
    public void ADirectoryIsSearchedToTheBottom()
    {
        Write("src/top.sql");
        Write("src/a/mid.sql");
        Write("src/a/b/deep.sql");

        // The exact failure of `src/**/*.sql` under a default bash was to return only src/a/mid.sql.
        Assert.Equal(
            ["src/a/b/deep.sql", "src/a/mid.sql", "src/top.sql"],
            Relative(Expand(Path.Combine(_root, "src"))));
    }

    [Fact]
    public void OnlySqlFilesAreCollected()
    {
        Write("src/a.sql");
        Write("src/readme.md");
        Write("src/b.txt");

        Assert.Equal(["src/a.sql"], Relative(Expand(Path.Combine(_root, "src"))));
    }

    [Fact]
    public void ExtensionMatchingIgnoresCase()
    {
        Write("src/a.SQL");

        // A repository must not check differently because CI runs on Linux and the author on Windows.
        Assert.Equal(["src/a.SQL"], Relative(Expand(Path.Combine(_root, "src"))));
    }

    [Fact]
    public void DotDirectoriesAreSkippedOnEveryPlatform()
    {
        Write("src/a.sql");
        Write(".git/hooks/x.sql");

        // This directory is created by Directory.CreateDirectory, so it has no hidden attribute —
        // which is the whole point. Relying on FileAttributes.Hidden passed here on Linux, where
        // .NET calls any dot-name hidden, and failed on Windows, where the attribute has to have
        // been set. Real git sets it, so the bug hid behind that and surfaced only in CI.
        Assert.Equal(["src/a.sql"], Relative(Expand(_root)));
    }

    [Fact]
    public void ADirectoryThatIsMerelyNoisyIsNotSkipped()
    {
        // node_modules is not dot-prefixed and maxdop does not keep a list of directory names it
        // dislikes: guessing would eventually guess wrong about somebody's layout. `exclude` in
        // .maxdop.json is the place to say so, and it is per-repository for that reason.
        Write("src/a.sql");
        Write("node_modules/pkg/y.sql");

        Assert.Equal(["node_modules/pkg/y.sql", "src/a.sql"], Relative(Expand(_root)));
    }

    [Fact]
    public void OverlappingArgumentsFormatAFileOnce()
    {
        var file = Write("src/a.sql");

        // Under --write this would otherwise write the same file twice, and under --check report it
        // twice, both from a perfectly reasonable `maxdop --check src/ src/a.sql`.
        Assert.Single(Expand(Path.Combine(_root, "src"), file).Files);
    }

    [Fact]
    public void ResultsAreSortedSoTwoRunsAgree()
    {
        Write("src/c.sql");
        Write("src/a.sql");
        Write("src/b.sql");

        var files = Expand(Path.Combine(_root, "src")).Files;
        Assert.Equal(files.OrderBy(f => f, StringComparer.Ordinal), files);
    }

    [Fact]
    public void AMissingPathIsAnError()
    {
        var expander = new PathExpander(new ExcludeRules(null));

        Assert.False(expander.TryAdd(Path.Combine(_root, "nope.sql"), named: true, out var error));
        Assert.Contains("no such file or directory", error);
    }

    [Fact]
    public void AnEmptyDirectoryIsNotAnError()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty"));

        Assert.Empty(Expand(Path.Combine(_root, "empty")).Files);
    }

    // --- exclusion ------------------------------------------------------------------------

    [Fact]
    public void ExcludedFilesAreDroppedFromAWalkAndCounted()
    {
        Config("""{ "exclude": ["src/generated/**"] }""");
        Write("src/a.sql");
        Write("src/generated/g.sql");

        var expander = Expand(Path.Combine(_root, "src"));

        Assert.Equal(["src/a.sql"], Relative(expander));
        Assert.Equal(1, expander.ExcludedCount);
    }

    [Fact]
    public void ExclusionAppliesToAFileNamedDirectly()
    {
        // One rule for every source of paths. The alternative — exempting argv — means the same
        // repository answers differently depending on how the file arrived.
        Config("""{ "exclude": ["vendor/"] }""");
        var file = Write("vendor/third.sql");

        var expander = Expand(file);

        Assert.Empty(expander.Files);
        Assert.Equal(1, expander.ExcludedCount);
    }

    [Fact]
    public void PatternsAreRelativeToTheConfigNotTheWorkingDirectory()
    {
        // Run from anywhere, mean the same thing: this is why an editor invoking maxdop from the
        // user's home directory still honours the repository's exclusions.
        Config("""{ "exclude": ["src/generated/**"] }""");
        Write("src/generated/g.sql");
        Write("src/a.sql");

        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Path.GetTempPath());
            Assert.Equal(["src/a.sql"], Relative(Expand(Path.Combine(_root, "src"))));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public void ABrokenConfigStopsTheRunRatherThanExcludingNothing()
    {
        // Treating an unreadable config as "no exclusions" would format the files it was written to
        // protect, which is the expensive direction to be wrong in.
        Config("{ not json");
        Write("src/a.sql");

        var expander = new PathExpander(new ExcludeRules(null));

        Assert.False(expander.TryAdd(Path.Combine(_root, "src"), named: true, out var error));
        Assert.Contains("not valid JSON", error);
    }

    [Fact]
    public void NoConfigMeansNoExclusions()
    {
        Write("src/a.sql");
        Write("src/generated/g.sql");

        Assert.Equal(2, Expand(Path.Combine(_root, "src")).Files.Count);
    }

    // --- --files-from ---------------------------------------------------------------------

    [Fact]
    public void AListIsSplitOnNewlines()
    {
        var list = Path.Combine(_root, "list.txt");
        File.WriteAllText(list, "a.sql\nb/c.sql\n");

        Assert.True(FileList.TryRead(list, out var files, out var error), error);
        Assert.Equal(["a.sql", "b/c.sql"], files);
    }

    [Fact]
    public void AListContainingNulIsSplitOnNulInstead()
    {
        // What `git diff -z` and `find -print0` produce. Detected rather than configured, so the
        // safe form needs no extra maxdop flag — and a filename containing a newline survives it.
        var list = Path.Combine(_root, "list.bin");
        File.WriteAllBytes(list, Encoding.UTF8.GetBytes("a.sql\0odd\nname.sql\0"));

        Assert.True(FileList.TryRead(list, out var files, out var error), error);
        Assert.Equal(["a.sql", "odd\nname.sql"], files);
    }

    [Fact]
    public void CarriageReturnsAndBlankLinesAreIgnored()
    {
        var list = Path.Combine(_root, "list.txt");
        File.WriteAllText(list, "a.sql\r\n\r\nb.sql\r\n");

        Assert.True(FileList.TryRead(list, out var files, out var error), error);
        Assert.Equal(["a.sql", "b.sql"], files);
    }

    [Fact]
    public void AnEmptyListIsNotAnError()
    {
        // The commonest case in CI by a distance: a pull request that changed no SQL at all.
        var list = Path.Combine(_root, "list.txt");
        File.WriteAllText(list, string.Empty);

        Assert.True(FileList.TryRead(list, out var files, out var error), error);
        Assert.Empty(files);
    }

    [Fact]
    public void AMissingListIsAnError()
    {
        Assert.False(FileList.TryRead(Path.Combine(_root, "nope.txt"), out _, out var error));
        Assert.Contains("--files-from", error);
    }
}
