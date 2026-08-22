using Maxdop.Core.Comments;
using Maxdop.Core.Formatting;
using Maxdop.Core.Printing;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Tests;

/// <summary>
/// Non-reserved word casing: the printer naming specific token positions as keyword positions, and
/// the verifier permitting a case change at exactly those positions.
/// </summary>
/// <remarks>
/// T-SQL has a large vocabulary of words that are keywords grammatically but lex as
/// <c>Identifier</c> because they are not reserved — <c>NVARCHAR</c>, <c>NOCOUNT</c>, <c>APPLY</c>,
/// <c>CAST</c>, <c>OUTPUT</c>, <c>NOWAIT</c>. Since the verifier compares identifiers
/// case-sensitively (<c>[Foo]</c> and <c>[foo]</c> are different objects under a case-sensitive
/// collation), keyword casing could not reach any of them and <c>declare @a int</c> came out as
/// <c>DECLARE @a int</c>.
/// <para><b>The half of this that matters most is the negative half.</b> Reading the corpus showed
/// that the same slices which hold <c>NVARCHAR</c> and <c>NOCOUNT</c> also hold <c>dbo</c>,
/// <c>t_history</c>, <c>SQL_Latin1_General_CP1_CI_AS</c>, <c>DatabaseName</c> and <c>COL0</c> — a
/// schema, a history table, a collation and a column. Matching on spelling would eventually rename
/// one of those, silently, and only under a case-sensitive collation. So the tests below that assert
/// a word is <em>left alone</em> are the ones protecting real data.</para>
/// </remarks>
public class KeywordPositionTests
{
    private static string Format(string sql) => Formatted(sql, FormatOptions.Default);

    private static string Formatted(string sql, FormatOptions options)
    {
        var result = SqlFormatter.Format(sql, options);
        if (result.Status != FormatStatus.Formatted)
        {
            Assert.Fail($"{result.Status}: {string.Join(" | ", result.Diagnostics)}");
        }

        return result.Output;
    }

    // --- the words that are now recased ------------------------------------------------

    [Theory]
    // Built-in type names. ScriptDom classifies these as SqlDataTypeReference, so the name cannot be
    // an object name — which is what makes recasing provable rather than hopeful.
    [InlineData("declare @a int;", "DECLARE @a INT;")]
    [InlineData("declare @a nvarchar(50);", "DECLARE @a NVARCHAR(50);")]
    [InlineData("declare @a nvarchar(max);", "DECLARE @a NVARCHAR(MAX);")]
    // Spacing inside the parentheses is the author's: a data type is emitted as one token slice, since
    // reassembling it from name and parameters would mean modelling six DataTypeReference subclasses
    // for no layout gain. Only the case of the name changes.
    [InlineData("declare @a decimal(18,4);", "DECLARE @a DECIMAL(18,4);")]
    [InlineData("declare @a decimal(18, 4);", "DECLARE @a DECIMAL(18, 4);")]
    [InlineData("declare @a uniqueidentifier;", "DECLARE @a UNIQUEIDENTIFIER;")]
    public void BuiltInTypeNamesAreRecased(string input, string expected)
    {
        Assert.Equal(expected, Format(input));
    }

    [Theory]
    [InlineData("set nocount on;", "SET NOCOUNT ON;")]
    [InlineData("set ansi_nulls on;", "SET ANSI_NULLS ON;")]
    [InlineData("set quoted_identifier off;", "SET QUOTED_IDENTIFIER OFF;")]
    [InlineData("set transaction isolation level read uncommitted;", "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;")]
    public void SessionSettingNamesAreRecased(string input, string expected)
    {
        Assert.Equal(expected, Format(input));
    }

    [Theory]
    [InlineData("select a from t cross apply dbo.f(t.id) as x;", "CROSS APPLY")]
    [InlineData("select cast(a as int) from t;", "CAST(a AS INT)")]
    [InlineData("select try_cast(a as int) from t;", "TRY_CAST(a AS INT)")]
    [InlineData("select iif(a > 1, 'y', 'n') from t;", "IIF(")]
    [InlineData("raiserror('x', 16, 1) with nowait;", "WITH NOWAIT")]
    [InlineData("insert t (a) output inserted.a values (1);", "OUTPUT inserted.a")]
    [InlineData("select row_number() over (partition by a order by b) from t;", "OVER (PARTITION BY a")]
    [InlineData("declare c cursor local fast_forward for select a from t;", "CURSOR LOCAL FAST_FORWARD FOR")]
    [InlineData("fetch next from c into @a;", "FETCH NEXT FROM")]
    [InlineData("select a from t for xml path('row');", "FOR XML PATH('row')")]
    [InlineData("select a from t option (recompile, maxdop 1);", "OPTION (RECOMPILE, MAXDOP 1)")]
    [InlineData("throw 50000, 'x', 1;", "THROW 50000")]
    [InlineData("waitfor delay '00:01';", "WAITFOR DELAY")]
    [InlineData("drop table if exists #a;", "DROP TABLE IF EXISTS")]
    [InlineData("create table t (a int identity(1,1) not null);", "IDENTITY(1, 1)")]
    public void NonReservedKeywordsAreRecasedInTheirKeywordPositions(string input, string expected)
    {
        Assert.Contains(expected, Format(input), StringComparison.Ordinal);
    }

    [Fact]
    public void BeginTryAndEndCatchAreRecased()
    {
        Assert.Equal(
            """
            BEGIN TRY
                SELECT 1;
            END TRY
            BEGIN CATCH
                THROW;
            END CATCH
            """,
            Format("begin try select 1; end try begin catch throw; end catch"));
    }

    [Fact]
    public void LowerKeywordCaseGoesTheOtherWay()
    {
        // The claim is about which positions may be recased, not about which direction, so the option
        // has to work for identifiers too or `keywordCase: "lower"` would be half-applied.
        Assert.Equal(
            "declare @a int;",
            Formatted("DECLARE @a INT;", FormatOptions.Default with { KeywordCase = KeywordCase.Lower }));
    }

    // --- the words that must still be left exactly alone --------------------------------

    [Theory]
    // Every one of these was found by the corpus report living in the *same kind* of slice as the
    // words above. Renaming any of them would be silent corruption under a case-sensitive collation.
    [InlineData(
        "create table dbo.t (a int null) with (system_versioning = on (history_table = dbo.t_History));",
        "dbo.t_History")]
    [InlineData(
        "create table dbo.t (b nvarchar(50) collate SQL_Latin1_General_CP1_CI_AS null);",
        "SQL_Latin1_General_CP1_CI_AS")]
    [InlineData("select a from t with (index(MyIndex));", "MyIndex")]
    [InlineData("select a from t option (table hint (t, index(MyIndex)));", "MyIndex")]
    [InlineData("declare @a dbo.MyUserType;", "dbo.MyUserType")]
    [InlineData("set identity_insert dbo.MyTable on;", "dbo.MyTable")]
    public void ObjectNamesInTheSameRegionsAreNotTouched(string input, string preserved)
    {
        Assert.Contains(preserved, Format(input), StringComparison.Ordinal);
    }

    [Fact]
    public void UserDefinedTypeNameIsNotRecasedButABuiltInOneIs()
    {
        // The distinction is ScriptDom's, not a spelling rule: SqlDataTypeReference means a built-in
        // type, UserDataTypeReference means a type in a schema.
        Assert.Equal("DECLARE @a INT, @b dbo.MyType;", Format("declare @a int, @b dbo.MyType;"));
    }

    [Fact]
    public void LabelIsNotRecasedBecauseItHasNoNodeToProtectIt()
    {
        // LabelStatement holds its label in a plain string property with no AST node, so nothing marks
        // those tokens as a name. Recasing it would rename the label and leave every GOTO pointing at
        // the old spelling — broken code, not a cosmetic change.
        var result = Format("MyLabel: goto MyLabel;");

        Assert.Contains("MyLabel:", result, StringComparison.Ordinal);
        Assert.Contains("GOTO MyLabel;", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FunctionNamesAreNotRecased()
    {
        // ScriptDom models `ROW_NUMBER()` and `dbo.MyFunc()` identically, so there is no way to tell a
        // built-in from a user-defined function here. Left alone rather than guessed at.
        Assert.Contains("row_number()", Format("select row_number() over (order by a) from t;"), StringComparison.Ordinal);
        Assert.Contains("dbo.MyFunc(", Format("select dbo.MyFunc(1) from t;"), StringComparison.Ordinal);
    }

    // --- the permission is per token, and validated -------------------------------------

    [Fact]
    public void PrinterClaimsOnlyTheTokensItActuallyRecased()
    {
        // Every claim must land on an identifier the printer really did recase. If a handler ever
        // claimed a region containing a name, the claim set would include that name's token and this
        // would show it.
        const string sql = "declare @a int; set nocount on; select a from t with (index(MyIndex));";
        var root = Parse(sql, out var errors);
        Assert.Empty(errors);

        var printer = new SqlPrinter(root, CommentAttacher.Attach(root), FormatOptions.Default);
        _ = printer.Print(root);

        var claimed = printer.KeywordCasedTokens
            .Select(i => root.ScriptTokenStream![i])
            .ToList();

        Assert.All(claimed, token => Assert.Equal(TSqlTokenType.Identifier, token.TokenType));
        Assert.Contains(claimed, token => token.Text!.Equals("int", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(claimed, token => token.Text!.Equals("nocount", StringComparison.OrdinalIgnoreCase));

        // The index name sits in a slice the printer deliberately did not claim.
        Assert.DoesNotContain(claimed, token => token.Text!.Equals("MyIndex", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierStillRejectsACaseChangeItWasNotToldAbout()
    {
        // The safety property. With no claims, an identifier case change is a defect — which is what
        // keeps a wrongly-opted-in handler from passing silently.
        var before = Parse("SELECT Amount FROM t;", out _);
        var after = Parse("SELECT amount FROM t;", out _);

        Assert.False(RoundTripVerifier.Verify(before, after, out var diagnostic, new HashSet<int>()));
        Assert.Contains("token text changed", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifierRejectsAClaimOnSomethingThatIsNotAnIdentifier()
    {
        // A claim on a string literal or a quoted identifier is a printer bug. Relaxing the comparison
        // there would hide it, so it fails loudly instead.
        var before = Parse("SELECT 'Yes';", out _);
        var after = Parse("SELECT 'yes';", out _);

        // Index 2 is the literal: `SELECT`, whitespace, `'Yes'`.
        Assert.False(RoundTripVerifier.Verify(before, after, out var diagnostic, new HashSet<int> { 2 }));
        Assert.Contains("claimed a", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void StringLiteralsAreNeverRecasedEvenInsideAKeywordRegion()
    {
        // `Keywords` recases identifiers only. A literal is data whatever region it sits in.
        Assert.Contains("PATH('Row')", Format("select a from t for xml path('Row');"), StringComparison.Ordinal);
    }

    private static TSqlFragment Parse(string sql, out IList<ParseError> errors)
    {
        using var reader = new StringReader(sql);
        return new TSql180Parser(initialQuotedIdentifiers: false).Parse(reader, out errors);
    }

    [Fact]
    public void ResultSetDefinitionsKeepTheirNames()
    {
        // The EXEC tail is emitted as a pure-keyword slice, and `WITH RESULT SETS (…)` puts names in
        // it: column names, a four-part object name after AS OBJECT, a type name after AS TYPE.
        // Recasing them renamed things — `server1.db.dbo.t1` came out `SERVER1.DB.DBO.T1` — and did
        // so silently, because the slice *claims* those positions, so the verifier relaxes case
        // exactly where it must not and the round trip passes. The statement passes through instead.
        //
        // Found by formatting the corpus twice, once per keyword case, and looking for words that
        // changed but were not keywords.
        const string sql = "execute p1 with result sets (AS OBJECT server1.db.dbo.t1, (c1 int null), AS TYPE dbo.type1);";
        Assert.Equal(sql, Format(sql));
    }

    [Fact]
    public void ResultSetsWithoutDefinitionsStillFormats()
    {
        // No definitions means no names, so these keep their layout. Declining on any node in the
        // tail range was the first attempt and cost `WITH RECOMPILE` its formatting for no safety
        // gained.
        Assert.Equal("EXECUTE p1 WITH RESULT SETS NONE;", Format("execute p1 with result sets none;"));
        Assert.Equal("EXECUTE p1 WITH RECOMPILE;", Format("execute p1 with recompile;"));
    }
}
