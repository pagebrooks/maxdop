# Editors

## VS Code

Simply, install the [Maxdop extension](https://marketplace.visualstudio.com/items?itemName=pbrooks.maxdop) from the Visual Studio Marketplace.

* From Code Quick Open `Ctrl+P`, paste the following command, and press enter.

  ```
  ext install pbrooks.maxdop
  ```

* Once the extension is installed, you can set the default formatter in your `Settings.json` file:

  ```jsonc
  "[sql]": { "editor.defaultFormatter": "pbrooks.maxdop",
             "editor.formatOnSave": true
  }
  ```

  ***Note***: If you also have Microsoft's [mssql](https://marketplace.visualstudio.com/items?itemName=ms-mssql.mssql)
  extension installed, both register a formatter for SQL and VS Code needs to know which one to use.


* You can also manually run **maxdop: Format Document** in the Command Palette.

## Neovim

![Neovim reformatting a stored procedure on :w, through conform.nvim and maxdop](images/nvim.gif)

Saving is the whole interaction — no keymap, no command, no language server. With
[conform.nvim](https://github.com/stevearc/conform.nvim):

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

To format on save, as the recording above does:

```lua
require("conform").setup({
  format_on_save = { timeout_ms = 3000, lsp_format = "never" },
})
```

### Custom Keymapping

To have nvim format on a custom keymapping, leave `format_on_save` out of `setup()` and bind it yourself. Nothing else changes, the formatter
block above is the same:

```lua
vim.keymap.set("n", "<leader>f", function()
  require("conform").format({ async = true, lsp_format = "never" })
end, { desc = "Format with maxdop" })
```

### Formatting just the selection

Normal mode above formats the entire buffer. Visual mode needs its own function, because conform's answer to
a formatter with no `--range` is to format the **whole buffer** and apply the diff hunks that overlap
your selection — and the unit it applies is the hunk, not the line. Select two lines inside a
procedure maxdop rewrites wholesale and the whole procedure comes with them.

This sends only the selected lines to maxdop instead. A selection that is not a complete statement
does not parse, and you are told so rather than handed an edit you did not ask for:

```lua
local function format_selection()
  local first, last = vim.fn.line("v"), vim.fn.line(".")
  if first > last then first, last = last, first end

  local lines = vim.api.nvim_buf_get_lines(0, first - 1, last, false)
  local indent = lines[1]:match("^%s*")

  -- The buffer's own path, so .maxdop.json is discovered exactly as it would be
  -- when formatting the whole file.
  local name = vim.api.nvim_buf_get_name(0)
  local res = vim.system(
    { "maxdop", "--stdin-filepath", name ~= "" and name or "selection.sql" },
    { stdin = table.concat(lines, "\n") .. "\n" }
  ):wait()

  -- Exit 1 means the selection did not parse. maxdop echoes its input back on that
  -- path, so writing stdout would silently do nothing at all — refuse instead.
  if res.code ~= 0 then
    vim.notify("maxdop: selection is not formattable on its own\n" .. vim.trim(res.stderr),
      vim.log.levels.WARN)
    return
  end

  -- maxdop formats whatever it is given as a document, at column 0. Put the block
  -- back where it was; the indentation *inside* it is maxdop's.
  local out = vim.split((res.stdout:gsub("\n$", "")), "\n")
  for i, line in ipairs(out) do
    out[i] = line == "" and line or indent .. line
  end

  vim.api.nvim_buf_set_lines(0, first - 1, last, false, out)
  vim.api.nvim_feedkeys(vim.api.nvim_replace_termcodes("<Esc>", true, false, true), "n", false)
end

vim.keymap.set("x", "<leader>f", format_selection, { desc = "Format selection with maxdop" })
```

Select a whole statement and it is formatted in place, with nothing outside the selection touched.
Select half of one and you get a warning naming the line the parser stopped at, and the buffer is
left alone — which is the same bargain the CLI makes, and the reason `--range` does not exist.

***Limitations***: maxdop lays out what it is handed as a complete document, so the block is
re-indented to the first selected line's indentation and its internal indentation starts from there;
selecting an inner block of a procedure will not keep the procedure's nesting. And `vim.system`
needs Neovim 0.10 or newer.

## Vim

```vim
:%!maxdop --stdin-filepath % 2>/dev/null
```

**Important Notes**: 
* Vim's filter merges the command's stderr into the buffer, so on a file
that does not parse, the diagnostic is pasted into your SQL:

  ```sql
  select from where;
  maxdop: bad.sql: could not be parsed... Incorrect syntax near 'from'. (error 46010)
  ```

* `--stdin-filepath` is how the formatter finds the `.maxdop.json` belonging to *that* file's repo, rather than to
wherever the editor happened to be launched.



## Helix

![Helix piping a selection through maxdop, then formatting the file on write](images/helix.gif)

Helix needs two lines in your `languages.toml`:

```toml
[[language]]
name = "sql"
formatter = { command = "maxdop", args = ["--stdin-filepath", "%{buffer_name}"] }
auto-format = true
```


`:format` runs maxdop on demand. `%{buffer_name}` is Helix's expansion for
the current file, and it is what lets maxdop find the `.maxdop.json` governing that file; without it
the search starts from Helix's working directory instead, which might not be the desired location.

`auto-format` runs it on `:w`; 

### Formatting one statement

If the selected lines are not a complete statement, maxdop cannot parse them, and it writes your input back out unchanged. 