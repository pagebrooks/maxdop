# Security

## Reporting a vulnerability

Report privately through [GitHub Security Advisories](../../security/advisories/new). Please do not
open a public issue for anything exploitable.

Expect an acknowledgement within a few days. maxdop is maintained by one person, so a fix is a matter
of severity and available evenings rather than a service-level agreement — you will get an honest
estimate rather than a generous one.

## What maxdop does, and does not, do

Most of the attack surface people expect from a SQL tool is not here:

- **It never connects to a database.** There is no driver, no connection string, and no credential
  handling anywhere in the codebase.
- **It never executes SQL.** Your T-SQL is parsed into a syntax tree and printed back as text. It is
  data from the moment it is read to the moment it is written.
- **It makes no network requests.** Not at startup, not on first run, not for telemetry — there is
  none. The only thing that talks to the network is CI.

What it does do is read files you point it at, and — with `--write` — replace them.

## In scope

- Anything that makes maxdop write outside the paths it was given, or write a file it was not asked
  to modify.
- Anything that corrupts a file rather than leaving it untouched. maxdop verifies every format
  against the original and returns your input unchanged on any mismatch; a way around that check is a
  vulnerability, not just a bug.
- Crashes reachable from a `.sql` file, including stack exhaustion from deeply nested input.
- Anything in the VS Code extension that lets a repository decide what code runs — the executable
  path is deliberately `machine`-scoped so a workspace cannot set it.
- Supply-chain issues in the release pipeline: the published binaries carry
  [build provenance](https://github.com/actions/attest-build-provenance) and can be verified with
  `gh attestation verify <file> --repo <owner>/maxdop`.

## Out of scope

- **Output that is ugly rather than wrong.** File a normal issue.
- **A refusal (exit code 2).** That means maxdop declined to touch your file because it could not
  prove its own output equivalent. It is a bug worth reporting as an ordinary issue, and it is the
  safety mechanism working.
- **`.maxdop.json` changing how your SQL is formatted.** That is what the file is for. It cannot
  cause code execution: it is parsed as JSON into a fixed set of options.
- Findings in the third-party SQL that `tools/corpus/fetch.sh` downloads for measurement. That code
  is never committed, never executed, and belongs to its own projects.

## Supported versions

The most recent release. maxdop is a single static binary with no runtime to patch around it, so
upgrading is replacing one file.
