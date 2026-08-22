# CLI

```sh
maxdop query.sql                  # formatted SQL to stdout
maxdop --write src/                # format every .sql under src/, in place
maxdop --check src/                # nonzero if anything would change
maxdop --parser-version 2016      # pin the grammar
maxdop --config path/to.json      # override config discovery
cat query.sql | maxdop            # stdin to stdout
```

## Paths

A path may be a file or a directory. Directories are searched all the way down for `*.sql`, skipping
hidden directories such as `.git`.

**Prefer `src/` to a `src/**/*.sql` glob.** maxdop used to leave expansion to the shell, and that
command did the wrong thing quietly: `globstar` is off in a default bash — including the shell GitHub
Actions runs — so `**` collapses to a single `*` and matches exactly one directory level. A tree with
`src/top.sql`, `src/a/mid.sql` and `src/a/b/deep.sql` handed maxdop only the middle one, and the
check passed. Windows shells expand nothing at all, so the same command arrived as a literal string.
Naming the directory means the same thing in every shell on every platform.

## Excluding files

`exclude` in `.maxdop.json` is a list of globs, relative to that config file's own directory:

```json
{ "exclude": ["db/generated/**", "*.gen.sql", "vendor/"] }
```

| | |
| --- | --- |
| `*`, `?` | Match within one path segment |
| `**` | Spans segments, so `db/**/x.sql` crosses directories |
| no `/` in the pattern | Matches at any depth — `*.gen.sql` finds them anywhere |
| a `/` in the pattern | Anchored to the config file's directory |
| a directory name | Excludes everything beneath it, trailing slash optional |

Matching is case-insensitive on every platform, so a repository does not check differently because
CI runs on Linux and its author is on Windows.

Exclusions apply to every path maxdop is given — a directory walk, a `--files-from` list, and a file
named directly on the command line alike. One rule is easier to reason about than three; naming an
excluded file explicitly says so on stderr rather than doing nothing silently.

## Only the files that changed

maxdop has no dependency on git, and does not want one: it would mean a `git` binary in every CI
image, and it breaks in shallow checkouts, in worktrees, and in Perforce and Mercurial shops. Hand it
the list instead.

```sh
git diff --name-only --diff-filter=ACM -z origin/main | maxdop --check --files-from -
```

`--files-from` takes a path, or `-` for stdin. Entries are separated by newlines, or by NUL if any is
present — so `-z` and `-print0` output works without a second flag, and filenames containing spaces or
newlines survive. It works the same way with `hg status`, a Perforce changelist, a CI action's
changed-files output, or `find -newer`.

An empty list exits 0. A pull request that touched no SQL is the commonest way to reach this
command, and failing it would make the gate unusable.

## Adopting on a codebase that was never formatted

`--check` on an established repository fails on every file at once, so nobody turns it on. The usual
escape is to reformat everything in one commit, which destroys `git blame`, cannot be reviewed, and
conflicts with every branch in flight.

```sh
maxdop --write-baseline src/                        # record today's unformatted files
maxdop --check --baseline .maxdop-baseline src/     # green, and gets stricter over time
```

A baseline entry is the SHA-256 of a file's **current, unformatted** bytes. `--check` forgives a file
that still hashes to its recorded value; edit it at all and the hash stops matching, so it has to be
formatted to pass. The count only goes down, and it goes down as people touch code they were already
touching.

The file is sorted plain text in `sha256sum` format, so it reviews as a diff and merges line by line:

```
c76da3a72ea6b67db49af83e4b7cbb4d55c31467e741a1a53a0f1dfda7d3760d  src/a/b/deep.sql
30436fedd82367bf2fd4ab08c011f68ec31ce2edfe7243d4b31c3791c49359dc  src/a/mid.sql
```

Two deliberate limits:

- **A baseline is never discovered, only named.** It weakens what `--check` means, and a file that
  turned up next to the config would weaken it without anyone deciding to.
- **A refusal is never forgiven.** A baseline covers files that would be reformatted and files that
  do not parse. It does not cover [exit code 2](#exit-codes) — that means maxdop produced output it
  could not prove equivalent to your input, which is a bug report, not a style question.

A single native binary — no .NET runtime, no Node, no installer, ~3.5 ms cold start. Windows, macOS
and Linux, x64 and arm64, plus a musl build for Alpine CI images.

## Exit codes

These are the contract with CI.

| Code | Meaning |
| --- | --- |
| `0` | Formatted, or `--check` found nothing to change. |
| `1` | The input's problem — unparseable, in an encoding that cannot be decoded safely, or `--check` found a file that would change. Look at the file. It is left untouched, except for a multi-batch file where only some batches parse (see below). |
| `2` | maxdop's problem — a refusal, or bad arguments. Please report a refusal. |

## Grammar versioning

`--parser-version` (or `parserVersion` in config) pins the grammar per repo, so a 2016-target
codebase is not silently reformatted under 2025 rules.

It also unlocks syntax that has been removed from the modern grammar. `RAISERROR 50001 'legacy form'`
does not parse under the current parser, but formats fine under `--parser-version 90` or `100`.

## Migration scripts: one bad batch no longer costs the file

Migration scripts are multi-batch by necessity — a `CREATE PROCEDURE` has to begin its batch — and
they routinely carry one batch of sqlcmd syntax (`:setvar`, `$(DatabaseName)`) that no T-SQL parser
accepts.

A file that doesn't parse as a whole is split at its `GO` separators and formatted batch by batch.
Batches that don't parse are copied through byte for byte:

```
$ maxdop V002__add_orders.sql
maxdop: V002__add_orders.sql: formatted 2 of 3 batches; 1 left unchanged because they do not parse.
maxdop:   3:11: Incorrect syntax near '50001'. (error 46010)
```

The safety invariants are unchanged, not relaxed. Every formatted batch goes through the full
verification, and a batch that maxdop *refuses* — as opposed to one that doesn't parse — still
declines the whole file, so a maxdop bug never modifies your file. The assembled result is then
re-tokenised and compared against the input, because a seam between two batches is the one place no
per-batch check can see.

Batch splitting happens **only** when the whole file fails to parse, so a file that formats today is
unaffected by any of it. `GO 5` — sqlcmd's repeat count, which the T-SQL grammar rejects outright —
formats completely, count preserved.
