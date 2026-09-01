# Continuous Integration Setup 

`--check` writes nothing and exits nonzero if any file would change. That is the whole of what a gate
needs, and it is the reason maxdop is a command-line tool first: a formatter that lives only inside an
editor cannot fail a pull request, so its style stays a suggestion.

Everything below runs on GitHub Actions. The same two commands work on any runner.

## The short version

```yaml
name: sql-format

on:
  push:
  pull_request:

permissions:
  contents: read

jobs:
  maxdop:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
      - uses: astral-sh/setup-uv@v10.0.1
      - run: uvx maxdop@0.1.2 --check src/
```

`uvx` fetches the platform wheel — a ~7 MB static binary, no Python code and no .NET runtime — and
runs it. Nothing is added to your repository and nothing is installed system-wide.

**Pin the version.** `uvx maxdop` unpinned means the next maxdop release changes your formatting and
reds every open pull request until somebody reformats the tree. `maxdop@0.1.2` makes that a decision
you make in a commit.

Pin the action too. `astral-sh/setup-uv` no longer publishes floating `@v10`-style tags, so an exact
tag or a commit SHA is the only thing that resolves — check its releases for the current one rather
than copying the tag above forever.

## Only the files a pull request touches

On a large repository, checking the whole tree on every push is wasteful and — worse — turns an
unrelated legacy file into your problem the moment somebody edits a directory near it.

```yaml
      - uses: actions/checkout@v7
        with:
          fetch-depth: 0          # the default shallow clone has no merge base

      - uses: astral-sh/setup-uv@v10.0.1

      - name: Check the .sql files this pull request touches
        run: |
          git diff --name-only --diff-filter=ACM -z \
            "origin/${{ github.base_ref }}...HEAD" -- '*.sql' \
          | uvx maxdop@0.1.2 --check --files-from -
```

Two details decide whether this works.

**`--diff-filter=ACM` is not optional.** A path the branch deleted is still named by
`git diff --name-only`, and handing maxdop a file that is not there is exit code 2 — a red build
caused by deleting a file. Restricting to added, copied and modified paths is the fix; it is the form
[`cli.md`](cli.md#only-the-files-that-changed) uses, and a rename arrives as an add so nothing is
missed.

**You do not need to guard against an empty list.** With no paths on stdin maxdop prints
`no .sql files to check` and exits 0, so the usual `if [ -n "$FILES" ]` wrapper is unnecessary. This
is deliberate: a gate that fails when nothing changed is a gate people route around.

`-z` and `--files-from -` are NUL-separated, so paths containing spaces or newlines survive.

## Adopting a codebase that was never formatted

One reformat commit across a mature repository is unreviewable and destroys `git blame` for every
line it touches. The alternative is to record the backlog and gate everything else:

```sh
maxdop --write-baseline src/     # records every file that would change, exits 0
git add .maxdop-baseline
```

```yaml
      - run: uvx maxdop@0.1.2 --check --baseline .maxdop-baseline src/
```

New files and edited files are gated from the first day. A file in the baseline is forgiven until
somebody changes it, at which point it has to come into line. The backlog shrinks as the code is
touched, rather than in one commit nobody can read.

## Reading the result

```
0   nothing to do
1   a file would be reformatted, or a file could not be parsed
2   maxdop's own problem — bad arguments, an unreadable file
```

Both failures name the path, and a parse failure gives the position:

```
maxdop: src/rpt.sql would be reformatted
maxdop: src/bad.sql: could not be parsed, so it was left unchanged. 1:8: Incorrect syntax near 'from'. (error 46010)
```

**Exit 1 covers two different situations**, and they are not equally serious: a file that would be
reformatted is a style nit, and a file that does not parse is usually a real defect — or a dialect
maxdop's grammar version does not cover, which [`--parser-version`](cli.md#grammar-versioning) exists
for. If you want them treated differently, run `--check` twice, or split the step, rather than
reading exit 1 as one thing.

Nothing here rewrites your files: `--check` reports and stops. Even `--write` refuses a file it cannot
re-parse and token-compare against the input — see [Safety](safety.md).

## Without Python: the binary

`uvx` is the shortest route, not the only one. If you would rather not have a Python toolchain in the
job, or you want to verify what you are running, download the release asset — and cache it:

```yaml
      - name: Cache maxdop
        id: maxdop
        uses: actions/cache@v6
        with:
          path: ~/.cache/maxdop
          key: maxdop-0.1.2-linux-x64

      - name: Download maxdop
        if: steps.maxdop.outputs.cache-hit != 'true'
        env:
          MAXDOP_VERSION: 0.1.2
          GH_TOKEN: ${{ github.token }}
        run: |
          set -euo pipefail
          cd "$RUNNER_TEMP"
          gh release download "v$MAXDOP_VERSION" --repo pagebrooks/maxdop \
            --pattern "maxdop-$MAXDOP_VERSION-linux-x64.tar.gz" \
            --pattern SHA256SUMS
          sha256sum --ignore-missing -c SHA256SUMS
          mkdir -p "$HOME/.cache/maxdop"
          tar -xzf "maxdop-$MAXDOP_VERSION-linux-x64.tar.gz" \
            -C "$HOME/.cache/maxdop" --strip-components=1

      - run: echo "$HOME/.cache/maxdop" >> "$GITHUB_PATH"

      - run: maxdop --check src/
```

`SHA256SUMS` covers every asset in the release, so `--ignore-missing` verifies the one you pulled and
ignores the rest.

### Why the cache is not optional here

`gh release download` talks to the GitHub REST API, and in Actions that API is rated at **1,000
requests per hour per repository** for `GITHUB_TOKEN` — 15,000 on Enterprise Cloud. That budget is not
per workflow or per job: every job in the repository draws on the same pool, so a matrix across seven
platforms, several open pull requests, and whatever else in your workflows calls `gh` all spend from
one allowance. Without `GH_TOKEN` set at all, `gh` falls back to unauthenticated requests and the
limit is **60 per hour**, which one busy afternoon will exhaust.

Being rate-limited surfaces as a red build in the install step, on a pull request that changed no SQL,
which then goes green on re-run. That is the most expensive kind of CI failure: intermittent,
unrelated to the change, and self-healing just often enough that nobody tracks it down.

The cache turns N downloads per hour into one per version. Three details make it work:

- **The key carries the version and the platform.** `maxdop-0.1.2-linux-x64` misses the moment you
  bump the pin, so the new binary is fetched once and then reused; there is no stale-cache failure
  mode to reason about, and no `restore-keys` fallback that could serve you the old version.
- **`--strip-components=1` keeps the cached layout version-independent**, so the binary is always at
  `~/.cache/maxdop/maxdop` and the `PATH` step needs no version substituted into it.
- **Putting it on `PATH` is a separate step, outside the `if:`.** The download step is skipped on a
  cache hit, so anything it exported would be skipped too — which is the usual way this pattern is
  got wrong.

One thing to expect on the first few runs: Actions caches are scoped by branch. A cache written on the
default branch is readable from every branch, but one written on a feature branch is not visible to
sibling branches. Until the workflow has run once on your default branch, pull requests will each
download their own copy and it will look as though the cache is doing nothing.

This is one more argument for the `uvx` route above, which fetches from PyPI rather than the GitHub
API and so is not subject to this limit at all. `setup-uv` also caches uv's downloads by default —
its `enable-cache` input is `auto`, meaning on for GitHub-hosted runners.

A checksum published beside a file only proves the two match. To prove the binary came out of the
release workflow rather than from someone with write access to the release page, verify its build
provenance as well — insert before the `tar` line:

```sh
gh attestation verify "maxdop-$MAXDOP_VERSION-linux-x64.tar.gz" --repo pagebrooks/maxdop
```

## Other runners

The archive name is `maxdop-<version>-<rid>`, where the RID is one of `linux-x64`, `linux-arm64`,
`linux-musl-x64`, `win-x64`, `win-arm64`, `osx-x64` or `osx-arm64`. Windows builds are `.zip` rather
than `.tar.gz`; everything else is identical.

The Linux glibc builds are compiled against glibc 2.28, so they run on RHEL 8, Ubuntu 20.04 and
Amazon Linux 2 as well as current images. `linux-musl-x64` is for Alpine.

`uvx` handles all of this for you, which is the argument for the short version above.

## Not GitHub Actions

`--check` and `--files-from` are the whole interface; there is nothing GitHub-specific in any of it.
For a pre-commit hook rather than a CI step, see the pre-commit section of the
[README](../README.md) — that route installs the binary itself, so contributors need nothing
beforehand.
