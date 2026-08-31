## Casing on Built-in Functions and Globals

Built-in function names and global variables now take keyword casing. Until now `CAST`, `COALESCE`, `NULLIF`, `LEFT`, `RIGHT` and `IIF` were recased while `len` and
`row_number` beside them were not, so a single expression could come out in two cases.

### New Configuration

maxdop now recases from a list of built-in names, and that makes it the only casing decision in
the formatter not derived from the parse tree, which is controllable with a new configuration value:

```json
{
  ...
  "recaseBuiltInFunctions": true
}
```

It follows `keywordCase`, so `"lower"` brings `GETDATE()` and `@@ROWCOUNT` down rather than leaving
casing half-applied.

Setting it to `false` keeps the casing you wrote for everything the list covers — built-in function
names, global variables, the parser-matched table functions such as `STRING_SPLIT`, and the aggregate
a `PIVOT` names — so off means off rather than mostly off.

It does not reach casing that the parse tree proves. `CAST`, `NVARCHAR`, `WITHIN GROUP` and
`DBCC CHECKDB … WITH NO_INFOMSGS` stay cased under `keywordCase` with the switch off, because this
switch exists for the one decision made from a list instead of from the grammar.


Caveats:

- **The call must have no qualifier.** SQL Server requires at least a two-part name to invoke a
  user-defined function, so an unqualified `len(x)` can only ever bind to the built-in. If you have a
  function of your own called `Len`, you reach it as `dbo.Len(x)` — which has a call target and is
  left exactly as you wrote it.
- **The name must be undelimited.** `[len](x)` is untouched. Brackets are how you say "this
  identifier is spelled exactly like this".
- The list is consulted in function-name position only. A column named `len`, a table named
`replace`, an alias named `getdate`, a variable named `@len`. All plain names are untouched.

### Global variables

```sql
IF @@ERROR <> 0 PRINT @@SERVERNAME;   -- @@rowcount, @@trancount, @@fetch_status, …
```

**`DECLARE @@MyVar INT` is legal T-SQL**, and ScriptDom resolves a later reference by 
spelling rather than by scope, so in an expression position a
local `@@MyVar` arrives as the very same `GlobalVariableExpression` that `@@ROWCOUNT` does. Recasing
on the prefix quietly renames that variable under a case-sensitive collation. So the
documented globals are listed by name, and a local variable spelled like a system one keeps every
character you wrote.

## `WITHIN GROUP` is one clause

```sql
-- 0.1.1                                          -- 0.1.2
STRING_AGG(a, ',') within GROUP (ORDER BY b)      STRING_AGG(a, ',') WITHIN GROUP (ORDER BY b)
```

`GROUP` is reserved and lexed as its own token; `WITHIN` is not reserved and lexed as an identifier.
The clause reached the output through a slice that recases neither names nor identifiers — because a
`COLLATE Latin1_General_BIN` can sit in that same region, and a collation name is a name. So half the
clause came up and half did not.

Fixed structurally rather than by spelling: `WithinGroupClause` is a node, so its head is a region the
grammar guarantees holds no name. The collation that shares the old region is not a node in the same
way, and keeps exactly the treatment it had. The clause also gained a layout, breaking at its own
parenthesis when the line is long, the same as `OVER`.


## `PIVOT` aggregates

```sql
-- 0.1.1                                          -- 0.1.2
SELECT SUM(a) FROM t;                             SELECT SUM(a) FROM t;
… PIVOT (sum(amount) FOR m IN ([Jan])) p          … PIVOT (SUM(amount) FOR m IN ([Jan])) p
```

The same split casing as `WITHIN GROUP`, in a different place. `PIVOT` names its aggregate through
`AggregateFunctionIdentifier`, which is a `MultiPartIdentifier` rather than a `FunctionCall`, so the
built-in list never saw it — a `SUM` in the select list came up while a `sum` two lines below did not.


## `DBCC`

```sql
-- 0.1.1                                     -- 0.1.2
dbcc checkdb('MyDb') with no_infomsgs;       DBCC CHECKDB('MyDb') WITH NO_INFOMSGS;
dbcc shrinkfile (MyFileName, 100);           DBCC SHRINKFILE (MyFileName, 100);
```

Administrative statements reach the output through a generic fallback that normalises spacing and
descends into children but deliberately recases nothing, because it cannot tell a keyword from a name
in a construct nobody wrote a handler for. That is still true of `GRANT`, `BACKUP` and the rest, and
it is the right default.

`DBCC` is different because **the parser resolves it to an enum**. `checkdb` and `no_infomsgs` are
non-reserved and lex as `Identifier`, so the tokens prove nothing; but ScriptDom reports
`DbccCommand.CheckDB` and `DbccOptionKind.NoInfoMessages`, which is the parser saying it matched fixed
vocabulary. Only those positions are claimed (the file name in the second line above is an ordinary
identifier and survives verbatim).

One case is excluded on purpose. `DBCC mydll (FREE)` names an extended-procedure library, and
ScriptDom *still* reports `Command = Free`, so the enum alone would have recased somebody's DLL name.
Those statements are handed back untouched.

## Added 7 missing function families

| family | | |
| --- | --- | --- |
| **Graph** | SQL Server 2017 | `NODE_ID_FROM_PARTS`, `EDGE_ID_FROM_PARTS`, `OBJECT_ID_FROM_NODE_ID`, … |
| **Collation** | | `COLLATIONPROPERTY`, `TERTIARY_WEIGHTS` |
| **Regular expression** | SQL Server 2025 | `REGEXP_LIKE`, `REGEXP_REPLACE`, `REGEXP_SUBSTR`, `REGEXP_INSTR`, `REGEXP_COUNT` |
| **Fuzzy string** | SQL Server 2025 | `EDIT_DISTANCE`, `JARO_WINKLER_SIMILARITY`, … |
| **Vector** | SQL Server 2025 | `VECTOR_DISTANCE`, `VECTOR_NORM`, `VECTOR_NORMALIZE`, `VECTORPROPERTY` |
| **AI** | SQL Server 2025 | `AI_TRANSLATE`, `AI_SUMMARIZE`, `AI_CLASSIFY`, … |
| **External** | | `INVOKE_EXTERNAL_API` |

Names that already had a node of their own are still **not** on the list, and this is the rule the
list is kept to: `REGEXP_MATCHES` and `REGEXP_SPLIT_TO_TABLE` return tables and arrive as a
`GlobalFunctionTableReference`, exactly as `STRING_SPLIT` does, so the parser has already matched them.
`AI_GENERATE_EMBEDDINGS`, `AI_GENERATE_CHUNKS` and `VECTOR_SEARCH` have syntax rather than arguments —
`USE MODEL`, `SOURCE = …`, `TABLE = … AS x` — and get nodes of their own. **Those three are still
passed through unchanged; casing them is not in this release.**