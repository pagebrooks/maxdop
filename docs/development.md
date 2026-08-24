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
| `tools/Maxdop.Corpus/` | Dev harness that measures coverage, per option, and ranks what to build next |
| `mise.toml` | Pinned toolchain — `mise install` and you have it |

## Regenerating the demo images

Both images in the README are build artifacts, produced by running the real binary. Neither is a
screenshot someone remembered to retake, and CI fails if either would now show output the formatter
no longer produces.

```sh
cd demos
npm run generate         # the before/after still, via shiki + resvg
npm run terminal         # the terminal recording, via VHS — needs vhs, ttyd, ffmpeg
```

`npm run check` and `npm run terminal:check` are the CI halves. They format the sample input with the
real binary and compare it against a committed snapshot, which costs seconds and needs none of the
rendering tools — a GIF's bytes differ on every recording, so comparing the file itself would fail
every run and teach everyone to ignore it.

## The corpus

`./corpus` is third-party T-SQL fetched for measurement. It is gitignored and never committed;
`corpus/MANIFEST.txt` records what each source is, its licence and the commit it came from.

`tests/corpus/` is different: hand-written files covering every construct maxdop models, committed,
and used by both the test suite and the comment fuzzer.
