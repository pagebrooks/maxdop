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

Manifests go into `microsoft/winget-pkgs` under `manifests/p/pagebrooks/maxdop/<version>/`, as a
pull request. The three files in `winget/` are that set: version, installer and locale, declaring a
`zip` installer with `NestedInstallerType: portable`, because the archive contains a folder rather
than a bare executable.

**No Windows machine is needed.** Microsoft's own `wingetcreate` is Windows-only, but
[komac](https://github.com/russellbanks/Komac) does the same job and ships Linux and macOS builds:

```sh
komac submit --token <github-pat> packaging/winget
```

It forks `winget-pkgs`, commits the manifests to the right path, and opens the PR. The token needs
`public_repo` scope.

Komac is also the cheapest way to check the manifests are right, because it derives them
independently from the archive:

```sh
komac analyze maxdop-0.1.2-win-x64.zip
```

Compare its output against `winget/pagebrooks.maxdop.installer.yaml`. Two details it settles that are
easy to get wrong by hand, and both were wrong here first:

- **`RelativeFilePath` uses forward slashes**, not the backslashes Windows paths suggest. Accepted
  manifests such as `sharkdp/bat` do the same.
- **`ManifestVersion` must be the same in all three files, and must be a version winget-pkgs
  currently accepts.** This is what got the 0.1.1 submission rejected: these files were written
  against 1.6.0, komac regenerated the installer manifest at 1.12.0, and carried the other two
  through untouched — so the set declared two different specification versions at once. Every file
  validated on its own; the *set* did not. Check them together, never one at a time:

  ```sh
  grep -h '^ManifestVersion:' packaging/winget/*.yaml | sort -u   # must print exactly one line
  ```

The schema version moves. Before a submission, look at what a recently-merged package declares
rather than trusting the number already in these files:

```sh
curl -s https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/j/junegunn/fzf/<version>/junegunn.fzf.installer.yaml | grep ManifestVersion
```

The schemas themselves are public, so the whole set can be validated anywhere:

```sh
curl -O https://aka.ms/winget-manifest.installer.1.6.0.schema.json    # and .version. / .defaultLocale.
```

On Windows, `winget validate --manifest packaging\winget` adds URL and hash checks, and
`winget install --manifest packaging\winget` is the only test that proves the nested path is
right — a wrong one installs cleanly and leaves no working command. A `windows-latest` GitHub
Actions runner does this without owning a Windows machine.

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
