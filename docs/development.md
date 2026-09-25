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
| `tools/coverage-summary.py` | Merges the per-project Cobertura reports and lists unexecuted lines in the safety gates |
| `tools/mutation-summary.py` | Reads a Stryker report and lists the mutants no test objected to |
| `packaging/` | Manifests submitted to Scoop, WinGet, Homebrew and mason, plus the PyPI wheel builder |
| `mise.toml` | Pinned toolchain — `mise install` and you have it |

## Checking the tests, not the code

Two checks answer questions the suite cannot answer about itself. Neither gates a pull request —
both run in `nightly.yml`, and both are for reading rather than for passing.

**Coverage** exists for one section of its output: which lines of the safety gates does no test
execute? A gate that is never exercised approves everything, and it does so silently.

```sh
dotnet test maxdop.slnx -c Release --collect:"XPlat Code Coverage" --results-directory coverage
python3 tools/coverage-summary.py coverage
```

The percentage is a lookup tool, not a target. Most of `src/` is node handlers, where an uncovered
line means a construct nobody wrote a fixture for; driving that number up by writing tests for the
number is how a suite starts lying.

**Mutation testing** asks the sharper version of the same question: if a gate stopped working, would
anything notice? Stryker breaks the gates on purpose — inverts a comparison, flips a boundary — and
reports every change the tests let through.

```sh
dotnet tool restore
cd tests/Maxdop.Core.Tests && dotnet stryker
python3 ../../tools/mutation-summary.py StrykerOutput
```

Scoped by `tests/Maxdop.Core.Tests/stryker-config.json` to the five gate files, and that scope is
the design. Mutating the node handlers produces about 6,000 mutants whose survivors are mostly
layouts no fixture pins — a coverage gap, not a defect, and enough noise to bury the signal. A
survivor in a gate is different in kind.

Expect about thirty minutes, and expect a score below 100%. Some survivors are cosmetic: which
token a diagnostic anchors to, where a message truncates. `mutation-summary.py` separates those from
the rest, because pinning them would make the suite harder to change without making the formatter
safer. The `break` threshold in the config is a ratchet — currently **78**, set under the **83.17%**
actually achieved, so weakening a gate's tests fails while cosmetic survivors are left alone. Raise it
when the score rises; the headroom absorbs run-to-run variance in how many mutants time out rather
than being killed outright.

**`disable-mix-mutants` is load-bearing. Do not remove it.** It reads like a performance setting
being switched off, and it is the opposite: without it the run reports a score of ~16% with
*literally zero* mutants killed in any file, which looks like a damning result about the test suite
and is entirely an artifact. Stryker activates several mutants per test run when it judges them
independent; the five gate files all sit on one call path — `SqlFormatter` calls `CommentAttacher`,
`BatchFormatter` and `RoundTripVerifier` on every format — so that assumption does not hold here and
results are misattributed. Disabling the mixing is also *faster* in this repo, 30 minutes against 64,
because nothing is re-run.

Proven by bisection on 2026-09-01: mutating `RoundTripVerifier.cs` alone kills 45 and scores 80.3%,
reproducibly. The same file inside a five-file run with mixing on kills 0. Same concurrency, same
tests, same commit.

## Regenerating the demo images

Both images in the README are build artifacts, produced by running the real binary. Neither is a
screenshot someone remembered to retake, and CI fails if either would now show output the formatter
no longer produces.

```sh
cd demos
npm run generate         # the before/after still, via shiki + resvg
npm run terminal         # the terminal recording, via VHS — needs vhs, ttyd, ffmpeg
npm run nvim             # the format-on-save recording — also needs Neovim, which mise pins
npm run helix            # the Helix recording — also needs Helix, which mise pins
```

The Neovim and Helix demos share `demos/terminal/before.sql` and `after.sql` rather than keeping
their own copies, so all three recordings show the same input becoming the same output. That also
means `npm run terminal:check` already covers them, and there are deliberately no `nvim:check` or
`helix:check` steps in CI — they would assert the same thing three times.

Both editor demos take their configuration out of `docs/editors.md` at record time — the Neovim
keymap from the Lua block, Helix's `languages.toml` from the TOML block — so a recording can only
show config the documentation actually publishes, and renaming those sections fails the script.

Both editors are pinned in `mise.toml`, and the record scripts consult that pin ahead of `PATH`, so
`mise install` is the whole setup for either. That is not tidiness. conform's synchronous
format-on-save calls `vim.wait` with a fractional timeout, and a Neovim strict enough to reject that
— every 0.12 nightly so far — errors in `BufWritePre` and writes the file **unformatted**. Recording
against whatever `nvim` happens to be on `PATH` is how the GIF ends up showing a stack trace over a
buffer nothing happened to: broken, but still a valid GIF, so nothing downstream notices.

Environment variables override the pin where that is what you want. `NVIM_BINARY` and `HELIX_BINARY`
name an editor directly, and `CONFORM_NVIM` points at a conform.nvim checkout so the Neovim recording
can be made offline.

`npm run check` and `npm run terminal:check` are the CI halves. They format the sample input with the
real binary and compare it against a committed snapshot, which costs seconds and needs none of the
rendering tools — a GIF's bytes differ on every recording, so comparing the file itself would fail
every run and teach everyone to ignore it.

Each check also asserts that its recording lasts as long as its tape describes, because a recording
can fail without failing. A GIF that stopped filming a third of the way through, and one that caught
an editor's stack trace instead of the editor, are both valid GIFs showing identical formatter
output, so the text comparison is structurally blind to them — and both have now happened. A
truncated one is short by a wide margin and loops back into the middle of the demo, which the length
is enough to catch. `demos/tape-duration.mjs` estimates the tape and reads the GIF's own frame delays
out of its bytes rather than shelling out to `ffprobe`, so this stays inside the same promise of
needing none of the rendering tools.
