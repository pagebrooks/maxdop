# Editors

The whole interface is stdin in, stdout out, exit code back. Every integration below spawns the same
binary you run in CI.

## VS Code

Install the extension. It bundles the binary for your platform — nothing is downloaded on activation.

If you also have Microsoft's [mssql](https://marketplace.visualstudio.com/items?itemName=ms-mssql.mssql)
extension installed, both register a formatter for SQL and VS Code needs to know which one to use.
Installing maxdop makes it the default; to hand formatting back to mssql, set it explicitly:

```jsonc
"[sql]": { "editor.defaultFormatter": "ms-mssql.mssql" }
```

Either way, **maxdop: Format Document** in the Command Palette always formats with maxdop, so you can
keep mssql on format-on-save and still reach for this one deliberately.

Everything that isn't formatting — connections, IntelliSense, query execution — is untouched.

## Neovim

With [conform.nvim](https://github.com/stevearc/conform.nvim):

```lua
require("conform").setup({
  formatters_by_ft = { sql = { "maxdop" } },
  formatters = {
    maxdop = {
      command = "maxdop",
      args = { "--stdin-filepath", "$FILENAME" },

      -- conform defaults to exit_codes = { 0 }. Exit 1 means "the input has a problem", and
      -- maxdop's output on that path is still safe to write: an unparseable file comes back
      -- byte for byte, and a multi-batch file comes back with the batches that did parse
      -- formatted. Accepting 1 keeps both. Exit 2 is deliberately excluded — that is maxdop's
      -- bug, and it should surface as an error rather than silently doing nothing.
      exit_codes = { 0, 1 },
    },
  },
})
```

No language server is involved, and none is needed for formatting.

## Plain Vim, and the one trap in it

```vim
:%!maxdop --stdin-filepath % 2>/dev/null
```

**Keep the `2>/dev/null`.** Vim's filter merges the command's stderr into the buffer, so on a file
that does not parse the diagnostic is pasted into your SQL:

```sql
select from where;
maxdop: bad.sql: could not be parsed... Incorrect syntax near 'from'. (error 46010)
```

Setting `shellredir` is not enough; the redirect has to be in the filter command itself. This is why
a plugin is the better path — conform reads the two streams separately and cannot do this.

## Why `--stdin-filepath`

It is how the formatter finds the `.maxdop.json` belonging to *that* file's repo, rather than to
wherever the editor happened to be launched. Prettier takes the flag for the same reason.
