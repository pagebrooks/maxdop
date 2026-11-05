---
name: prepare-release
description: Prepare the next maxdop release. Survey what has landed since the last tag, triage open findings (nightly-red issues, Dependabot alerts, the .NET security canary), fix what should ship, verify, write the version bumps and release notes in a worktree, then hand back for the maintainer to commit, merge and tag. Covers patch, security and feature releases. Run when the maintainer asks; it never commits, pushes or tags.
disable-model-invocation: true
---

# Prepare a release

Any maxdop release: a security patch, a batch of dependency bumps, or new formatting behaviour. The
end state is a worktree holding the release prep, plus any fixes that have to ship with it, all
verified. The hand-off should let the maintainer commit, merge and tag without re-deriving anything.

The maintainer may say what the release is for ("ship the SDK fix", "release what's on main"). If
not, work it out in step 1 and propose it.

## Ground rules

- **Never** `git commit`, `git push`, merge, open a PR, push a tag, or delete a branch. The
  maintainer does all of that. Leave the work uncommitted and say which worktree it is in.
- Work in a worktree under `.claude/worktrees/`, created from `origin/main` after a `git fetch`,
  on the branch `feature/pb/release-X.Y.Z`. Use one worktree for the whole release.
- All toolchains through mise (`mise exec -- dotnet …`, `mise exec -- npm …`). Never install an SDK
  system-wide.
- Run `mise exec -- dotnet format` after any C# change.
- Report results as they are. A skipped check is reported as skipped, and a failing one with its
  output.

## 1. Survey

### What has landed since the last tag

```bash
git fetch --tags origin
prev="$(git tag --sort=-v:refname | head -1)"
git log --oneline "$prev"..origin/main
gh pr list --state merged --base main --search "merged:>=$(git log -1 --format=%cs "$prev")" \
  --json number,title,author --jq '.[] | "#\(.number) \(.title) (\(.author.login))"'
git diff --stat "$prev"..origin/main -- src/ editors/vscode/src/ editors/vscode/package.json
```

Sort it into what users see and what they don't:

- **Formatter or CLI behaviour** (`src/`): layout changes, new options, new diagnostics, parser bumps.
- **VS Code extension** (`editors/vscode/src`, `package.json` contributions).
- **Packaging and install**: archives, wheels, channels.
- **Security**: anything that ships in the binary or the VSIX, with its advisory ID.
- **Build, test and CI only**: Dependabot bumps of dev tools, workflows. These usually rate one line
  in the notes, or nothing.

### Open findings

```bash
gh issue list --label nightly-red --state open
gh api 'repos/{owner}/{repo}/dependabot/alerts?state=open' \
  --jq '.[] | "\(.number) \(.security_advisory.severity) \(.dependency.package.ecosystem) \(.dependency.package.name) \(.security_advisory.ghsa_id)"'
gh pr list --author app/dependabot --state open
gh run list --workflow nightly.yml -L 5
gh run list --workflow release.yml -L 3          # includes the Sunday dry run
python3 tools/dotnet-security-check.py
mise exec -- dotnet list maxdop.slnx package --vulnerable --include-transitive
(cd editors/vscode && npm audit --audit-level=moderate)
```

For each red run, `gh run view <id> --log-failed` gives the cause. A nightly-red issue links its run.
Open findings should normally be fixed before the release. Shipping a release with a known advisory
in the binary needs the maintainer's say-so.

### Propose the release

Show the maintainer:

1. The unreleased changes, sorted as above.
2. The open findings, each with a proposed fix and whether it ships in a binary or VSIX or is
   build-only.
3. The proposed version, with the reason for it:
   - **Patch** (`0.1.4` → `0.1.5`): fixes, security and dependency updates, and no change to
     formatting output or to the CLI's options.
   - **Minor** (`0.1.x` → `0.2.0`): any intended change to formatting output, a new option or config
     key, or a changed default. Before 1.0 a minor is what tells users to expect a behaviour change.
   - The maintainer decides major and pre-1.0 graduation. Never propose them yourself.

If the release is fixes only and the maintainer asked for exactly that, carry on. Otherwise wait
for the maintainer to confirm the version and the scope (which findings to fix now, and anything to
hold back) before writing anything.

## 2. Fix what has to ship

Only the findings the maintainer agreed to. Feature work is out of scope for this skill: if
something unfinished needs to be in the release, say so and stop.

### .NET runtime (the canary, or NU1903 on `Microsoft.DotNet.ILCompiler`)

The AOT binaries compile in the runtime that the pinned SDK bundles. The fix is always the SDK pin:

1. Use the SDK that `tools/dotnet-security-check.py` names. Its source is
   `https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json`.
2. Update `dotnet = "…"` in `mise.toml` and the version in `docs/development.md` (`mise install` line).
3. `mise install`. Then AOT-publish linux-x64 and confirm that `src/Maxdop.Cli/obj/project.assets.json`
   lists the patched `Microsoft.DotNet.ILCompiler`.
4. If the feature band changed (for example 3xx to 4xx, because the band stopped receiving SDKs), run
   the whole suite. A band jump can add analyzers, and `TreatWarningsAsErrors` is on.

CI reads the pin from `mise.toml` and enforces it with a generated `global.json`, so no workflow
edit is needed. The musl leg is the exception: it builds in `mcr.microsoft.com/dotnet/sdk:10.0-alpine`.

### npm (`editors/vscode`)

- If an open Dependabot PR fixes it, say so and let the maintainer merge it first. Then rebase the
  worktree.
- `npm update <pkg>` when the parent's range allows the fixed version.
- When a parent pins a vulnerable range that no release of the parent has lifted, add an `overrides`
  entry to `editors/vscode/package.json` (precedent: minimatch, GHSA-g3hv-75wg-m249) and regenerate
  the lock with `npm install`. Check that `npx vsce ls` lists the same files as before.
- Then `npm audit --audit-level=moderate`, and `npm ci && npm test` (`mise exec --`).
- The extension has no runtime npm dependencies, so npm fixes are build tooling. The notes say so.
- Do not bump `@types/vscode`, `@types/node` majors or `typescript` majors. See `.github/dependabot.yml`.

### NuGet

Versions are inline in each `.csproj`. ScriptDom alone is the `<ScriptDomVersion>` property in
`Directory.Build.props`. Bump, run `dotnet format`, run the tests.

A ScriptDom bump changes the parser. Follow the ratchet: regenerate
`tests/Maxdop.Core.Tests/GrammarSurface.txt` from the new package (concrete `TSqlFragment`
subclasses, ordinal-sorted, one per line, trailing newline; a .NET file-based script with
`#:package Microsoft.SqlServer.TransactSql.ScriptDom@<ver>` reproduces it). Name the new node types
in the notes, and do the corpus comparison in step 3.

### Test tools (`.config/dotnet-tools.json`)

Stryker majors change config validation (Stryker 5 rejects `thresholds.low < thresholds.break`).
Check that `cd tests/Maxdop.Core.Tests && mise exec -- dotnet stryker` gets past startup. A full run
takes about fifty minutes and is not needed: the mutation job runs on the PR because that file
changed.

### GitHub Actions SHAs

Dependabot handles these, grouped weekly. Nothing to do beyond merging its PR.

## 3. Verify

All of these, with their output summarised in the hand-off:

```bash
mise exec -- dotnet format --verify-no-changes      # if any C# changed
mise exec -- dotnet test maxdop.slnx -c Release --nologo
mise exec -- dotnet publish src/Maxdop.Cli -c Release -r linux-x64 -p:PublishAot=true -o <scratchpad>/aot
python3 tools/dotnet-security-check.py
mise exec -- dotnet list maxdop.slnx package --vulnerable --include-transitive
(cd editors/vscode && npm audit --audit-level=moderate)
(cd editors/vscode && mise exec -- npm ci && mise exec -- npm test)       # if the extension changed
```

Use the scratchpad directory for the `-o` output and the corpus, not the worktree.

### Formatting diff against the previous release

This measures what users will see change. Do it for every release:

1. Fetch the corpus once with `tools/corpus/fetch.sh`, into the scratchpad.
2. `gh release download v<prev> --pattern 'maxdop-<prev>-linux-x64.tar.gz'` and unpack it.
3. Make two copies of the whole corpus. Run `--write` on one with the previous binary and on the
   other with the new AOT build.
4. `diff -r` the two copies.

- **Identical:** the notes open with the "No formatting changes" line (see step 4).
- **Different:** every changed file has to be explained by a change in the survey, and the notes
  have to describe each change with a small before/after example. A diff that nothing in the survey
  explains is a regression. Stop and show it. A difference in a patch release means the version
  proposal was wrong: go back to the maintainer.

Also run `mise exec -- dotnet run --project tools/Maxdop.Corpus -c Release -- <corpus>` and compare
it with the `corpus-report` artifact of the last green nightly (`gh run download <id> -n corpus-report`).
Refused, crashed and unstable must not grow. Moved may change only as the formatting diff explains.

## 4. Release prep

Change exactly these, and nothing else that mentions the old version. Workflow comments and tools
cite past versions as history.

| File | Change |
|---|---|
| `pyproject.toml` | `version = "X.Y.Z"` and `dependencies = ["maxdop==X.Y.Z"]`. The tag's wheels job fails if the pin does not match. |
| `README.md` | The install snippet's `V=X.Y.Z` and the pre-commit `rev: vX.Y.Z` |
| `docs/ci.md` | Every `maxdop@…`, `maxdop-…-linux-x64` and `MAXDOP_VERSION` pin |
| `packaging/pypi/README.md` | `additional_dependencies: [maxdop==X.Y.Z]` |
| `editors/vscode/CHANGELOG.md` | New top `## X.Y.Z` entry, in the style of the ones below it |
| `docs/release-notes-X.Y.Z.md` | New. No H1, because the workflow sets the title. Without this file the release falls back to generated notes, which the maintainer does not want. |

Do **not** bump `packaging/winget`, `packaging/homebrew`, `packaging/scoop`, `packaging/mason` or the
`version` in `editors/vscode/package.json`. CI stamps them or the channel is updated after the tag.

If a change in the survey documents itself somewhere (a new option in `docs/cli.md`, a new setting
in `docs/editors.md`), check those docs were updated in the change's own PR. If they weren't, say so
in the hand-off rather than writing them as part of the prep.

### Release notes

Plain prose, no marketing. Lead with what a user notices first. Read the last two or three
`docs/release-notes-*.md` before writing, and match them.

- **No formatting change:** open with `**No formatting changes.** A file formatted by <prev> comes
  out of X.Y.Z byte for byte the same.` Then a sentence on what the release is about
  (`docs/release-notes-0.1.3.md`, `0.1.4.md`).
- **Formatting changes:** one `##` section per change, each with a short before/after SQL example,
  most visible first (`docs/release-notes-0.1.2.md`). Say how to keep the old behaviour if an option
  allows it.
- **Security:** for each fix, give the advisory ID, whether it ships in the binary or VSIX or is
  build tooling only, and whether maxdop could reach it.
- **Build, test and CI only:** group into a short "Toolchain" section, or leave out.

The VS Code changelog entry is shorter and written for extension users. It mentions the bundled
maxdop version.

## 5. Hand off, then stop

Report:

1. **Worktree path and branch.**
2. **The release:** version, why that bump, and the survey summary.
3. **Findings:** what was fixed and what was left (and why).
4. **Verification results**, each passed or failed, with failing output. Include the formatting
   diff result: identical, or N files changed, all explained.
5. **Suggested commits**, in Conventional Commits format, one per logical change, each with the
   files to stage. Usually: one `build(deps): …` or `fix(…): …` per fix, then
   `chore(release): prepare vX.Y.Z`. Each body explains *why*, as the repo's history does. No
   `Co-Authored-By` trailer and no "Generated with" line.
6. **The maintainer's steps:**
   - Push the branch and open the PR. `pyproject.toml` changed, so the release workflow dry-runs all
     seven native builds, the VSIX and the wheels on the PR. Wait for green.
   - Merge, then `git tag vX.Y.Z <merge commit> && git push origin vX.Y.Z`.
   - Tell Claude it is tagged, for step 6.

Do not continue past this point until the maintainer says the tag is pushed.

## 6. After the tag

```bash
gh run list --workflow release.yml -L 1      # then: gh run watch <id>
```

If a **build** leg fails, the `release` job has not run and nothing is published. Diagnose it and
fix it on a branch. Moving the tag (`git push --delete origin vX.Y.Z`, then re-tagging) is the
maintainer's decision. Say what the fix is, and leave the tag to them.

If a **publish** job fails after `release` succeeded, `gh run rerun <id> --failed` is safe for
openvsx (`--skip-duplicate`), homebrew (no-op when unchanged) and winget (skips when already
submitted). It is **not** safe for pypi or the Marketplace, where a version that partly published
cannot be replaced. Look at those before suggesting anything.

Then check every channel and report what each shows:

| Channel | Check |
|---|---|
| GitHub Release | `gh release view vX.Y.Z --json assets --jq '.assets \| length'`; notes are the hand-written ones |
| PyPI | `curl -s https://pypi.org/pypi/maxdop/json \| jq -r .info.version` |
| VS Code Marketplace | `npx --prefix editors/vscode vsce show pbrooks.maxdop --json \| jq -r '.versions[0].version'` |
| Open VSX | `curl -s https://open-vsx.org/api/pbrooks/maxdop \| jq -r .version` |
| Homebrew tap | `gh api repos/pagebrooks/homebrew-tap/contents/Formula/maxdop.rb --jq .content \| base64 -d \| grep -m1 version` |
| WinGet | the `winget` job's PR on microsoft/winget-pkgs. If the job is off (`PUBLISH_WINGET` unset), submit with Komac from `mise.toml`: `komac update pagebrooks.maxdop --version X.Y.Z --urls <win-x64 zip> <win-arm64 zip> --dry-run --output <scratch>`, show the maintainer, then `komac submit --yes <dir>` once they agree |
| Scoop | `pagebrooks/scoop-maxdop` updates itself (Excavator plus `checkver`/`autoupdate`), usually within a day: `gh api repos/pagebrooks/scoop-maxdop/contents/bucket/maxdop.json --jq .content \| base64 -d \| grep -m1 '"version"'`. Report it as pending rather than failed on the day of the tag |

nightly-red issues close themselves on the next green scheduled run. To close them sooner, dispatch
the check on main: `gh workflow run nightly.yml -f job=deps`.
