using System.Text;
using Maxdop.Cli;
using Maxdop.Core.Formatting;
using Maxdop.Core.Text;

namespace Maxdop.Cli.Tests;

/// <summary>
/// The file-writing half of the CLI, which <see cref="SourceEncodingTests"/> in the core cannot
/// reach.
/// </summary>
/// <remarks>
/// The encoding layer is tested thoroughly one level down — detection, byte-identical round-trip,
/// BOM preservation — but every one of those tests works on a byte array. Nothing proved that
/// <c>--write</c> actually routes a file through it, and a formatter that silently converts SSMS's
/// UTF-16 output to UTF-8 would show up as a whole-file diff on the user's next commit. These tests
/// go through the real runner and read the bytes back off disk.
/// </remarks>
public sealed class FormatRunnerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("maxdop-runner-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static FormatRunner Runner(Mode mode) =>
        new(mode, FormatOptions.Default, configPath: null, parserVersionOverride: null);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Formatting changes this, so <c>--write</c> and <c>--check</c> both have work to do.</summary>
    private const string Unformatted = "select a,b from dbo.t where a=1";

    [Fact]
    public void Utf16FileIsRewrittenAsUtf16()
    {
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        var path = Write("utf16.sql", [.. encoding.GetPreamble(), .. encoding.GetBytes(Unformatted)]);

        var outcome = Runner(Mode.Write).RunFiles([path]);

        var written = File.ReadAllBytes(path);
        Assert.Equal(RunOutcome.Ok, outcome);
        Assert.Equal([0xFF, 0xFE], written[..2]);
        Assert.Equal("UTF-16 LE with BOM", SourceEncoding.Detect(written).Name);

        // The point of the round trip: the content changed, the encoding did not.
        var text = encoding.GetString(written[2..]);
        Assert.NotEqual(Unformatted, text);
        Assert.Contains("SELECT", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Utf8FileWithoutABomDoesNotGrowOne()
    {
        var path = Write("plain.sql", Encoding.UTF8.GetBytes(Unformatted));

        Assert.Equal(RunOutcome.Ok, Runner(Mode.Write).RunFiles([path]));

        var written = File.ReadAllBytes(path);
        Assert.NotEqual<byte[]>([0xEF, 0xBB, 0xBF], written[..3]);
    }

    [Fact]
    public void WritingTwiceIsAByteLevelNoOp()
    {
        var path = Write("twice.sql", Encoding.UTF8.GetBytes(Unformatted));

        Assert.Equal(RunOutcome.Ok, Runner(Mode.Write).RunFiles([path]));
        var first = File.ReadAllBytes(path);

        Assert.Equal(RunOutcome.Ok, Runner(Mode.Write).RunFiles([path]));
        Assert.Equal(first, File.ReadAllBytes(path));
    }

    [Fact]
    public void CheckReportsTheFileAndLeavesItAlone()
    {
        var original = Encoding.UTF8.GetBytes(Unformatted);
        var path = Write("check.sql", original);

        Assert.Equal(RunOutcome.WouldChangeOrUnparseable, Runner(Mode.Check).RunFiles([path]));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void CheckIsQuietOnAnAlreadyFormattedFile()
    {
        var path = Write("formatted.sql", Encoding.UTF8.GetBytes(Unformatted));
        Runner(Mode.Write).RunFiles([path]);

        Assert.Equal(RunOutcome.Ok, Runner(Mode.Check).RunFiles([path]));
    }

    [Fact]
    public void AFileThatCannotSurviveDecodingIsLeftUntouched()
    {
        // 0xE9 is é in Windows-1252 and not valid UTF-8. With no BOM to go on the file reads as
        // UTF-8, decoding would substitute U+FFFD, and writing that back would destroy the byte.
        // The runner has to refuse before it formats anything.
        var original = "-- caf\u00E9\nSELECT 1;\n"u8.ToArray();
        original[7] = 0xE9;
        var path = Write("cp1252.sql", original);

        // The input's problem, not maxdop's: the file is a fact, it is left alone, and the reader's
        // next move is to go and look at it — the same instruction exit 1 already carries for a file
        // that will not parse. Classifying it as maxdop's problem made `--check` over any tree
        // containing one legacy-encoded file exit 2 permanently, and put it beyond what a baseline
        // can forgive.
        Assert.Equal(RunOutcome.WouldChangeOrUnparseable, Runner(Mode.Write).RunFiles([path]));

        // The part that actually matters, and is unchanged: not one byte was touched.
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void AnUnparseableFileIsLeftUntouchedAndReportedAsTheInputsProblem()
    {
        var original = Encoding.UTF8.GetBytes(":setvar Path \"C:\\temp\"\nSELECT 1;\n");
        var path = Write("sqlcmd.sql", original);

        Assert.Equal(RunOutcome.WouldChangeOrUnparseable, Runner(Mode.Write).RunFiles([path]));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void TheWorstOutcomeAcrossSeveralFilesWins()
    {
        var good = Write("good.sql", Encoding.UTF8.GetBytes(Unformatted));
        var bad = Write("bad.sql", Encoding.UTF8.GetBytes("SELECT FROM WHERE;"));

        // Ordered so the failure comes first: the run must not stop at it, and the good file must
        // still be formatted.
        Assert.Equal(RunOutcome.WouldChangeOrUnparseable, Runner(Mode.Write).RunFiles([bad, good]));
        Assert.Contains("SELECT", File.ReadAllText(good), StringComparison.Ordinal);
        Assert.DoesNotContain("select", File.ReadAllText(good), StringComparison.Ordinal);
    }
}
