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
  version "0.1.2"
  license "MIT"

  livecheck do
    url :stable
    strategy :github_latest
  end

  on_macos do
    on_arm do
      url "https://github.com/pagebrooks/maxdop/releases/download/v0.1.2/maxdop-0.1.2-osx-arm64.tar.gz"
      sha256 "8e1cd49d526cd62b7f8c0543736c452e9f50361083d6e829d0dd6563a8dc81c0"
    end
    on_intel do
      url "https://github.com/pagebrooks/maxdop/releases/download/v0.1.2/maxdop-0.1.2-osx-x64.tar.gz"
      sha256 "8f280f715acdd838b3cefd0b777cf0c5880a0ed0d6e5426ccc557da029aa1cf8"
    end
  end

  # No musl build here on purpose: Homebrew on Linux is glibc, and these binaries need only
  # glibc 2.17, which is older than anything Homebrew supports.
  on_linux do
    on_arm do
      url "https://github.com/pagebrooks/maxdop/releases/download/v0.1.2/maxdop-0.1.2-linux-arm64.tar.gz"
      sha256 "67105bee2e418acce54d821cdba8c9f4053a3256f53f650c634af26ee6458cda"
    end
    on_intel do
      url "https://github.com/pagebrooks/maxdop/releases/download/v0.1.2/maxdop-0.1.2-linux-x64.tar.gz"
      sha256 "e47d6cdc13e45d4d811930016acc58814857950bdb9b27181cd72cebb97c9c5a"
    end
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
