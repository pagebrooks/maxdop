**Casing, finished.** Built-in function names and global variables now take keyword casing, and one
clause that came out in two cases at once is fixed.

Everything here is formatter behaviour, so **expect a one-time diff** the first time you run 0.1.2
over a repository formatted with 0.1.1. Nothing about the safety guarantees changed: 2,215
real-world files, every configuration option, still 0 refused, 0 crashed, 0 non-idempotent.

## Built-in functions take keyword casing

```sql
-- 0.1.1                                  -- 0.1.2
select len(a), isnull(c, 0),              SELECT LEN(a), ISNULL(c, 0),
       count(*), getdate(),                      COUNT(*), GETDATE(),
       row_number() over (order by a)             ROW_NUMBER() OVER (ORDER BY a)
from string_split(@s, ',')                FROM STRING_SPLIT(@s, ',')
```

Until now `CAST`, `COALESCE`, `NULLIF`, `LEFT`, `RIGHT` and `IIF` were recased while `len` and
`row_number` beside them were not, so a single expression could come out in two cases. The reason was
never style — it was proof. ScriptDom gives those six a node type of their own, which proves the word
cannot be an object name. `GETDATE()` and `dbo.MyFunc()` are the *same node* with the same
`Identifier` inside, so there was nothing to prove it with.

**maxdop now recases from a list of built-in names**, and that makes it the only casing decision in
the formatter not derived from the parse tree — which is why it is the only one with a switch:

```json
{ "recaseBuiltInFunctions": true }
```

The list is not the whole argument, though. Two conditions around it carry most of the weight:

- **The call must have no qualifier.** SQL Server requires at least a two-part name to invoke a
  user-defined function, so an unqualified `len(x)` can only ever bind to the built-in. If you have a
  function of your own called `Len`, you reach it as `dbo.Len(x)` — which has a call target and is
  left exactly as you wrote it.
- **The name must be undelimited.** `[len](x)` is untouched. Brackets are how you say "this
  identifier is spelled exactly like this".

And the list is consulted in function-name position only. A column named `len`, a table named
`replace`, an alias named `getdate`, a variable named `@len` — all plain names, all untouched.

## Global variables too

```sql
IF @@ERROR <> 0 PRINT @@SERVERNAME;   -- @@rowcount, @@trancount, @@fetch_status, …
```

Same switch, same shape of proof — and a trap worth knowing about, because the obvious rule is wrong.

`@@` looks like it should be proof by itself. It is not: **`DECLARE @@MyVar INT` is legal T-SQL**, and
ScriptDom resolves a later reference by spelling rather than by scope, so in an expression position a
local `@@MyVar` arrives as the very same `GlobalVariableExpression` that `@@ROWCOUNT` does. Recasing
on the prefix would have quietly renamed that variable under a case-sensitive collation. So the
documented globals are listed by name, and a local variable spelled like a system one keeps every
character you wrote.

This needed the round-trip verifier's claim rule widened by exactly one token shape — a keyword claim
could previously land only on an `Identifier`, and `@@ROWCOUNT` lexes as a `Variable`. A claim on a
local `@Amount` is still rejected, loudly.

## `WITHIN GROUP` is one clause again

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

## A comment-placement fix that came with it

Taking `WITHIN GROUP` out of that slice emptied a text test that a comment guard was standing behind,
switching the guard off without changing a line of it. The comment fuzzer — which inserts a comment at
every token boundary of the construct corpus and checks it comes back between the same two tokens —
caught seven sites where a comment then moved.

Restoring the guard fixed those seven **and six pre-existing ones with the same cause**: a comment
written before the `)` of a windowed call used to end up after it. Net, comment placement is better
than 0.1.1 by six sites, and no gate other than the fuzzer would have noticed either direction.

## Configuration

One new key, defaulting to `true`:

```json
{
  "keywordCase": "upper",
  "recaseBuiltInFunctions": true
}
```

It follows `keywordCase`, so `"lower"` brings `GETDATE()` and `@@ROWCOUNT` down rather than leaving
casing half-applied. Set it to `false` to keep 0.1.1 behaviour for these names exactly — including for
`STRING_SPLIT` and the other parser-proved table functions, which ride the same switch so that off
means off rather than mostly off.

## Everything else

No changes to installation, exit codes, encodings, grammar versioning or the safety gates. See the
[0.1.0 notes](https://github.com/pagebrooks/maxdop/blob/v0.1.2/docs/release-notes-0.1.0.md) for
formatter behaviour and [safety.md](https://github.com/pagebrooks/maxdop/blob/v0.1.2/docs/safety.md)
for how the numbers above are measured.
