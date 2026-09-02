#!/usr/bin/env python3
"""Summarise a Stryker.NET report, listing the mutants no test objected to.

Run after `dotnet stryker`:

    python3 tools/mutation-summary.py tests/Maxdop.Core.Tests/StrykerOutput

The score is the headline and the least useful part. What matters is the list at the end: each entry
is a change to a safety gate that the suite did not notice. Some are cosmetic — which token a
diagnostic anchors to, where a message truncates — and pinning those would make the suite harder to
change without making the formatter safer. The rest are real gaps, and the difference is only
visible by reading them.
"""

import collections
import json
import pathlib
import sys

# Mutants in these categories change what a diagnostic *says* rather than what a gate *decides*.
# Split out rather than hidden: a surviving logic mutant is a gap, a surviving message mutant is a
# judgement call, and totalling them together hides which one you are looking at.
COSMETIC = ("String", "Interpolation")


def is_cosmetic(mutator: str) -> bool:
    """Stryker names mutators "String mutation", "Equality mutation", and so on."""
    return any(kind in mutator for kind in COSMETIC)


def latest_report(root: pathlib.Path) -> pathlib.Path | None:
    reports = sorted(root.rglob("mutation-report.json"), key=lambda p: p.stat().st_mtime)
    return reports[-1] if reports else None


def main(argv):
    if len(argv) != 2:
        print(__doc__.strip(), file=sys.stderr)
        return 2

    report = latest_report(pathlib.Path(argv[1]))
    if report is None:
        print(f"no mutation-report.json under {argv[1]}", file=sys.stderr)
        return 1

    data = json.loads(report.read_text())
    counts = collections.Counter()
    survivors = []

    for path, info in data.get("files", {}).items():
        source = info.get("source", "").split("\n")
        name = pathlib.Path(path).name
        for mutant in info.get("mutants", []):
            status = mutant.get("status", "?")
            counts[status] += 1
            if status != "Survived":
                continue
            line_no = mutant.get("location", {}).get("start", {}).get("line", 0)
            text = source[line_no - 1].strip() if 0 < line_no <= len(source) else ""
            survivors.append((name, line_no, mutant.get("mutatorName", "?"), text))

    # Stryker's own formula: uncovered mutants count against the score, because a mutant no test
    # reaches is one nothing would have caught either.
    killed = counts["Killed"] + counts["Timeout"]
    tested = killed + counts["Survived"] + counts["NoCoverage"]
    score = 100.0 * killed / tested if tested else 0.0

    print("```")
    print(f"score    {score:.1f}%   ({counts['Killed']} killed, {counts['Timeout']} timeout, "
          f"{counts['Survived']} survived, {counts['NoCoverage']} uncovered)")

    logic = [s for s in survivors if not is_cosmetic(s[2])]
    cosmetic = [s for s in survivors if is_cosmetic(s[2])]

    for label, group in (("logic", logic), ("message", cosmetic)):
        print(f"\nsurviving {label} mutants: {len(group)}")
        for name, line_no, mutator, text in sorted(group):
            print(f"  {name}:{line_no}  {mutator}")
            if text:
                print(f"      {text[:96]}")
    print("```")

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
