#!/usr/bin/env python3
"""Builds one PyPI wheel per platform from a release's archives.

maxdop is a native binary, not a Python project, so there is nothing for a build
backend to compile. A wheel is a zip with a prescribed layout, and this writes
that layout directly: the binary goes in the wheel's `.data/scripts/` directory,
which pip installs onto PATH with the execute bit set.

The binary is *not* wrapped in a Python console script. A shim would put
interpreter startup in front of every invocation and undo the cold start that is
the point of shipping a static binary.

Platform tags are derived from the binaries rather than hardcoded: the glibc
floor is read out of the ELF, and the macOS minimum out of the Mach-O load
commands. A tag that is too high makes pip refuse a machine that would have
worked; too low installs onto one that cannot run it.

The archives are an input, not something this fetches. From a release:

    gh release download v0.1.0 -R pagebrooks/maxdop \\
        -p '*.tar.gz' -p '*.zip' -p SHA256SUMS -D /tmp/maxdop-rel
    (cd /tmp/maxdop-rel && sha256sum --ignore-missing -c SHA256SUMS)

    python3 build_wheels.py --version 0.1.0 --archives /tmp/maxdop-rel --out /tmp/maxdop-wheels

In the release workflow the archives are already on disk, so only the last line
is needed there.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import re
import stat
import struct
import tarfile
import zipfile
from dataclasses import dataclass
from pathlib import Path

# RID -> the wheel platform tag's architecture half. The OS half is derived.
ARCH = {
    "linux-x64": "x86_64",
    "linux-arm64": "aarch64",
    "linux-musl-x64": "x86_64",
    "osx-x64": "x86_64",
    "osx-arm64": "arm64",
    "win-x64": "win_amd64",
    "win-arm64": "win_arm64",
}

SUMMARY = (
    "A T-SQL formatter that runs in CI, understands the whole language, "
    "and checks its own work"
)

CLASSIFIERS = [
    "Development Status :: 4 - Beta",
    "Intended Audience :: Developers",
    "License :: OSI Approved :: MIT License",
    "Topic :: Database",
    "Topic :: Software Development :: Quality Assurance",
    "Programming Language :: SQL",
]


@dataclass
class Payload:
    rid: str
    name: str          # filename as installed (maxdop or maxdop.exe)
    data: bytes


def glibc_floor(data: bytes) -> tuple[int, int]:
    """Highest GLIBC_x.y this binary asks the loader for."""
    found = {(int(a), int(b)) for a, b in re.findall(rb"GLIBC_(\d+)\.(\d+)", data)}
    if not found:
        raise SystemExit("no GLIBC_ version references found; is this really glibc-linked?")
    return max(found)


def macos_minimum(data: bytes) -> tuple[int, int]:
    """Minimum macOS version from LC_BUILD_VERSION / LC_VERSION_MIN_MACOSX."""
    magic, = struct.unpack_from("<I", data, 0)
    if magic != 0xFEEDFACF:
        raise SystemExit(f"unexpected Mach-O magic {magic:#x}; only thin 64-bit is handled")

    ncmds, = struct.unpack_from("<I", data, 16)
    offset = 32
    for _ in range(ncmds):
        cmd, cmdsize = struct.unpack_from("<II", data, offset)
        if cmd in (0x32, 0x24):  # LC_BUILD_VERSION, LC_VERSION_MIN_MACOSX
            raw, = struct.unpack_from("<I", data, offset + (12 if cmd == 0x32 else 8))
            return (raw >> 16) & 0xFFFF, (raw >> 8) & 0xFF
        offset += cmdsize
    raise SystemExit("no macOS minimum-version load command found")


def platform_tag(p: Payload) -> str:
    arch = ARCH[p.rid]
    if p.rid.startswith("win-"):
        return arch
    if p.rid == "linux-musl-x64":
        # musl builds carry no version floor to read; 1_2 is the tag every
        # currently supported Alpine satisfies.
        return f"musllinux_1_2_{arch}"
    if p.rid.startswith("linux-"):
        major, minor = glibc_floor(p.data)
        return f"manylinux_{major}_{minor}_{arch}"
    major, minor = macos_minimum(p.data)
    # macOS 11+ tags use a 0 minor; pip matches x_0 against any 11.x host.
    return f"macosx_{major}_{0 if major >= 11 else minor}_{arch}"


def read_archives(directory: Path, version: str) -> list[Payload]:
    """
    Reads one binary per platform out of the release archives.

    `version` names the *archives*, which is not always the version the wheels
    get. A dispatch run builds `0.0.0-ci.<run>` archives but has to produce PEP
    440 wheels, so the caller trims the version for the wheel and passes the
    untrimmed one here.
    """
    payloads = []
    for rid in ARCH:
        exe = "maxdop.exe" if rid.startswith("win-") else "maxdop"
        stem = f"maxdop-{version}-{rid}"

        if (path := directory / f"{stem}.tar.gz").exists():
            with tarfile.open(path) as tar:
                member = tar.extractfile(f"{stem}/{exe}")
                if member is None:
                    raise SystemExit(f"{path}: no {exe} inside")
                data = member.read()
        elif (path := directory / f"{stem}.zip").exists():
            with zipfile.ZipFile(path) as zf:
                data = zf.read(f"{stem}/{exe}")
        else:
            raise SystemExit(
                f"no archive for {rid} in {directory}\n"
                f"Expected {stem}.tar.gz or {stem}.zip. Fetch a release's archives with:\n"
                f"  gh release download v{version} -R pagebrooks/maxdop "
                f"-p '*.tar.gz' -p '*.zip' -p SHA256SUMS -D {directory}"
            )

        payloads.append(Payload(rid=rid, name=exe, data=data))
    return payloads


def metadata(version: str, readme: str) -> str:
    lines = [
        "Metadata-Version: 2.1",
        "Name: maxdop",
        f"Version: {version}",
        f"Summary: {SUMMARY}",
        "Author: Page Brooks",
        "License: MIT",
        "Project-URL: Homepage, https://github.com/pagebrooks/maxdop",
        "Project-URL: Repository, https://github.com/pagebrooks/maxdop",
        "Project-URL: Issues, https://github.com/pagebrooks/maxdop/issues",
        "Keywords: sql,t-sql,tsql,sql-server,formatter,pre-commit",
        *(f"Classifier: {c}" for c in CLASSIFIERS),
        "Requires-Python: >=3.8",
        "Description-Content-Type: text/markdown",
        "",
        readme,
    ]
    return "\n".join(lines)


def build(p: Payload, version: str, readme: str, license_text: str, out: Path) -> Path:
    tag = f"py3-none-{platform_tag(p)}"
    dist_info = f"maxdop-{version}.dist-info"
    script = f"maxdop-{version}.data/scripts/{p.name}"

    entries = {
        script: p.data,
        f"{dist_info}/METADATA": metadata(version, readme).encode(),
        f"{dist_info}/WHEEL": (
            "Wheel-Version: 1.0\n"
            "Generator: maxdop build_wheels.py\n"
            "Root-Is-Purelib: false\n"
            f"Tag: {tag}\n"
        ).encode(),
        f"{dist_info}/licenses/LICENSE": license_text.encode(),
    }

    record = []
    for name, blob in entries.items():
        digest = base64.urlsafe_b64encode(hashlib.sha256(blob).digest()).rstrip(b"=").decode()
        record.append(f"{name},sha256={digest},{len(blob)}")
    record.append(f"{dist_info}/RECORD,,")
    entries[f"{dist_info}/RECORD"] = ("\n".join(record) + "\n").encode()

    out.mkdir(parents=True, exist_ok=True)
    path = out / f"maxdop-{version}-{tag}.whl"

    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as zf:
        for name, blob in entries.items():
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            # S_IFREG matters as much as the 0o755. pip decides whether to make
            # an installed file executable with stat.S_ISREG(mode) and mode &
            # 0o111, so a mode missing the regular-file bits fails that test and
            # the binary lands on PATH without the execute bit. uv installs it
            # correctly either way, which is exactly how this hides in testing.
            mode = stat.S_IFREG | (0o755 if name == script else 0o644)
            info.external_attr = mode << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            zf.writestr(info, blob)

    return path


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", required=True, help="the version the wheels are stamped with")
    ap.add_argument(
        "--archive-version",
        help="the version in the archive filenames, when it differs from --version. "
        "A dispatch run names its archives 0.0.0-ci.<run> but must stamp wheels 0.0.0, "
        "because a wheel filename has to be PEP 440.",
    )
    ap.add_argument("--archives", type=Path, required=True)
    ap.add_argument("--out", type=Path, default=Path("wheels"))
    args = ap.parse_args()

    here = Path(__file__).resolve().parent
    readme = (here / "README.md").read_text()
    license_text = (here.parent.parent / "LICENSE").read_text()

    for p in read_archives(args.archives, args.archive_version or args.version):
        path = build(p, args.version, readme, license_text, args.out)
        print(f"{path.name}  {path.stat().st_size / 1_048_576:.1f} MB")


if __name__ == "__main__":
    main()
