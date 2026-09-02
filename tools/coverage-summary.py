#!/usr/bin/env python3
"""Summarise Cobertura coverage, with the safety gates called out by name.

Run after `dotnet test --collect:"XPlat Code Coverage"`:

    python3 tools/coverage-summary.py <results-directory>

Every test project writes its own Cobertura file covering only what it touched, so the files are
merged rather than read one at a time: a line the CLI suite reached and the Core suite did not is
covered, and reading either file alone would say otherwise.

The percentage is a lookup tool, not a target. What the numbers are for is the last section: lines
in the three safety gates that no test executes. Those gates are the whole basis of "it cannot break
your code", and a gate branch nothing runs is a branch nothing would notice breaking.
"""

import collections
import pathlib
import sys
import xml.etree.ElementTree as ET

# The classes the safety claim rests on. A missed line in any of these is worth a look in a way that
# a missed line in a node handler is not: a handler that is never exercised prints nothing, while a
# gate that is never exercised approves everything.
GATES = (
    "Maxdop.Core.Formatting.RoundTripVerifier",
    "Maxdop.Core.Formatting.SqlFormatter",
    "Maxdop.Core.Text.SourceEncoding",
    "Maxdop.Core.Formatting.BatchFormatter",
    "Maxdop.Core.Comments.CommentAttacher",
)


def merge(paths):
    """Union of covered lines per class across every report."""
    hits = collections.defaultdict(dict)   # class -> {line: covered}
    branches = collections.defaultdict(dict)
    filenames = {}
    for path in paths:
        for cls in ET.parse(path).getroot().iter("class"):
            name = cls.get("name", "")
            filenames.setdefault(name, cls.get("filename", ""))
            for line in cls.iter("line"):
                number = int(line.get("number", 0))
                covered = int(line.get("hits", 0)) > 0
                hits[name][number] = hits[name].get(number, False) or covered
                if line.get("branch") == "True":
                    taken = line.get("condition-coverage", "")
                    done = taken.startswith("100%")
                    branches[name][number] = branches[name].get(number, False) or done
    return hits, branches, filenames


def rate(covered, total):
    return f"{100.0 * covered / total:5.1f}%" if total else "    —"


def main(argv):
    if len(argv) != 2:
        print(__doc__.strip(), file=sys.stderr)
        return 2

    reports = sorted(pathlib.Path(argv[1]).rglob("coverage.cobertura.xml"))
    if not reports:
        print(f"no coverage.cobertura.xml under {argv[1]}", file=sys.stderr)
        return 1

    hits, branches, filenames = merge(reports)
    print(f"Merged {len(reports)} report(s).\n")

    # --- per assembly ---------------------------------------------------------------
    totals = collections.defaultdict(lambda: [0, 0])
    for name, lines in hits.items():
        assembly = "Maxdop.Cli" if name.startswith("Maxdop.Cli") else "Maxdop.Core"
        totals[assembly][0] += sum(1 for c in lines.values() if c)
        totals[assembly][1] += len(lines)

    print(f"{'assembly':<32}{'lines':>9}{'covered':>9}{'rate':>8}")
    for assembly, (covered, total) in sorted(totals.items()):
        print(f"{assembly:<32}{total:>9}{covered:>9}{rate(covered, total):>8}")

    # --- the gates ------------------------------------------------------------------
    print(f"\n{'safety gate':<44}{'lines':>7}{'rate':>8}{'branch':>9}")
    uncovered = []
    for gate in GATES:
        lines = hits.get(gate)
        if not lines:
            print(f"{gate:<44}{'—':>7}{'not in report':>17}")
            continue
        covered = sum(1 for c in lines.values() if c)
        taken = branches.get(gate, {})
        btotal = len(taken)
        bcovered = sum(1 for c in taken.values() if c)
        print(f"{gate:<44}{len(lines):>7}{rate(covered, len(lines)):>8}{rate(bcovered, btotal):>9}")
        for number in sorted(n for n, c in lines.items() if not c):
            uncovered.append((gate, filenames.get(gate, ""), number))

    # --- the part worth acting on ---------------------------------------------------
    print(f"\nUnexecuted lines in the safety gates: {len(uncovered)}")
    for gate, filename, number in uncovered:
        print(f"  {filename}:{number}  ({gate.rsplit('.', 1)[-1]})")

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
