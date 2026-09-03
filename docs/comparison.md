# Comparison

Several of these are much larger products. This table only refers to the formatting capabilities.

| | Cost for all features | Parser | Repo-wide config | CLI for CI | Platforms |
| --- | --- | --- | --- | --- | --- |
| **maxdop** | **Free — MIT, nothing withheld** | ScriptDom | `.maxdop.json` | **Yes** | Win / macOS / Linux / musl, x64 + arm64 |
| [SSMS 22.7 formatter](https://learn.microsoft.com/en-us/ssms/scripting/format-t-sql) (Preview) | Free with SSMS | ScriptDom | `.editorconfig` | Not documented | Windows only |
| [mssql (Microsoft)](https://marketplace.visualstudio.com/items?itemName=ms-mssql.mssql) | Free — MIT | **ScriptDom** (preview, on by default) | `.vscode/settings.json` — 41 settings, VS Code only | No | cross-platform, x64 + arm64 |
| [SQL Formatter](https://marketplace.visualstudio.com/items?itemName=ReneSaarsoo.sql-formatter-vsc) / [Prettier SQL](https://marketplace.visualstudio.com/items?itemName=inferrinizzard.prettier-sql-vscode) | Free — MIT | `sql-formatter` (token-based) | yes | yes (npm) | cross-platform |
| [SQLTools](https://marketplace.visualstudio.com/items?itemName=mtxr.sqltools) | Free — MIT | `@sqltools/formatter` | — | No | cross-platform |
| [Poor Man's T-SQL Formatter](https://poorsql.com) | Free — **AGPL** | own (token-based) | — | Yes | .NET + JS, cross-platform |
| [SQL Pretty Printer](https://www.dpriver.com/products/sqlpp/desktop_index.php) | **$** — SDK costs extra | own | — | SDK | Windows desktop, add-ins, online |
| [SQLinForm](https://www.sqlinform.com) | **$** — free tier has fewer options | own | — | Pro | N++, VS Code, SSMS, JetBrains, desktop |
| [Devart SQL Complete](https://www.devart.com/dbforge/sql/sqlcomplete/) | **$$** — free Express is editor-only | own | profiles | **Paid tiers only** | SSMS + Visual Studio |
| [Redgate SQL Prompt](https://www.red-gate.com/products/sql-prompt/) | **$$$** per seat, per year | own | style files | **Yes** | SSMS + Visual Studio; no VS Code |


# Microsoft's ScriptDom-based Formatters

SSMS 22.7 and Microsoft's mssql VS Code extension both run ScriptDom. The extension ships
`Microsoft.SqlServer.TransactSql.ScriptDom.dll` inside its language service and turns its new
formatter on by default.

**A formatter with no command line cannot gate a pull request.** Microsoft's runs inside a
language-service process whose reason to exist is serving an editor. There is no `--check`, no exit
code, nothing to put in CI. Style becomes a team's style only when something fails the build. maxdop
is the same binary in your editor and your pipeline, and `.maxdop.json` is committed next to the code
it formats rather than living in each developer's editor settings.

**Nothing else verifies its own output.** Every other tool here formats and hands the result back.
maxdop re-parses what it produced, compares its significant token stream and its comments against the
input — identical tokens mean an identical tree — and returns your file untouched if anything differs. That is a stance about who carries the risk, not a feature
on a roadmap — a formatter that declines to format is a support ticket for a vendor and a promise
kept for a linter. See [Safety](safety.md).

**One file, nothing to acquire.** The mssql extension carries a 292 MB portable service that is
framework-dependent — its `runtimeconfig.json` asks for `Microsoft.NETCore.App 10.0.0` — so a .NET
runtime has to be present, which is what the bundled `vscode-dotnet-runtime` extension is for. That
figure is one measurement of one version, taken in August 2026, and is quoted to make a point about
architecture rather than about a number; expect it to change. maxdop
is one static ~18 MB executable with no runtime, including a musl build. It runs on an Alpine CI
image and on an air-gapped build agent, where an acquisition step is not an option.

## Token-based formatters

Token-based formatters split the text into tokens without building a grammar. They get lexical facts
right and start to fall apart on structural ones. Real outputs:

<table>
<thead>
<tr><th align="left">you wrote</th><th align="left"><code>sql-formatter</code> 15.8.2, <code>tsql</code></th><th align="left"><code>maxdop</code></th></tr>
</thead>
<tbody>
<tr>
<td valign="top">

```sql
declare @i int
set @i = 1
if @i = 1 print 'one'
else print 'other'
select @i
```

</td>
<td valign="top">

```sql
-- formatted, but not better
declare @i int
set
  @i = 1 if @i = 1 print 'one' else print 'other'
select
  @i
```

</td>
<td valign="top">

```sql
DECLARE @i INT
SET @i = 1
IF @i = 1
    PRINT 'one'
ELSE
    PRINT 'other'
SELECT @i
```

</td>
</tr>
<tr>
<td valign="top">

```sql
if @a = 1
begin
if @b = 2
begin
select 1;
end
end
```

</td>
<td valign="top">

```sql
-- nesting lost
if @a = 1 begin if @b = 2 begin
select
  1;

end end
```

</td>
<td valign="top">

```sql
IF @a = 1
BEGIN
    IF @b = 2
    BEGIN
        SELECT 1;
    END
END
```

</td>
</tr>
<tr>
<td valign="top">

```sql
SELECT 1 << 1 >> 1;
SELECT 'a' || 'b';
```

</td>
<td valign="top">

```sql
-- no longer valid SQL
SELECT
  1 < < 1 > > 1;

SELECT
  'a' | | 'b';
```

</td>
<td valign="top">

```sql
SELECT 1 << 1 >> 1;
SELECT 'a' || 'b';
```

</td>
</tr>
</tbody>
</table>

## Casing needs a parser too

Uppercasing keywords looks like a lexical job, and for `SELECT` and `FROM` it is. The rest of it is
not, because T-SQL's non-reserved words lex as ordinary identifiers — the token stream cannot tell
`sum` the aggregate from `sum` the column, or `checkdb` the `DBCC` command from a table called
`checkdb`. Getting those right means asking the parse tree, and getting them *wrong* in the other
direction means renaming somebody's object under a case-sensitive collation.

Both tools below were asked to upper-case keywords. Real outputs:

<table>
<thead>
<tr><th align="left">you wrote</th><th align="left"><code>sql-formatter</code> 15.8.2, <code>tsql</code>, <code>keywordCase: upper</code></th><th align="left"><code>maxdop</code></th></tr>
</thead>
<tbody>
<tr>
<td valign="top">

```sql
select len(a), getdate(),
  row_number() over (order by a),
  sum(x)
from t pivot (sum(a)
  for b in ([x])) p;
dbcc checkdb('db')
  with no_infomsgs;
```

</td>
<td valign="top">

```sql
SELECT
  len(a),
  getdate(),
  row_number() OVER (
    ORDER BY
      a
  ),
  sum(x)
FROM
  t PIVOT (sum(a) FOR b IN ([x])) p;

DBCC checkdb ('db')
WITH
  no_infomsgs;
```

</td>
<td valign="top">

```sql
SELECT LEN(a), GETDATE(), ROW_NUMBER() OVER (ORDER BY a), SUM(x)
FROM t PIVOT (SUM(a) FOR b IN ([x])) p;
DBCC CHECKDB('db') WITH NO_INFOMSGS;
```

</td>
</tr>
</tbody>
</table>

Every word the token-based formatter left alone is one it had no way to classify. maxdop reaches them
from three different kinds of proof, and declines where it has none:

- **The parse tree**, for anything with a node of its own — `CAST`, `COALESCE`, and the table
  functions the parser has already matched, such as `STRING_SPLIT`.
- **The parser's enums**, for `DBCC`: `checkdb` resolves to `DbccCommand.CheckDB`, which is ScriptDom
  saying it matched fixed vocabulary rather than read a name.
- **A published list of built-in names**, for the 283 functions that arrive as an undifferentiated
  call — `LEN`, `GETDATE`, and the graph, vector, regex and AI families. This is the one casing
  decision in the formatter not derived from the parse tree, so it is the one with a switch
  (`recaseBuiltInFunctions`), and it applies only to an unqualified, undelimited name: `dbo.Len(x)`
  and `[len](x)` are left exactly as written.

Where none of the three applies, nothing is recased. `GRANT`, `BACKUP` and the rest of the
administrative surface keep the casing you gave them, because in a construct with no handler there is
no way to tell a keyword from an object name, and half-cased output is worse than none.
