namespace Maxdop.Cli.Tests;

/// <summary>
/// The <c>exclude</c> matcher.
/// </summary>
/// <remarks>
/// Worth its own suite because every failure here is silent in the direction that matters: a pattern
/// that matches too much removes files from the check without saying anything useful, and a repo-wide
/// gate that quietly stops looking at files is the failure mode this whole feature exists to fix.
/// </remarks>
public class GlobTests
{
    [Theory]
    // A bare name applies at any depth, the way .gitignore treats one.
    [InlineData("*.gen.sql", "a.gen.sql")]
    [InlineData("*.gen.sql", "db/dbo/a.gen.sql")]
    [InlineData("build", "build/x.sql")]
    [InlineData("build", "build")]
    // A pattern with a separator is anchored to the config that declared it.
    [InlineData("db/generated/**", "db/generated/x.sql")]
    [InlineData("db/generated/**", "db/generated/deep/x.sql")]
    [InlineData("db/generated", "db/generated/deep/x.sql")]
    // ** spans segments; * does not.
    [InlineData("db/**/x.sql", "db/x.sql")]
    [InlineData("db/**/x.sql", "db/a/b/x.sql")]
    [InlineData("db/*/x.sql", "db/a/x.sql")]
    // A trailing slash is the same as naming the directory.
    [InlineData("vendor/", "vendor/third.sql")]
    // Leading slash anchors explicitly.
    [InlineData("/src/x.sql", "src/x.sql")]
    // Case is ignored on every platform, so a repo checks the same way everywhere.
    [InlineData("*.GEN.SQL", "a.gen.sql")]
    [InlineData("Vendor/", "vendor/third.sql")]
    // Windows separators in a hand-written pattern still work.
    [InlineData("db\\generated\\**", "db/generated/x.sql")]
    public void Matches(string pattern, string path)
    {
        Assert.True(Glob.Matches(pattern, path), $"expected {pattern} to match {path}");
    }

    [Theory]
    // * must not cross a separator, or `src/*.sql` would swallow the whole tree.
    [InlineData("db/*/x.sql", "db/a/b/x.sql")]
    [InlineData("*.sql", "a.sql.bak")]
    // Anchored patterns do not float.
    [InlineData("/src/x.sql", "db/src/x.sql")]
    [InlineData("db/generated/**", "other/db/generated/x.sql")]
    // A partial name is not a match.
    [InlineData("gen", "generated/x.sql")]
    [InlineData("build", "rebuild/x.sql")]
    public void DoesNotMatch(string pattern, string path)
    {
        Assert.False(Glob.Matches(pattern, path), $"expected {pattern} not to match {path}");
    }

    [Fact]
    public void QuestionMarkIsExactlyOneCharacterAndStaysInItsSegment()
    {
        Assert.True(Glob.Matches("v?.sql", "v1.sql"));
        Assert.False(Glob.Matches("v?.sql", "v12.sql"));
        Assert.False(Glob.Matches("db?x.sql", "db/x.sql"));
    }

    [Fact]
    public void PathologicalPatternDoesNotHang()
    {
        // The shape that makes naive backtracking wildcard matchers exponential. A config file must
        // not be able to wedge CI, so this is a real requirement rather than a curiosity.
        Assert.False(Glob.Matches("a*a*a*a*a*a*a*b", new string('a', 64)));
    }

    [Fact]
    public void EmptyPatternMatchesNothing()
    {
        Assert.False(Glob.Matches(string.Empty, "a.sql"));
        Assert.False(Glob.Matches("   ", "a.sql"));
    }

    [Fact]
    public void MatchesAnyIsTheOrOfItsPatterns()
    {
        string[] patterns = ["vendor/", "*.gen.sql"];

        Assert.True(Glob.MatchesAny(patterns, "vendor/a.sql"));
        Assert.True(Glob.MatchesAny(patterns, "db/b.gen.sql"));
        Assert.False(Glob.MatchesAny(patterns, "db/b.sql"));
        Assert.False(Glob.MatchesAny([], "anything.sql"));
    }
}
