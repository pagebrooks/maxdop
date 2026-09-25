#!/usr/bin/env python3
"""Render the Homebrew formula for a release.

    python3 tools/homebrew-formula.py <version> <path-to-SHA256SUMS>

Writes the formula to stdout. The four checksums come out of the release's own `SHA256SUMS`, which
the release job has already verified with `sha256sum -c` — so the formula cannot disagree with the
release, and nothing here re-downloads or re-hashes an archive.

This is the source of truth for the formula. `packaging/homebrew/maxdop.rb` is its output, committed
so a reader can see the current shape without running anything.
"""

import pathlib
import sys

# Homebrew on Linux is glibc-only, so the musl build is deliberately absent. maxdop's glibc floor is
# 2.17, older than anything Homebrew supports, so the ordinary Linux archives are always fine.
PLATFORMS = {
    "osx-arm64": ("on_macos", "on_arm"),
    "osx-x64": ("on_macos", "on_intel"),
    "linux-arm64": ("on_linux", "on_arm"),
    "linux-x64": ("on_linux", "on_intel"),
}

TEMPLATE = '''\
# Homebrew formula for maxdop. GENERATED — do not edit by hand.
#
# Produced by tools/homebrew-formula.py from the release's SHA256SUMS, and pushed to
# https://github.com/pagebrooks/homebrew-tap as Formula/maxdop.rb by the `homebrew` job in
# release.yml. The copy in packaging/ is committed so the current shape is readable without
# running the generator; editing either copy by hand is overwritten on the next release.
#
# Installs as `brew install pagebrooks/tap/maxdop`.
#
# Unlike ffpdf.rb in that tap, this builds nothing. maxdop ships a static binary per platform, so
# the formula picks the right archive and installs the executable out of it — which is why four
# URLs and four checksums change on every release, and why generating it beats bumping it.
#
# homebrew-core is a separate question and not this file: it applies a notability bar that maxdop
# does not meet yet. A personal tap has no such bar, which is why this can ship today.
class Maxdop < Formula
  desc "T-SQL formatter that runs in CI and verifies its own output"
  homepage "https://github.com/pagebrooks/maxdop"
  version "{version}"
  license "MIT"

  livecheck do
    url :stable
    strategy :github_latest
  end

  on_macos do
    on_arm do
{osx_arm64}
    end
    on_intel do
{osx_x64}
    end
  end

  # No musl build here on purpose: Homebrew on Linux is glibc, and these binaries need only
  # glibc 2.17, which is older than anything Homebrew supports.
  on_linux do
    on_arm do
{linux_arm64}
    end
    on_intel do
{linux_x64}
    end
  end

  def install
    # Each archive holds a single maxdop-<version>-<rid>/ directory, and Homebrew has already
    # changed into it by this point, so the binary is simply here.
    bin.install "maxdop"
  end

  test do
    assert_match "maxdop #{{version}}", shell_output("#{{bin}}/maxdop --version")

    # Not just that it starts. A formatter that installs and then formats nothing is the
    # failure worth catching here, and it is invisible to a --version check.
    (testpath/"q.sql").write("select a,b from t where x=1;\\n")
    assert_equal "SELECT a, b FROM t WHERE x = 1;\\n", shell_output("#{{bin}}/maxdop #{{testpath}}/q.sql")

    # --check is the contract with CI: exit 1 on a file that would change, and leave it alone.
    output = shell_output("#{{bin}}/maxdop --check #{{testpath}}/q.sql 2>&1", 1)
    assert_match "would be reformatted", output
    assert_equal "select a,b from t where x=1;\\n", (testpath/"q.sql").read
  end
end
'''


def checksums(path: pathlib.Path) -> dict[str, str]:
    """Filename to hash, from a sha256sum-format file."""
    found = {}
    for line in path.read_text().splitlines():
        parts = line.split(None, 1)
        if len(parts) != 2:
            continue
        digest, name = parts
        # sha256sum marks binary-mode entries with a leading '*' on the filename. The Windows
        # archives in this release are written that way and the tarballs are not, so stripping it
        # is not optional even though the entries this reads happen to be text-mode today.
        found[name.lstrip("*")] = digest
    return found


def block(version: str, rid: str, digest: str) -> str:
    url = (
        f"https://github.com/pagebrooks/maxdop/releases/download/"
        f"v{version}/maxdop-{version}-{rid}.tar.gz"
    )
    return f'      url "{url}"\n      sha256 "{digest}"'


def main(argv: list[str]) -> int:
    if len(argv) != 3:
        print(__doc__.strip(), file=sys.stderr)
        return 2

    version, sums = argv[1], pathlib.Path(argv[2])
    found = checksums(sums)

    blocks = {}
    missing = []
    for rid in PLATFORMS:
        name = f"maxdop-{version}-{rid}.tar.gz"
        if name not in found:
            missing.append(name)
            continue
        blocks[rid.replace("-", "_")] = block(version, rid, found[name])

    # Loudly, rather than emitting a formula with a hole in it. A formula missing a platform block
    # installs nothing on that platform and says nothing about why.
    if missing:
        print(f"{sums}: no checksum for {', '.join(missing)}", file=sys.stderr)
        return 1

    print(TEMPLATE.format(version=version, **blocks), end="")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
