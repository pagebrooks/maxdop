-- The Neovim the recording runs. Not a config anyone should copy wholesale: the
-- formatter block is the part that matters and is the same one docs/editors.md
-- tells people to use, verbatim. Everything else exists to keep the frame clean.
--
-- Loaded with `nvim -u`, and XDG_* point into the staging directory, so whatever
-- config the person recording happens to have is not involved.

-- Space, so the recording can show a keypress a viewer will recognise.
vim.g.mapleader = " "

vim.opt.swapfile = false
vim.opt.number = true
vim.opt.signcolumn = "no"
vim.opt.laststatus = 2
vim.opt.showmode = false
vim.opt.termguicolors = true
vim.opt.shortmess:append("I") -- no intro screen; the file should be the first thing on screen
vim.cmd.colorscheme("habamax")

-- conform.nvim, cloned by render.mjs. Prepended rather than appended so nothing
-- on the recorder's runtimepath can shadow it.
vim.opt.runtimepath:prepend(vim.env.CONFORM_PATH)

require("conform").setup({
  formatters_by_ft = { sql = { "maxdop" } },
  formatters = {
    maxdop = {
      command = "maxdop",
      args = { "--stdin-filepath", "$FILENAME" },
      -- Exit 1 means the input has a problem, and maxdop's output on that path is
      -- still safe to write. Exit 2 is maxdop's bug and is deliberately excluded.
      exit_codes = { 0, 1 },
    },
  },
  format_on_save = { timeout_ms = 3000, lsp_format = "never" },
})

-- The selection keymap is not written here. render.mjs appends it, lifted out of
-- docs/editors.md, so the recording can only ever show the code the documentation
-- actually publishes — and breaks loudly if that snippet stops working.
