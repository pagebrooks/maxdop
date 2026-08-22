using Maxdop.Cli;

namespace Maxdop.Cli.Tests;

/// <summary>
/// SQLCMD detection, which exists only to turn a confusing parse error into a useful sentence.
/// </summary>
public class SqlcmdDirectivesTests
{
    [Theory]
    [InlineData(":setvar DbName Foo\nUSE [$(DbName)];", ":setvar")]
    [InlineData(":r .\\shared\\helpers.sql", ":r")]
    [InlineData(":connect myserver", ":connect")]
    [InlineData(":on error exit", ":on")]
    [InlineData("SELECT 1;\nGO\n:setvar x 1", ":setvar")]
    [InlineData("    :setvar indented 1", ":setvar")]
    [InlineData(":SETVAR ShoutingCase 1", ":setvar")]
    public void DirectivesAreRecognised(string sql, string expected)
    {
        Assert.Equal(expected, SqlcmdDirectives.Find(sql));
    }

    [Theory]
    [InlineData("SELECT geography::Point(1, 2, 4326);")]
    [InlineData("SELECT t2::f() FROM t;")]
    [InlineData("SELECT dbo.Type::Method() FROM t;")]
    public void TypeMethodCallsAreNotDirectives(string sql)
    {
        // `::` is T-SQL. Matching it would tell users their perfectly good query is a SQLCMD script,
        // which is why detection requires a single colon followed by a letter.
        Assert.Null(SqlcmdDirectives.Find(sql));
    }

    [Theory]
    [InlineData("SELECT 1;")]
    [InlineData("-- a comment mentioning :setvar in prose")]
    [InlineData("SELECT ':setvar' AS NotADirective;")]
    [InlineData("")]
    public void OrdinaryTsqlIsNotFlagged(string sql)
    {
        // The middle two are why detection runs only *after* a parse has already failed: a directive
        // name inside a comment or a literal is not a directive, and here the worst a false positive
        // can do is word the message badly rather than skip a file that would have formatted.
        Assert.Null(SqlcmdDirectives.Find(sql));
    }

    [Fact]
    public void ColonFollowedByNonLetterIsNotADirective()
    {
        Assert.Null(SqlcmdDirectives.Find(": 1"));
        Assert.Null(SqlcmdDirectives.Find(":123"));
    }

    [Fact]
    public void UnknownColonWordIsNotGuessedAt()
    {
        // Only the documented directive names count; anything else is more likely to be something
        // this detector has misread than a SQLCMD command nobody has heard of.
        Assert.Null(SqlcmdDirectives.Find(":frobnicate everything"));
    }
}
