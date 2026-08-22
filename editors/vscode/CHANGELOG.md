# Changelog

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
