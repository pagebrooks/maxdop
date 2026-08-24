**Distribution only. The formatter is unchanged.**

Nothing under `src/` moved between 0.1.0 and 0.1.1, so this release formats your SQL exactly as the
last one did — same layout, same safety guarantees, same exit codes. What changed is how you install
it and where it can run.

## pip, uv and pipx

maxdop is now on PyPI as seven per-platform wheels, each carrying the native binary:

```sh
pip install maxdop
uvx maxdop --check src/     # no install at all
```

There is no Python in the hot path. The wheel is a delivery mechanism: it puts the same static
binary on your PATH and gets out of the way, so cold start is still ~3.5 ms and no interpreter starts
when you format a file. This is aimed at people who already have Python — the dbt, SQLFluff and
Airflow end of the SQL world — and changes nothing for anyone installing the archive directly.

Wheels cover `manylinux_2_17` (x86_64, aarch64), `musllinux_1_2` (x86_64), `macosx_12_0` (x86_64,
arm64), `win_amd64` and `win_arm64`. The glibc floor is 2.17, so the Linux wheels install well below
the distribution they were built on.

## pre-commit hooks

```yaml
repos:
  - repo: https://github.com/pagebrooks/maxdop
    rev: v0.1.1
    hooks:
      - id: maxdop          # rewrites files, fails so you re-stage
      - id: maxdop-check    # fails without touching anything
```

pre-commit builds a virtualenv, pip resolves the wheel for that machine, and the binary lands on
PATH — nobody has to install maxdop first. `v0.1.1` is the earliest usable `rev`, because `v0.1.0`
was tagged before these hooks existed.

Exclusions still come from `.maxdop.json` rather than the hook, so a file excluded there is skipped
even when pre-commit hands it over explicitly.

## Scoop

```powershell
scoop bucket add maxdop https://github.com/pagebrooks/scoop-maxdop
scoop install maxdop
```

The manifest carries `checkver` and `autoupdate`, so Scoop's Excavator picks up new releases and
pulls hashes from the release's `SHA256SUMS` rather than re-downloading the archives.

WinGet manifests are prepared in [`packaging/winget/`](https://github.com/pagebrooks/maxdop/tree/v0.1.1/packaging/winget)
but are not in `microsoft/winget-pkgs` yet.

## Provenance

The wheels are attested like every other artifact, so a pip-installed binary can be traced to the
workflow run that built it:

```sh
gh attestation verify maxdop-0.1.1-py3-none-manylinux_2_17_x86_64.whl --repo pagebrooks/maxdop
```

## Everything else

Formatter behaviour, configuration and known limitations are unchanged — see the
[0.1.0 notes](https://github.com/pagebrooks/maxdop/blob/v0.1.1/docs/release-notes-0.1.0.md). Test
dependencies were bumped; nothing shipped in the binary changed.
