#!/usr/bin/env python3
"""Reads a plugin's build.yaml and prints its guid/version/changelog/targetAbi as JSON.

Used by .github/workflows/build.yml so the release-tag check and the manifest update step
share ONE correctly-parsed source for these fields, instead of the grep/sed this repo used
to extract guid/version. grep/sed cannot safely pull out `changelog: >`, a folded YAML block
scalar spanning multiple lines - the tag-version-mismatch bug this script exists to prevent
was found precisely because build.yaml's version had drifted from the release tag with
nothing checking it.

Usage: extract_build_meta.py <path-to-build.yaml>
Prints a single line of JSON: {"guid": ..., "version": ..., "changelog": ..., "targetAbi": ...}
"""

import json
import sys

import yaml


def main() -> int:
    if len(sys.argv) != 2:
        print(
            "::error::extract_build_meta.py expects exactly 1 argument (path to build.yaml)",
            file=sys.stderr,
        )
        return 1

    path = sys.argv[1]

    with open(path, encoding="utf-8") as f:
        data = yaml.safe_load(f)

    missing = [key for key in ("guid", "version", "changelog", "targetAbi") if not str(data.get(key, "")).strip()]
    if missing:
        print(
            f"::error::{path} is missing a non-empty value for: {', '.join(missing)}. "
            "All four are required to build a release and a manifest.json entry.",
            file=sys.stderr,
        )
        return 1

    print(json.dumps({
        "guid": str(data["guid"]).strip(),
        "version": str(data["version"]).strip(),
        "changelog": str(data["changelog"]).strip(),
        "targetAbi": str(data["targetAbi"]).strip(),
    }))
    return 0


if __name__ == "__main__":
    sys.exit(main())
