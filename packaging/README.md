# Packaging

Manifests for the package managers maxdop is distributed through. None of these are consumed by the
build — each is a copy of what was submitted to somebody else's repository, kept here so the next
version starts from the last one that worked rather than from a blank file.

| Directory | Manager | Submitted to |
| --- | --- | --- |
| `scoop/` | Scoop (Windows) | a bucket repository — see below |
| `winget/` | WinGet (Windows) | [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) |
| `mason/` | mason.nvim (Neovim) | [mason-org/mason-registry](https://github.com/mason-org/mason-registry) |

Every hash here is cross-checked against the release's `SHA256SUMS` before submission. A wrong hash
is the single most common reason a package-manager PR is rejected, and it is entirely avoidable.

## Scoop

Scoop installs from *buckets*. Which bucket to aim at is the only real decision:

- **Your own bucket** works today and needs nobody's approval. Create a repository named
  `scoop-maxdop`, put `maxdop.json` in a `bucket/` directory at its root, and users run:

  ```powershell
  scoop bucket add maxdop https://github.com/pagebrooks/scoop-maxdop
  scoop install maxdop
  ```

- **The `extras` bucket** reaches everyone with Scoop already installed, no bucket command needed.
  Submit a PR to [ScoopInstaller/Extras](https://github.com/ScoopInstaller/Extras).

- **The `main` bucket** holds well-known command-line tools and applies a notability bar. It is worth
  trying once maxdop has visible traction; it is not worth trying first.

The realistic path is your own bucket now, `extras` once there is something to point at.

`maxdop.json` already carries `checkver` and `autoupdate`, so once it is in a bucket the manifest
updates itself: Scoop's Excavator bot watches GitHub releases, rewrites the URLs, and pulls the new
hashes out of the release's `SHA256SUMS` rather than downloading and hashing the archives. Test that
machinery before relying on it:

```powershell
# from a checkout of the bucket repository
.\bin\checkver.ps1 maxdop -Update
```

## WinGet

Manifests go into `microsoft/winget-pkgs` under
`manifests/p/pagebrooks/maxdop/<version>/`, as a pull request. Do not hand-build the PR; use
Microsoft's own tool, which generates the manifests, validates them against the schema, and opens
the PR for you:

```powershell
winget install Microsoft.WingetCreate

# Generates manifests from the release archives and submits the PR.
wingetcreate new https://github.com/pagebrooks/maxdop/releases/download/v0.1.0/maxdop-0.1.0-win-x64.zip

# For later versions, update the existing package instead:
wingetcreate update pagebrooks.maxdop --version 0.2.0 --urls <x64-zip> <arm64-zip> --submit
```

The manifests in `winget/` are the shape those commands should produce — a three-file set (version,
installer, locale) declaring a `zip` installer with `NestedInstallerType: portable`, because the
archive contains a folder rather than a bare executable. Keep them as the reference for what was
submitted; let `wingetcreate` generate the copy that actually goes in the PR, since it tracks the
current schema version and this file does not.

Two things to expect:

- **Automated validation runs on the PR**, then a human reviews it. Turnaround is usually days.
- **The binaries are unsigned**, so Windows SmartScreen may warn on first run. That does not block a
  WinGet submission — plenty of accepted packages are unsigned — but it is the reason a code-signing
  certificate eventually becomes worth its cost.

Once the package is accepted, [winget-releaser](https://github.com/vedantmgoyal2009/winget-releaser)
can open the update PR automatically on every tagged release. It needs a personal access token with
`public_repo` scope, and it publishes to a Microsoft repository on your behalf, so wire it up
deliberately rather than as a default.

## Keeping these current

Both manifests hardcode a version and two hashes. After a release, refresh them from the published
`SHA256SUMS` rather than editing by hand:

```sh
gh release download vX.Y.Z -p 'maxdop-*-win-*.zip' -p 'SHA256SUMS'
grep -E 'win-(x64|arm64)\.zip' SHA256SUMS
```
