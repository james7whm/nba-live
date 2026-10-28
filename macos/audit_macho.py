#!/usr/bin/env python3
"""Validate every shipped Mach-O for the requested CPU and minimum macOS version."""
import argparse
import json
from pathlib import Path
import re
import subprocess


def minimum_version(output):
    if re.search(r"\bLC_BUILD_VERSION\b", output):
        match = re.search(r"(?m)^\s*minos\s+(\d+(?:\.\d+){1,2})\s*$", output)
        if not re.search(r"(?m)^\s*platform\s+MACOS\s*$", output):
            raise ValueError("Mach-O does not target macOS")
    elif re.search(r"\bLC_VERSION_MIN_MACOSX\b", output):
        match = re.search(r"(?m)^\s*version\s+(\d+(?:\.\d+){1,2})\s*$", output)
    else:
        raise ValueError("Mach-O minimum version was not provided")
    if not match:
        raise ValueError("Mach-O minimum version could not be parsed")
    return tuple(int(part) for part in match.group(1).split("."))


def run(app, arch):
    binary = app / "Contents/MacOS/NBALiveMac"
    libraries = sorted((app / "Contents/Frameworks").rglob("*.dylib"))
    result = []
    for file in [binary, *libraries]:
        subprocess.run(["xcrun", "lipo", "-verify_arch", arch, str(file)], check=True)
        output = subprocess.check_output(["xcrun", "vtool", "-arch", arch, "-show-build", str(file)], text=True)
        version = minimum_version(output)
        if version > (12, 0, 0):
            raise ValueError(f"{file.name} requires macOS {'.'.join(map(str, version))}")
        result.append({"file": str(file.relative_to(app)), "architecture": arch, "minimum": ".".join(map(str, version))})
    destination = app / "Contents/Resources/macos-compatibility.json"
    destination.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"Verified {len(result)} native binaries for {arch} and macOS 12.0")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("app", type=Path)
    parser.add_argument("architecture", choices=("arm64", "x86_64"))
    arguments = parser.parse_args()
    run(arguments.app, arguments.architecture)
