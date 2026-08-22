using Maxdop.Cli;

namespace Maxdop.Cli.Tests;

/// <summary>
/// The CLI contract, at the argument layer.
/// </summary>
/// <remarks>
/// Worth testing at this level because the failure modes are silent: a flag that parses into the
/// wrong mode formats the wrong thing, and an exit code that collapses two outcomes into one makes a
/// CI pipeline lie.
/// </remarks>
public class CommandLineTests
{
    private static CommandLine Parse(params string[] args)
    {
        Assert.True(CommandLine.TryParse(args, out var command, out var error), error);
        return command;
    }

    private static string Reject(params string[] args)
    {
        Assert.False(CommandLine.TryParse(args, out _, out var error));
        Assert.NotNull(error);
        return error!;
    }

    // --- modes ------------------------------------------------------------------------

    [Fact]
    public void NoArgumentsMeansStdin()
    {
        Assert.Equal(Mode.Stdin, Parse().Mode);
    }

    [Fact]
    public void OneFileGoesToStdout()
    {
        var command = Parse("a.sql");

        Assert.Equal(Mode.ToStdout, command.Mode);
        Assert.Equal(["a.sql"], command.Files);
    }

    [Theory]
    [InlineData("--write")]
    [InlineData("-w")]
    public void WriteFlagSelectsWriteMode(string flag)
    {
        Assert.Equal(Mode.Write, Parse(flag, "a.sql").Mode);
    }

    [Fact]
    public void CheckSelectsCheckMode()
    {
        Assert.Equal(Mode.Check, Parse("--check", "a.sql", "b.sql").Mode);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void HelpWinsOverEverythingElse(string flag)
    {
        // Help must work even alongside arguments that would otherwise be rejected, or `--help` is
        // useless exactly when someone needs it.
        Assert.Equal(Mode.Help, Parse(flag, "--write", "--check").Mode);
    }

    [Fact]
    public void VersionIsItsOwnMode()
    {
        Assert.Equal(Mode.Version, Parse("--version").Mode);
    }

    // --- combinations that cannot mean anything ---------------------------------------

    [Fact]
    public void WriteAndCheckAreMutuallyExclusive()
    {
        Assert.Contains("mutually exclusive", Reject("--write", "--check", "a.sql"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--write")]
    [InlineData("--check")]
    public void WriteAndCheckNeedAFile(string flag)
    {
        // Neither can act on stdin: there is nothing to rewrite and nothing to compare against.
        Assert.Contains("needs at least one file", Reject(flag), StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralFilesWithoutWriteOrCheckIsRejected()
    {
        // Formatting them all to stdout would run them together into one stream, which is never what
        // anybody meant — and would look like it worked.
        Assert.Contains("run them together", Reject("a.sql", "b.sql"), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownOptionIsRejectedRatherThanTreatedAsAFile()
    {
        Assert.Contains("unknown option: --nope", Reject("--nope"), StringComparison.Ordinal);
    }

    [Fact]
    public void RangeIsReservedAndSaysWhy()
    {
        // Reserved rather than ignored: silently formatting the whole file when the caller asked for
        // a range would overwrite text outside it, which is how an editor's "format selection"
        // destroys work.
        var error = Reject("--range", "1:5");

        Assert.Contains("reserved", error, StringComparison.Ordinal);
        Assert.Contains("outside the range", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--config")]
    [InlineData("--stdin-filepath")]
    [InlineData("--parser-version")]
    public void ValueFlagsRejectAMissingValue(string flag)
    {
        // Without this, `maxdop --config` would silently read stdin with default settings.
        Assert.Contains("needs a value", Reject(flag), StringComparison.Ordinal);
    }

    // --- values -----------------------------------------------------------------------

    [Fact]
    public void ConfigAndStdinFilePathAreCaptured()
    {
        var command = Parse("--config", "/etc/maxdop.json", "--stdin-filepath", "/repo/proc.sql");

        Assert.Equal("/etc/maxdop.json", command.ConfigPath);
        Assert.Equal("/repo/proc.sql", command.StdinFilePath);
        Assert.Equal(Mode.Stdin, command.Mode);
    }

    [Theory]
    [InlineData("2016", 130)]
    [InlineData("2022", 160)]
    [InlineData("130", 130)]
    [InlineData("fabricdw", 0)]
    public void ParserVersionAcceptsProductYearsAndCompatibilityLevels(string value, int expected)
    {
        Assert.Equal(expected, Parse("--parser-version", value).ParserVersion);
    }

    [Fact]
    public void UnrecognisedParserVersionExplainsWhatIsAccepted()
    {
        var error = Reject("--parser-version", "2011");

        Assert.Contains("not recognised", error, StringComparison.Ordinal);
        Assert.Contains("fabricdw", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ParserVersionIsUnsetWhenNotGiven()
    {
        // Null rather than the default version, so the config file's value is not overridden by an
        // option the user never passed.
        Assert.Null(Parse("a.sql").ParserVersion);
    }

    [Fact]
    public void FilenamesThatLookLikeFlagsAreStillFiles()
    {
        // A single `-` is stdin by convention elsewhere; here it is just not an option, and treating
        // it as one would reject a legitimately odd filename.
        Assert.Equal(["-"], Parse("-").Files);
    }

    // --- discovery, lists and baselines ------------------------------------------------

    [Fact]
    public void FilesFromIsCarried()
    {
        Assert.Equal("-", Parse("--check", "--files-from", "-").FilesFrom);
    }

    [Fact]
    public void FilesFromSatisfiesTheNeedForPaths()
    {
        // `git diff | maxdop --check --files-from -` names no path on the command line at all.
        Assert.Equal(Mode.Check, Parse("--check", "--files-from", "-").Mode);
    }

    [Fact]
    public void FilesFromNeedsSomethingToDoWithTheFiles()
    {
        Assert.Contains("--files-from needs", Reject("--files-from", "-"));
    }

    [Fact]
    public void WriteBaselineIsItsOwnMode()
    {
        var command = Parse("--write-baseline", "src");

        Assert.Equal(Mode.WriteBaseline, command.Mode);
        Assert.Equal(Baseline.DefaultFileName, command.BaselinePath);
    }

    [Fact]
    public void WriteBaselineTakesAnExplicitPath()
    {
        Assert.Equal("custom", Parse("--write-baseline", "--baseline", "custom", "src").BaselinePath);
    }

    [Fact]
    public void WriteBaselineDoesNotCombineWithCheck()
    {
        Assert.Contains("replaces --check", Reject("--check", "--write-baseline", "src"));
    }

    [Fact]
    public void BaselineIsRejectedWithWrite()
    {
        // A baseline forgives files that would fail a check. Under --write it could only mean "leave
        // these badly formatted", which is exclusion, not adoption.
        Assert.Contains("applies to --check", Reject("--write", "--baseline", "b", "src"));
    }

    [Fact]
    public void BaselineNeedsAModeThatConsultsIt()
    {
        Assert.Contains("needs --check", Reject("--baseline", "b", "a.sql"));
    }

    [Fact]
    public void BaselineIsNeverDiscovered()
    {
        // A baseline weakens what --check means, so it has to be asked for. A file that happened to
        // be sitting next to the config must not quietly soften the gate.
        Assert.Null(Parse("--check", "src").BaselinePath);
    }

    [Fact]
    public void WriteBaselineNeedsSomewhereToLook()
    {
        Assert.Contains("needs at least one file or directory", Reject("--write-baseline"));
    }
}
