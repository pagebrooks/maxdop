using System.Text;

namespace Maxdop.Cli.Tests;

/// <summary>
/// Replacing a file's contents without ever leaving it half-written.
/// </summary>
/// <remarks>
/// The failure this guards is narrow and severe: <c>File.WriteAllBytes</c> truncates before it
/// writes, so a write that does not finish destroys the file. Every safety gate in the formatter
/// exists to stop maxdop writing output it cannot prove correct, and none of them can see this one.
/// </remarks>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("maxdop-atomic-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Path_(string name) => System.IO.Path.Combine(_root, name);

    private string Existing(string name, string content = "SELECT 1;\n")
    {
        var path = Path_(name);
        File.WriteAllText(path, content);
        return path;
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private string[] StrayTempFiles() =>
        [.. Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories)];

    // --- the ordinary path --------------------------------------------------------------

    [Fact]
    public void ContentsAreReplaced()
    {
        var path = Existing("a.sql");

        AtomicFile.Write(path, Bytes("SELECT 2;\n"));

        Assert.Equal("SELECT 2;\n", File.ReadAllText(path));
    }

    [Fact]
    public void NoTemporaryFileIsLeftBehind()
    {
        var path = Existing("a.sql");

        AtomicFile.Write(path, Bytes("SELECT 2;\n"));

        // The temp lives in the target's own directory, because a rename is only atomic within one
        // filesystem. That makes leaving one behind a visible mess in the user's repository.
        Assert.Empty(StrayTempFiles());
        Assert.Single(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public void WritingAFileThatDoesNotExistYetWorks()
    {
        var path = Path_("new.sql");

        AtomicFile.Write(path, Bytes("SELECT 1;\n"));

        Assert.Equal("SELECT 1;\n", File.ReadAllText(path));
    }

    // --- metadata -----------------------------------------------------------------------

    [Fact]
    public void UnixModeIsCarriedAcross()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Existing("a.sql");
        const UnixFileMode Restricted = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(path, Restricted);

        AtomicFile.Write(path, Bytes("SELECT 2;\n"));

        // The temp file is created under the process umask, so without an explicit copy a 0640 file
        // comes back 0644 — the file still works and the permissions have quietly widened, which is
        // the kind of change nobody notices until it matters.
        Assert.Equal(Restricted, File.GetUnixFileMode(path));
    }

    // --- links --------------------------------------------------------------------------

    [Fact]
    public void WritingThroughASymlinkUpdatesTheTargetAndKeepsTheLink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var target = Existing("target.sql");
        var link = Path_("link.sql");
        File.CreateSymbolicLink(link, target);

        AtomicFile.Write(link, Bytes("SELECT 2;\n"));

        // PathExpander skips links while walking a directory, but a link named directly on the
        // command line never reaches that walk and does arrive here. Renaming over it would turn the
        // user's symlink into a regular file — a regression the old non-atomic write did not have.
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal("SELECT 2;\n", File.ReadAllText(target));
        Assert.Equal("SELECT 2;\n", File.ReadAllText(link));
    }

    [Fact]
    public void AChainOfSymlinksResolvesToTheFileAtTheEnd()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var target = Existing("target.sql");
        var middle = Path_("middle.sql");
        var outer = Path_("outer.sql");
        File.CreateSymbolicLink(middle, target);
        File.CreateSymbolicLink(outer, middle);

        AtomicFile.Write(outer, Bytes("SELECT 2;\n"));

        Assert.NotNull(new FileInfo(outer).LinkTarget);
        Assert.NotNull(new FileInfo(middle).LinkTarget);
        Assert.Equal("SELECT 2;\n", File.ReadAllText(target));
    }

    [Fact]
    public void HardLinksAreBrokenAndThatIsTheAcceptedTrade()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Existing("a.sql");
        var hard = Path_("hard.sql");
        Assert.Equal(0, RunHardLink(path, hard));

        AtomicFile.Write(path, Bytes("SELECT 2;\n"));

        // Pinning a known limitation rather than a desirable behaviour. POSIX has no way to replace a
        // file's contents atomically, so rename is the only option and it necessarily swaps the
        // directory entry to a new inode. Writing in place would keep the link but leave a mixture of
        // old and new bytes on a failed write, which for SQL is worse: a hybrid file may still parse
        // and mean something different. Git makes the same call.
        Assert.Equal("SELECT 2;\n", File.ReadAllText(path));
        Assert.Equal("SELECT 1;\n", File.ReadAllText(hard));
    }

    private static int RunHardLink(string from, string to)
    {
        using var process = System.Diagnostics.Process.Start("ln", [from, to])!;
        process.WaitForExit();
        return process.ExitCode;
    }

    // --- failure ------------------------------------------------------------------------

    [Fact]
    public void AFailedWriteLeavesTheOriginalIntact()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var directory = Directory.CreateDirectory(Path_("locked")).FullName;
        var path = System.IO.Path.Combine(directory, "a.sql");
        File.WriteAllText(path, "SELECT 1;\n");

        // Read and execute only: the file itself is still writable, but no new entry can be created
        // in the directory, so the temp file cannot be made.
        //
        // This case is a deliberate behaviour change, not a simulation of a full volume — on a full
        // volume both strategies fail. A writable file inside a directory the user cannot write to
        // formatted successfully before, because writing in place needs no directory entry, and now
        // it does not. Refusing is the answer consistent with the rest of the project: maxdop
        // declines rather than giving up a guarantee quietly, and the caller gets a clear message
        // instead of a file rewritten by the one path that can still truncate it.
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            Assert.ThrowsAny<Exception>(() => AtomicFile.Write(path, Bytes("SELECT 2;\n")));

            // The whole point: the file that could not be replaced was not damaged either.
            Assert.Equal("SELECT 1;\n", File.ReadAllText(path));
        }
        finally
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
