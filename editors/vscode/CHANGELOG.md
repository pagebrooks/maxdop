# Changelog

## 0.1.3

No change to formatting. A file formatted by 0.1.2 comes out exactly the same.

- A file that cannot be formatted because it does not parse now says so. The status bar shows
  **maxdop: parse error** while that file is open, with the parser's message in the tooltip; clicking
  it opens the **maxdop** output channel. It clears once the file formats, and it never pops up a
  dialog while you are mid-edit.
- New command: **maxdop: Show Output**.
- Now also published to Open VSX, for VSCodium, Cursor and other editors that use it.
- Bundles maxdop 0.1.3.

## 0.1.2

Formatting changes, so expect a one-time diff the first time you save a file that was last formatted
with 0.1.1.

- Built-in function names now take the configured keyword case: `getdate()` becomes `GETDATE()`,
  `len(a)` becomes `LEN(a)`. Only unqualified, undelimited calls are touched — `dbo.MyFunc(...)`,
  `dbo.Len(...)` and `[len](...)` keep the casing you wrote.
- Global variables too: `@@rowcount` becomes `@@ROWCOUNT`. Your own `DECLARE @@MyVar` is left alone —
  that is legal T-SQL and indistinguishable from a system variable to the parser, so the documented
  globals are recognised by name rather than by the `@@` prefix.
- Both follow `keywordCase`, and both can be switched off with `"recaseBuiltInFunctions": false` in
  `.maxdop.json`.
- `WITHIN GROUP` no longer comes out half-cased as `within GROUP`, and now breaks at its own
  parenthesis on a long line, like `OVER`.
- Comment placement improved in six cases: a comment written before the closing parenthesis of a
  windowed call stayed put instead of moving past it.
- Bundles maxdop 0.1.2.

## 0.1.1

No change to formatting. This version exists so the extension keeps step with the maxdop release it
bundles; the binary inside behaves exactly as 0.1.0 did.

- Bundles maxdop 0.1.1.
- Marketplace listing rewritten — clearer on what the extension does, and on the fact that style is
  configured in `.maxdop.json` rather than in editor settings.

## 0.1.0

First public release.

- Formats T-SQL through [maxdop](https://github.com/pagebrooks/maxdop), built on Microsoft's
  `Microsoft.SqlServer.TransactSql.ScriptDom` parser — stored procedures, `GO` batches, custom
  delimiters and SQL Server 2000-era syntax included.
- Every format is verified against the original before it is returned: the output is re-parsed and
  its tokens, tree and comments compared with the input. On any mismatch the file is left exactly as
  it was and the reason goes to the **maxdop** output channel.
- Format on save, format selection, and format on type are all supported.
- Style is read from a `.maxdop.json` at the root of your repository, so a project formats the same
  way for everyone who opens it. There are no editor-level formatting settings.
- The platform binary is bundled in the extension. Nothing is downloaded on first activation.
