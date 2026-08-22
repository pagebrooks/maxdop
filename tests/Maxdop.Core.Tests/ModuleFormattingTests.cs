using Maxdop.Core.Formatting;
using Maxdop.Core.Printing;

namespace Maxdop.Core.Tests;

/// <summary>
/// The module definitions and <c>MERGE</c> — the four constructs the MVP scope named and that had no
/// handler.
/// </summary>
/// <remarks>
/// The corpus hid this gap completely: First Responder Kit and Ola Hallengren are stored procedures
/// almost exclusively, so real-world coverage read 99.2% while a file containing one view and one
/// function measured <b>2.5%</b>. A database project is mostly views, functions and triggers.
/// <para>All three module handlers dispatch on ScriptDom's abstract base, so <c>ALTER</c> and
/// <c>CREATE OR ALTER</c> are covered by the same code — which the tests below check rather than
/// assume.</para>
/// </remarks>
public class ModuleFormattingTests
{
    private static string Format(string sql, int maxWidth = 120)
    {
        var result = SqlFormatter.Format(
            sql,
            FormatOptions.Default with { Print = PrintOptions.Default with { MaxWidth = maxWidth } });

        if (result.Status != FormatStatus.Formatted)
        {
            Assert.Fail($"{result.Status}: {string.Join(" | ", result.Diagnostics)}");
        }

        return result.Output;
    }

    private static void AssertStable(string sql)
    {
        var once = Format(sql);
        Assert.Equal(once, Format(once));
    }

    // --- CREATE VIEW ------------------------------------------------------------------

    [Fact]
    public void ViewBodyGoesOnItsOwnLineAfterAs()
    {
        Assert.Equal(
            """
            CREATE VIEW dbo.vLedger
            AS
            SELECT a, b FROM dbo.t;
            """,
            Format("create view dbo.vLedger as select a,b from dbo.t;"));
    }

    [Fact]
    public void ViewColumnListAndOptionsSurvive()
    {
        // Dropping the column list would rename every column the view exposes; `WITH CHECK OPTION` is a
        // bool with no node, so it can only arrive through the statement's tail.
        Assert.Equal(
            """
            CREATE VIEW dbo.v (a, b)
            WITH SCHEMABINDING
            AS
            SELECT x, y FROM dbo.t
            WITH CHECK OPTION;
            """,
            Format("create view dbo.v (a,b) with schemabinding as select x,y from dbo.t with check option;"));
    }

    [Theory]
    [InlineData("alter view dbo.v as select 1 as x;", "ALTER VIEW dbo.v")]
    [InlineData("create or alter view dbo.v as select 1 as x;", "CREATE OR ALTER VIEW dbo.v")]
    public void EveryViewSpellingUsesTheSameHandler(string sql, string expectedHeader)
    {
        Assert.StartsWith(expectedHeader, Format(sql), StringComparison.Ordinal);
    }

    // --- CREATE FUNCTION --------------------------------------------------------------

    [Fact]
    public void ScalarFunctionFormats()
    {
        Assert.Equal(
            """
            CREATE FUNCTION dbo.fnBalance (@id INT)
            RETURNS DECIMAL(18, 2)
            AS
            BEGIN
                RETURN 1;
            END
            """,
            Format("create function dbo.fnBalance (@id int) returns decimal(18, 2) as begin return 1; end"));
    }

    [Fact]
    public void EmptyParameterListKeepsItsParentheses()
    {
        // With no parameters there is no node to anchor on, so the `()` has to be found in the tokens —
        // without that it fell into the RETURNS slice and came out as `dbo.f\n() RETURNS INT`.
        Assert.Equal(
            """
            CREATE FUNCTION dbo.f()
            RETURNS INT
            AS
            BEGIN
                RETURN 1;
            END
            """,
            Format("create function dbo.f() returns int as begin return 1; end"));
    }

    [Fact]
    public void ParameterListSpreadOverLinesDoesNotDuplicateItsParenthesis()
    {
        // Five corpus files caught this: the closing parenthesis was assumed to be the token after the
        // last parameter, so a list written across lines emitted it twice and the output stopped
        // parsing.
        AssertStable("CREATE FUNCTION dbo.f\n(@a INT, @b INT)\nRETURNS INT\nAS\nBEGIN\n    RETURN 1;\nEND");

        Assert.DoesNotContain(
            "))",
            Format("CREATE FUNCTION dbo.f\n(@a INT, @b INT)\nRETURNS INT\nAS\nBEGIN\n    RETURN 1;\nEND"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void InlineTableValuedFunctionBodyIsFormatted()
    {
        // The query inside an inline table-valued function *is* the function, so leaving it verbatim
        // would mean the whole definition went unformatted.
        Assert.Equal(
            """
            CREATE FUNCTION dbo.itvf (@id INT)
            RETURNS TABLE AS RETURN (
                SELECT a FROM dbo.t WHERE id = @id
            );
            """,
            Format("create function dbo.itvf (@id int) returns table as return (select a from dbo.t where id=@id);"));
    }

    [Fact]
    public void InlineTableValuedFunctionWithItsOwnWithClauseKeepsItsParentheses()
    {
        // Whether the parentheses belong to the query or to the return type depends on the query:
        // a plain `RETURN (SELECT …)` parses them as part of the query, while
        // `RETURN (WITH XMLNAMESPACES (…) SELECT …)` puts the statement's range at the WITH, inside
        // them — so forwarding dropped them and the output stopped parsing.
        var result = Format("CREATE FUNCTION dbo.f() RETURNS TABLE RETURN (WITH XMLNAMESPACES ('u' AS p) SELECT c FROM t)");

        Assert.Contains("XMLNAMESPACES", result, StringComparison.Ordinal);
        Assert.EndsWith(")", result.TrimEnd(), StringComparison.Ordinal);
        AssertStable("CREATE FUNCTION dbo.f() RETURNS TABLE RETURN (WITH XMLNAMESPACES ('u' AS p) SELECT c FROM t)");
    }

    [Fact]
    public void MultiStatementTableValuedFunctionReusesTheTableLayout()
    {
        // The return type here is a DeclareTableVariableBody — the same node `DECLARE @t TABLE` uses —
        // so the table gets the identical layout for free.
        Assert.Equal(
            """
            CREATE FUNCTION dbo.mtvf (@id INT)
            RETURNS @r TABLE (
                a INT
            )
            AS
            BEGIN
                RETURN;
            END
            """,
            Format("create function dbo.mtvf (@id int) returns @r table (a int) as begin return; end"));
    }

    [Fact]
    public void FunctionOptionsGoOnTheirOwnLine()
    {
        Assert.Contains(
            "WITH SCHEMABINDING, RETURNS NULL ON NULL INPUT\nAS",
            Format(
                "create function dbo.f() returns int with schemabinding, returns null on null input"
                + " as begin return 1; end"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ClrFunctionPassesThrough()
    {
        // `AS EXTERNAL NAME asm.Class.Method` has no T-SQL body, so the layout does not apply.
        const string sql = "CREATE FUNCTION dbo.f() RETURNS INT AS EXTERNAL NAME a1.c1.f1;";
        Assert.Equal(sql, Format(sql));
    }

    // --- CREATE TRIGGER ---------------------------------------------------------------

    [Fact]
    public void TriggerFormatsWithItsTargetAndActions()
    {
        Assert.Equal(
            """
            CREATE TRIGGER dbo.tr
            ON dbo.t
            AFTER INSERT, UPDATE
            AS
            BEGIN
                SELECT 1;
            END
            """,
            Format("create trigger dbo.tr on dbo.t after insert, update as begin select 1; end"));
    }

    [Fact]
    public void InsteadOfTriggerFormats()
    {
        Assert.Equal(
            """
            CREATE TRIGGER dbo.tr
            ON dbo.t
            INSTEAD OF DELETE
            AS
            SELECT 1;
            """,
            Format("create trigger dbo.tr on dbo.t instead of delete as select 1;"));
    }

    [Theory]
    [InlineData("create trigger tr on database for create_table as select 1;", "ON DATABASE\nFOR CREATE_TABLE")]
    [InlineData("create trigger tr on all server for ddl_login_events as select 1;", "ON ALL SERVER\nFOR DDL_LOGIN_EVENTS")]
    [InlineData("create trigger tr on database for alter_procedure, drop_procedure as select 1;", "FOR ALTER_PROCEDURE, DROP_PROCEDURE")]
    public void DdlTriggerScopeAndEventsAreRecased(string sql, string expected)
    {
        // All four forms are enums by the time the printer sees them: TriggerScope for the target,
        // EventType or EventGroup for the action. The parser has resolved the word, so no object name can
        // reach here — which is what makes this recasing provable rather than a guess about spelling.
        Assert.Contains(expected, Format(sql), StringComparison.Ordinal);
    }

    [Fact]
    public void DmlTriggerTargetIsAnObjectNameAndKeepsItsCasing()
    {
        // The other half of the same handler: when TriggerObject has a Name, the range *is* the name.
        Assert.Contains("ON dbo.MyTable", Format("create trigger tr on dbo.MyTable after insert as select 1;"), StringComparison.Ordinal);
    }

    [Fact]
    public void TriggerActionsStayOnOneLine()
    {
        // The action list is grouped, so `AFTER INSERT, UPDATE, DELETE` does not break at each comma.
        Assert.Contains(
            "AFTER INSERT, UPDATE, DELETE",
            Format("create trigger dbo.tr on dbo.t after insert, update, delete as select 1;"),
            StringComparison.Ordinal);
    }

    // --- MERGE ------------------------------------------------------------------------

    [Fact]
    public void MergeFormatsWithEachActionOnItsOwnLine()
    {
        Assert.Equal(
            """
            MERGE INTO dbo.tgt AS t
            USING dbo.src AS s
                ON t.id = s.id
            WHEN MATCHED THEN
                UPDATE SET t.a = s.a
            WHEN NOT MATCHED BY TARGET THEN
                INSERT (a, b) VALUES (s.a, s.b)
            WHEN NOT MATCHED BY SOURCE THEN
                DELETE;
            """,
            Format(
                "merge into dbo.tgt as t using dbo.src as s on t.id=s.id"
                + " when matched then update set t.a=s.a"
                + " when not matched by target then insert (a,b) values (s.a,s.b)"
                + " when not matched by source then delete;"));
    }

    [Fact]
    public void MergeActionConditionSurvives()
    {
        // `WHEN MATCHED AND <condition> THEN` — the clause's own range starts at the condition, so the
        // `WHEN MATCHED AND` in front of it belongs to no node and is read from the gap.
        Assert.Contains(
            "WHEN MATCHED AND s.x = 1 THEN",
            Format("merge dbo.tgt as t using dbo.src as s on t.id=s.id when matched and s.x=1 then update set t.a=s.a;"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MergeOutputClauseIsShared()
    {
        // MERGE reuses the OUTPUT clause the other DML statements already had.
        Assert.Contains(
            "OUTPUT $action",
            Format("merge dbo.tgt as t using dbo.src as s on t.id=s.id when matched then delete output $action;"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MergeTopFilterAndIntoAreKeptAsWritten()
    {
        Assert.StartsWith(
            "MERGE TOP (5) INTO dbo.tgt",
            Format("merge top (5) into dbo.tgt using dbo.src as s on t.id=s.id when matched then delete;"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MergeInsertDefaultValuesFormats()
    {
        Assert.Contains(
            "INSERT DEFAULT VALUES",
            Format("merge dbo.tgt as t using dbo.src as s on t.id=s.id when not matched then insert default values;"),
            StringComparison.Ordinal);
    }

    // --- comments and stability across all four -----------------------------------------

    [Theory]
    [InlineData("CREATE VIEW dbo.v\nAS\n-- why\nSELECT 1 AS x;")]
    [InlineData("CREATE FUNCTION dbo.f()\nRETURNS INT\nAS\nBEGIN\n    -- why\n    RETURN 1;\nEND")]
    [InlineData("CREATE TRIGGER dbo.tr ON dbo.t AFTER INSERT AS\n-- why\nSELECT 1;")]
    [InlineData("MERGE dbo.tgt AS t USING dbo.src AS s ON t.id = s.id\n-- why\nWHEN MATCHED THEN DELETE;")]
    public void CommentsSurviveAndOutputIsStable(string sql)
    {
        var result = Format(sql);

        Assert.Contains("why", result, StringComparison.Ordinal);
        Assert.Equal(result, Format(result));
    }
}
