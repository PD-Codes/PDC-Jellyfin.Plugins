#!/usr/bin/env python3
"""Fills in checksum/timestamp/sourceUrl on an EXISTING manifest.json version entry.

Called by .github/workflows/build.yml right after a GitHub Release is published, once
per plugin, with the plugin's own GUID, version, ZIP checksum, release timestamp and
release asset URL. It deliberately does not invent a new plugin entry or a new
versions[] entry: a plugin manifest.json has never heard of, or a version with no
changelog text yet, both need a human decision (what does this release say it does),
and guessing that from a build number would be exactly the kind of silent, low-effort
correctness this project's plugins are built to avoid, applied to the repository's own
tooling instead of to a plugin's data.

Usage: update_manifest.py <guid> <version> <checksum> <timestamp> <source_url>
"""

import json
import sys


def main() -> int:
    if len(sys.argv) != 6:
        print(
            "::error::update_manifest.py expects exactly 5 arguments "
            "(guid, version, checksum, timestamp, source_url); "
            f"got {len(sys.argv) - 1}.",
            file=sys.stderr,
        )
        return 1

    guid, version, checksum, timestamp, source_url = sys.argv[1:6]

    with open("manifest.json", encoding="utf-8") as f:
        data = json.load(f)

    plugin = next((p for p in data if p.get("guid", "").lower() == guid.lower()), None)
    if plugin is None:
        print(
            f"::error::No manifest.json entry with guid {guid}. Add the plugin's base "
            "entry (name/description/owner/category) by hand first - see plugins/README.md.",
            file=sys.stderr,
        )
        return 1

    entry = next(
        (v for v in plugin.get("versions", []) if v.get("version") == version),
        None,
    )
    if entry is None:
        print(
            f"::error::No versions[] entry for {version} under guid {guid}. Add one by hand "
            "(version/changelog/targetAbi) before tagging - this script only fills in "
            "checksum/timestamp/sourceUrl on an entry that already exists, it does not invent "
            "changelog text.",
            file=sys.stderr,
        )
        return 1

    entry["checksum"] = checksum
    entry["timestamp"] = timestamp
    entry["sourceUrl"] = source_url

    with open("manifest.json", "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
        f.write("\n")

    print(f"manifest.json updated: {guid} v{version} checksum={checksum}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
