**A T-SQL formatter that runs in CI, understands the whole language, and checks its own work.**

First public release. One static binary, no runtime to install, MIT.

## Why another SQL formatter

Most of them never parse your SQL — they split it into tokens and guess, which holds until the shape
of the code matters. maxdop is built on [ScriptDom](https://github.com/microsoft/sqlscriptdom),
Microsoft's own T-SQL parser, the one behind DacFx and SQL database projects. Twelve grammars, SQL
Server 2000 through 2025 plus Fabric DW. Stored procedures, `GO` batches, custom delimiters and
2000-era syntax all read the way the server reads them.

Microsoft ships two ScriptDom-based formatters of its own, and both live inside an editor. Neither
has a command line, so neither can fail a build. That is the gap this fills: the same binary runs in
your editor and your pipeline, and the style lives in a `.maxdop.json` committed next to the code.

## It verifies its own output

Every format is re-parsed and compared against the input — token stream, tree, and comments. On any
mismatch you get your original file back untouched and a distinct exit code. Below that sits a
byte-level gate: encodings round-trip or the file is not written, so a UTF-16-with-BOM file stays
UTF-16 with its BOM, CRLF stays CRLF, and a Windows-1252 file is declined rather than mangled.

Measured against **2,215 real-world files** — AdventureWorks, WideWorldImporters, the First Responder
Kit, Ola Hallengren's Maintenance Solution, sp_WhoIsActive, and ScriptDom's own parser test suite —
formatted once per configuration option, ten variants each:

| | |
| --- | --- |
| Refused, crashed, or non-idempotent | **0**, in every variant |
| Comments lost | **0** of 11,261 |
| Token coverage, corpus-wide | **98.6%** |
| Per-file median coverage | **100%** |

Those numbers are re-measured nightly in CI, not on a laptop. [How it's measured →](https://github.com/pagebrooks/maxdop/blob/v0.1.0/docs/safety.md)

## Install

Download for your platform, or install **maxdop** from the VS Code Marketplace — the extension
bundles the binary and downloads nothing on first run.

```sh
# Linux x64
curl -fsSL -O https://github.com/pagebrooks/maxdop/releases/download/v0.1.0/maxdop-0.1.0-linux-x64.tar.gz
tar -xzf maxdop-0.1.0-linux-x64.tar.gz && sudo install maxdop-0.1.0-linux-x64/maxdop /usr/local/bin/
```

Builds for `linux-x64`, `linux-arm64`, `linux-musl-x64` (Alpine), `win-x64`, `win-arm64`, `osx-x64`
and `osx-arm64`. ~18 MB, ~3 ms cold start, no .NET runtime, no Node, no installer.

### Verify what you downloaded

Every archive and VSIX carries [build provenance](https://github.com/actions/attest-build-provenance),
signed by the workflow that produced it:

```sh
gh attestation verify maxdop-0.1.0-linux-x64.tar.gz --repo pagebrooks/maxdop
```

`SHA256SUMS` is attached as well. The attestation is the stronger check — whoever could replace the
binaries could rewrite the checksum file alongside them.

> These binaries are not signed with an Apple Developer ID or an Authenticode certificate, so macOS
> Gatekeeper and Windows SmartScreen will warn on a direct download. The Marketplace extension is not
> affected.

## Using it

```sh
maxdop query.sql              # formatted SQL to stdout
maxdop --write src/           # a file or a directory, searched to the bottom
maxdop --check src/           # exit 1 if anything would change
maxdop --parser-version 2016  # pin the grammar, so 2016 code is not reformatted under 2025 rules
cat query.sql | maxdop        # stdin to stdout, how editors call it
```

Point it at directories rather than at a `src/**/*.sql` glob: `globstar` is off in a default bash,
including the shell GitHub Actions runs, so `**` collapses to one directory level and quietly checks
a fraction of your repository.

**Exit codes are the contract with CI.** `0` nothing to do · `1` the input's problem and the file is
untouched · `2` maxdop's problem, please report it.

### Only the files that changed

maxdop has no dependency on git and does not want one — it would mean a `git` binary in every CI
image, and it breaks in shallow checkouts and in Perforce and Mercurial shops. Hand it the list:

```sh
git diff --name-only --diff-filter=ACM -z origin/main | maxdop --check --files-from -
```

Separators are detected, so `-z` and `-print0` output work without a second flag. An empty list exits
0, because a pull request that touched no SQL is the commonest way to reach this command.

### Adopting on a codebase nobody has formatted

`--check` on an established repository fails on every file at once, so nobody turns it on.

```sh
maxdop --write-baseline src/                       # record today's unformatted files
maxdop --check --baseline .maxdop-baseline src/    # green, and gets stricter on its own
```

A baseline entry is the hash of a file's current, unformatted bytes. Edit the file and the hash stops
matching, so it has to be formatted to pass. The count only goes down, as people touch code they were
already touching. It is sorted plain text in `sha256sum` format — reviewable as a diff, mergeable line
by line, and it needs no version control history to work.

## Configuration

One `.maxdop.json` at the repo root; the nearest one at or above the file wins.

```json
{
  "maxWidth": 100,
  "indentSize": 4,
  "useTabs": false,
  "keywordCase": "upper",
  "leadingCommas": false,
  "alwaysBreakSelectList": false,
  "alwaysBreakWhere": false,
  "maxBlankLines": 1,
  "parserVersion": "2022",
  "initialQuotedIdentifiers": false,
  "exclude": ["db/generated/**", "*.gen.sql"]
}
```

There are no editor-level formatting settings, on purpose. A repository formats the same way whoever
opens it.

## Known limitations

- **`--range` is reserved and not implemented.** It is rejected rather than ignored, because
  formatting the whole document when the caller asked for a selection is how "Format Selection ate my
  file" happens. The VS Code extension therefore offers no range formatting.
- **15 comments across 8 files moved** in the corpus run — almost all commented-out DDL inside
  statements that have no handler yet and are emitted verbatim. Nothing is lost; a comment can land on
  the wrong side of such a construct.
- **1.4% of tokens** corpus-wide are still passed through verbatim rather than laid out. They are
  reproduced byte-for-byte, so this is a completeness limit, not a correctness one.
- **Files that do not parse are left alone**, with the reason on stderr. sqlcmd directives
  (`:setvar`, `$(Var)`) are the usual cause. Multi-batch scripts are split at `GO` and formatted
  batch by batch, so one bad batch no longer costs the file.
- No Oracle, MySQL, PostgreSQL or Snowflake, and no plans for them.

## Editors

The VS Code extension bundles the platform binary. Neovim works through
[conform.nvim](https://github.com/stevearc/conform.nvim), and anything that can pipe a buffer through
a command works too — the whole interface is stdin in, stdout out, exit code back.

## Thanks

Built on [ScriptDom](https://github.com/microsoft/sqlscriptdom) (MIT). Measured against public T-SQL
from Microsoft, [Brent Ozar Unlimited](https://github.com/BrentOzarULTD/SQL-Server-First-Responder-Kit),
[Ola Hallengren](https://github.com/olahallengren/sql-server-maintenance-solution) and
[Adam Machanic](https://github.com/amachanic/sp_whoisactive) — fetched for measurement, never
vendored.

Bug reports and corrections welcome. Security issues: see [SECURITY.md](https://github.com/pagebrooks/maxdop/blob/v0.1.0/SECURITY.md).
