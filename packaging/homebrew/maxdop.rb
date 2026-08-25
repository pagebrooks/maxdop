# Homebrew formula for maxdop, kept here for the same reason as every other manifest in
# packaging/: this is a copy of what was pushed to somebody else's repository, so the next
# version starts from the last one that worked rather than from a blank file.
#
# It belongs in https://github.com/pagebrooks/homebrew-tap as Formula/maxdop.rb, which makes
# the install `brew install pagebrooks/tap/maxdop`.
#
# Unlike ffpdf.rb in that tap, this builds nothing. maxdop ships a static binary per platform,
# so the formula picks the right archive and installs the executable out of it. That also means
# four URLs and four checksums change on every release — see the note at the bottom.
#
# homebrew-core is a separate question and not this file: it applies a notability bar that
# maxdop does not meet yet. A personal tap has no such bar, which is why this can ship today.
class Maxdop < Formula
  desc "T-SQL formatter that runs in CI and verifies its own output"
  homepage "https://github.com/pagebrooks/maxdop"
  version "0.1.1"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/pagebrooks/maxdop/releases/download/v0.1.1/maxdop-0.1.1-osx-arm64.tar.gz"
      sha256 "6aa4564d04e8430961184279ed93134508a6b9c5ea0c36daab98ba3e5a73979d"
    end
    on_intel do
      url "https://github.com/pagebrooks/maxdop/releases/download/v0.1.1/maxdop-0.1.1-osx-x64.tar.gz"
      sha256 "c7287ec3edf9e8f1317e0bcd693dee255b16f611b5bac5b503614c43cbdd8d5b"
    end
  end

  # No musl build here on purpose: Homebrew on Linux is glibc, and these binaries need only
  # glibc 2.17, which is older than anything Homebrew supports.
  on_linux do
    on_arm do
      url "https://github.com/pagebrooks/maxdop/releases/download/v0.1.1/maxdop-0.1.1-linux-arm64.tar.gz"
      sha256 "066583e50d3acc96bd95ce65611077a08028cc90bd5e3dea1e7e333255ceb179"
    end
    on_intel do
      url "https://github.com/pagebrooks/maxdop/releases/download/v0.1.1/maxdop-0.1.1-linux-x64.tar.gz"
      sha256 "d3d6b29f514b60b8f4e86184ddc4a400055b924523a92fd7d20213fc366742cd"
    end
  end

  livecheck do
    url :stable
    strategy :github_latest
  end

  def install
    # Each archive holds a single maxdop-<version>-<rid>/ directory, and Homebrew has already
    # changed into it by this point, so the binary is simply here.
    bin.install "maxdop"
  end

  test do
    assert_match "maxdop #{version}", shell_output("#{bin}/maxdop --version")

    # Not just that it starts. A formatter that installs and then formats nothing is the
    # failure worth catching here, and it is invisible to a --version check.
    (testpath/"q.sql").write("select a,b from t where x=1;\n")
    assert_equal "SELECT a, b FROM t WHERE x = 1;\n", shell_output("#{bin}/maxdop #{testpath}/q.sql")

    # --check is the contract with CI: exit 1 on a file that would change, and leave it alone.
    output = shell_output("#{bin}/maxdop --check #{testpath}/q.sql 2>&1", 1)
    assert_match "would be reformatted", output
    assert_equal "select a,b from t where x=1;\n", (testpath/"q.sql").read
  end
end

# Bumping this for a release: four urls, four sha256s from the release's SHA256SUMS, and the
# version. `brew livecheck maxdop` reports that a new one exists; it does not write the file.
