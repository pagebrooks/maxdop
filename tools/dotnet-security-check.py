#!/usr/bin/env python3
"""Fail when Microsoft has shipped a .NET security release that the pinned SDK does not include.

    python3 tools/dotnet-security-check.py [releases.json]

The release binaries are Native AOT, so the .NET runtime is compiled into them: the runtime a user
gets is whichever one the SDK pinned in mise.toml bundles. A NuGet scan cannot see that. `dotnet list
package --vulnerable` does not list the AOT compiler pack at all, and NuGet audit only flags it once
a GHSA reaches the advisory database and a restore happens to pull the pack. v0.1.4 found out the
hard way: runtime 10.0.14 shipped its fix on 25 October, and the first sign of it here was the tag
build failing on 5 November.

This reads Microsoft's own release metadata instead, so it goes red on the day a security runtime
ships. With no argument it fetches the channel's releases.json; pass a path to check a saved copy.
"""

import json
import os
import pathlib
import re
import sys
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
METADATA = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/{channel}/releases.json"


def version_key(version: str) -> tuple[int, ...]:
    """10.0.14 -> (10, 0, 14). Preview suffixes never appear in a pin worth comparing."""
    return tuple(int(part) for part in re.findall(r"\d+", version.split("-")[0]))


def feature_band(sdk: str) -> int:
    """10.0.403 -> 4. SDKs ship in bands of a hundred, and a band can stop receiving updates."""
    return version_key(sdk)[2] // 100


def pinned_sdk() -> str:
    match = re.search(r'^\s*dotnet\s*=\s*"([^"]+)"', (ROOT / "mise.toml").read_text(), re.MULTILINE)
    if not match:
        sys.exit("mise.toml has no dotnet pin")
    return match.group(1)


def load(channel: str, path: str | None) -> dict:
    if path:
        return json.loads(pathlib.Path(path).read_text())
    with urllib.request.urlopen(METADATA.format(channel=channel), timeout=30) as response:
        return json.load(response)


def summary(lines: list[str]) -> None:
    target = os.environ.get("GITHUB_STEP_SUMMARY")
    if target:
        with open(target, "a") as handle:
            handle.write("\n".join(lines) + "\n")


def main() -> int:
    sdk = pinned_sdk()
    channel = ".".join(sdk.split(".")[:2])
    releases = load(channel, sys.argv[1] if len(sys.argv) > 1 else None)["releases"]

    bundled = next(
        (entry.get("runtime-version") or release["runtime"]["version"]
         for release in releases
         for entry in release.get("sdks") or [release["sdk"]]
         if entry["version"] == sdk),
        None,
    )
    if bundled is None:
        print(f"::error file=mise.toml::SDK {sdk} is not in the {channel} release metadata")
        return 1

    missing = [
        release for release in releases
        if release.get("security") and version_key(release["runtime"]["version"]) > version_key(bundled)
    ]

    if not missing:
        message = f"SDK {sdk} bundles runtime {bundled}, and no later .NET {channel} security release exists."
        print(message)
        summary([f"**.NET** {message}"])
        return 0

    newest = max(missing, key=lambda release: version_key(release["runtime"]["version"]))
    sdks = sorted((entry["version"] for entry in newest.get("sdks") or [newest["sdk"]]), key=version_key)
    same_band = [candidate for candidate in sdks if feature_band(candidate) == feature_band(sdk)]
    if same_band:
        advice = f"Move mise.toml to SDK {same_band[-1]}."
    else:
        # What 10.0.3xx did after 10.0.303: the band stopped, so the fix only exists in a later one.
        later = [candidate for candidate in sdks if feature_band(candidate) > feature_band(sdk)]
        advice = (f"The {feature_band(sdk)}xx band has no SDK for it; move mise.toml to "
                  f"{(later or sdks)[0]} and run the full suite, since a band jump can add analyzers.")

    lines = [
        f"SDK {sdk} bundles runtime {bundled}, and the AOT binaries compile that runtime in.",
        f"{len(missing)} later .NET {channel} security release(s) are not included:",
    ]
    for release in sorted(missing, key=lambda release: version_key(release["runtime"]["version"])):
        cves = [cve["cve-id"] for cve in release.get("cve-list") or []]
        lines.append(f"  runtime {release['runtime']['version']} ({release['release-date']}): "
                     f"{len(cves)} CVE(s) {', '.join(cves)}")
    lines.append(advice)

    print("\n".join(lines))
    print(f"::error file=mise.toml::.NET runtime {newest['runtime']['version']} is a security release "
          f"the pinned SDK {sdk} does not include. {advice}")
    summary(["**.NET** security release missing from the pin", "", "```", *lines, "```"])
    return 1


if __name__ == "__main__":
    sys.exit(main())
