using Maxdop.Cli;
using Maxdop.Core.Formatting;

namespace Maxdop.Cli.Tests;

/// <summary>
/// <c>.maxdop.json</c> discovery and parsing.
/// </summary>
/// <remarks>
/// The config file is the headline feature — team consistency — so the failure modes matter: a
/// config resolved from the wrong directory applies one repo's house style to another's files, and a
/// typo that falls back to defaults instead of erroring means the team silently is not using the
/// settings they agreed.
/// </remarks>
public sealed class ConfigFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("maxdop-config-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relativeDirectory, string json)
    {
        // Normalised, so writing to "." yields /root/.maxdop.json rather than /root/./.maxdop.json
        // and comparisons against what Discover returns are meaningful.
        var directory = Path.GetFullPath(Path.Combine(_root, relativeDirectory));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ConfigFile.FileName);
        File.WriteAllText(path, json);
        return path;
    }

    private static FormatOptions Load(string path)
    {
        Assert.True(ConfigFile.TryLoad(path, FormatOptions.Default, out var options, out var error), error);
        return options;
    }

    // --- discovery --------------------------------------------------------------------

    [Fact]
    public void DiscoveryWalksUpFromTheFilesOwnDirectory()
    {
        var expected = Write(".", "{}");
        var deep = Path.Combine(_root, "src", "procs");
        Directory.CreateDirectory(deep);

        Assert.Equal(expected, ConfigFile.Discover(deep));
    }

    [Fact]
    public void NearestConfigWins()
    {
        Write(".", """{"maxWidth": 200}""");
        var nearer = Write(Path.Combine("src", "procs"), """{"maxWidth": 60}""");

        var found = ConfigFile.Discover(Path.Combine(_root, "src", "procs"));

        Assert.Equal(nearer, found);
        Assert.Equal(60, Load(found!).Print.MaxWidth);
    }

    [Fact]
    public void MissingConfigIsNotAnError()
    {
        // Most repos will not have one, and the defaults are meant to be good.
        Assert.Null(ConfigFile.Discover(Path.Combine(_root, "no", "config", "here")));
    }

    [Fact]
    public void DiscoveryHandlesANullDirectory()
    {
        // Reading stdin without --stdin-filepath: there is no anchor to resolve against, and
        // falling back to the working directory would apply an unrelated repo's settings.
        Assert.Null(ConfigFile.Discover(null));
    }

    // --- parsing ----------------------------------------------------------------------

    [Fact]
    public void EveryOptionMapsThrough()
    {
        var path = Write(".", """
            {
              "maxWidth": 72,
              "indentSize": 2,
              "useTabs": true,
              "keywordCase": "lower",
              "leadingCommas": true,
              "alwaysBreakSelectList": true,
              "alwaysBreakWhere": true,
              "maxBlankLines": 0,
              "parserVersion": "2019",
              "initialQuotedIdentifiers": true
            }
            """);

        var options = Load(path);

        Assert.Equal(72, options.Print.MaxWidth);
        Assert.Equal(2, options.Print.IndentSize);
        Assert.True(options.Print.UseTabs);
        Assert.Equal(KeywordCase.Lower, options.KeywordCase);
        Assert.True(options.LeadingCommas);
        Assert.True(options.AlwaysBreakSelectList);
        Assert.True(options.AlwaysBreakWhere);
        Assert.Equal(0, options.MaxBlankLines);
        Assert.Equal(150, options.ParserVersion);
        Assert.True(options.InitialQuotedIdentifiers);
    }

    [Fact]
    public void AbsentKeysKeepTheirDefaults()
    {
        // Every property is nullable for this reason: an option the file does not mention must not be
        // reset, or a config that sets one key would silently revert every other.
        var options = Load(Write(".", """{"maxWidth": 72}"""));

        Assert.Equal(72, options.Print.MaxWidth);
        Assert.Equal(FormatOptions.Default.KeywordCase, options.KeywordCase);
        Assert.Equal(FormatOptions.Default.MaxBlankLines, options.MaxBlankLines);
        Assert.Equal(FormatOptions.Default.Print.IndentSize, options.Print.IndentSize);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAllowed()
    {
        // People hand-edit this file, and a config format that rejects a trailing comma is a config
        // format people fight with.
        var options = Load(Write(".", """
            {
              // house style, agreed 2026-08
              "keywordCase": "lower",
              "maxWidth": 90,
            }
            """));

        Assert.Equal(KeywordCase.Lower, options.KeywordCase);
        Assert.Equal(90, options.Print.MaxWidth);
    }

    // --- bad input is an error, never a silent fallback --------------------------------

    [Theory]
    [InlineData("""{"keywordCase": "sideways"}""", "keywordCase")]
    [InlineData("""{"parserVersion": "2011"}""", "parserVersion")]
    [InlineData("""{"maxWidth": 0}""", "maxWidth")]
    [InlineData("""{"maxWidth": -10}""", "maxWidth")]
    [InlineData("""{"indentSize": -1}""", "indentSize")]
    [InlineData("""{"maxBlankLines": -1}""", "maxBlankLines")]
    public void BadValuesAreRejectedAndNamed(string json, string expectedInMessage)
    {
        var path = Write(".", json);

        Assert.False(ConfigFile.TryLoad(path, FormatOptions.Default, out _, out var error));
        Assert.Contains(expectedInMessage, error!, StringComparison.Ordinal);
        Assert.Contains(path, error!, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedJsonIsReportedWithTheFilePath()
    {
        var path = Write(".", "{ this is not json");

        Assert.False(ConfigFile.TryLoad(path, FormatOptions.Default, out _, out var error));
        Assert.Contains("not valid JSON", error!, StringComparison.Ordinal);
        Assert.Contains(path, error!, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownKeysAreIgnored()
    {
        // Forward compatibility: a config written for a newer maxdop should still work, rather than
        // failing the whole team's build on a key this version has not learned yet.
        var options = Load(Write(".", """{"maxWidth": 72, "somethingFromTheFuture": true}"""));

        Assert.Equal(72, options.Print.MaxWidth);
    }

    [Fact]
    public void EmptyObjectIsValidAndChangesNothing()
    {
        Assert.Equal(FormatOptions.Default, Load(Write(".", "{}")));
    }
}
