# Development

## Building

```sh
mise install                      # .NET SDK 10.0.302, Node 22
mise exec -- dotnet build
mise exec -- dotnet test

# the shipping artifact: single native binary, no runtime needed
mise exec -- dotnet publish src/Maxdop.Cli -c Release -r linux-x64 -p:PublishAot=true
```

The toolchain is pinned in `mise.toml` and read by CI from the same file, so a local build and a
pipeline build cannot drift.

## Repo layout

| Path | What |
| --- | --- |
| `src/Maxdop.Core/` | The formatter: doc IR, layout engine, comment attachment, node handlers, verifier |
| `src/Maxdop.Cli/` | The `maxdop` binary (NativeAOT) |
| `editors/vscode/` | The VS Code extension; the binary is embedded, nothing is downloaded |
| `demos/` | Generates the before/after image in the README from the real binary |
| `demos/terminal/` | Records the terminal demo — `docs/images/demo.gif` and `demo.png` — with VHS |
| `demos/nvim/` | Records the format-on-save demo — `docs/images/nvim.gif` and `nvim.png` — with VHS |
| `demos/helix/` | Records the Helix demo — `docs/images/helix.gif` and `helix.png` — with VHS |
| `tools/Maxdop.Corpus/` | Dev harness that measures coverage, per option, and ranks what to build next |
| `packaging/` | Manifests for Scoop, WinGet and mason, plus how each is submitted |
| `mise.toml` | Pinned toolchain — `mise install` and you have it |

## Regenerating the demo images

Both images in the README are build artifacts, produced by running the real binary. Neither is a
screenshot someone remembered to retake, and CI fails if either would now show output the formatter
no longer produces.

```sh
cd demos
npm run generate         # the before/after still, via shiki + resvg
npm run terminal         # the terminal recording, via VHS — needs vhs, ttyd, ffmpeg
npm run nvim             # the format-on-save recording — also needs nvim
npm run helix            # the Helix recording — also needs hx
```

The Neovim and Helix demos share `demos/terminal/before.sql` and `after.sql` rather than keeping
their own copies, so all three recordings show the same input becoming the same output. That also
means `npm run terminal:check` already covers them, and there are deliberately no `nvim:check` or
`helix:check` steps in CI — they would assert the same thing three times.

Both editor demos take their configuration out of `docs/editors.md` at record time — the Neovim
keymap from the Lua block, Helix's `languages.toml` from the TOML block — so a recording can only
show config the documentation actually publishes, and renaming those sections fails the script.

Environment variables exist for those recordings. `CONFORM_NVIM` points at a conform.nvim checkout so
the Neovim one can be made offline, `HELIX_BINARY` pins Helix, and `NVIM_BINARY` pins Neovim. The second is not a convenience:
conform's synchronous format-on-save calls `vim.wait` with a fractional timeout, and a Neovim strict
enough to reject that errors in `BufWritePre` and writes the file **unformatted**. Recording against
an unpinned nightly is how this GIF ends up showing a formatter that did nothing.

`npm run check` and `npm run terminal:check` are the CI halves. They format the sample input with the
real binary and compare it against a committed snapshot, which costs seconds and needs none of the
rendering tools — a GIF's bytes differ on every recording, so comparing the file itself would fail
every run and teach everyone to ignore it.

## The corpus

`./corpus` is third-party T-SQL fetched for measurement. It is gitignored and never committed;
`corpus/MANIFEST.txt` records what each source is, its licence and the commit it came from.

`tests/corpus/` is different: hand-written files covering every construct maxdop models, committed,
and used by both the test suite and the comment fuzzer.
