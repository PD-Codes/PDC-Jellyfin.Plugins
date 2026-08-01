#!/usr/bin/env python3
"""Updates or creates a manifest.json version entry after a GitHub Release is published.

Called by .github/workflows/build.yml once per plugin, with the plugin's own GUID,
version, ZIP checksum, release timestamp, release asset URL, changelog text and
targetAbi - the last two coming straight out of that plugin's build.yaml (see
extract_build_meta.py), which is the file a human already edits to bump the version, so
the changelog is still 100% human-authored, just no longer duplicated into manifest.json
by hand as a second step.

If the plugin's GUID has no entry in manifest.json AT ALL, this still refuses rather than
inventing name/description/owner/category out of nothing - that base entry is a one-time,
by-hand addition (see plugins/README.md). But if the GUID exists and the VERSION does not
yet have a versions[] entry, one is now created (from changelog/targetAbi) and inserted at
the front - manifest.json lists newest first, matching the existing file's convention.

If the version entry already exists (e.g. this script re-runs for the same release), only
checksum/timestamp/sourceUrl are refreshed - changelog/targetAbi on an EXISTING entry are
left as they are, in case they were hand-edited in manifest.json after publishing (a typo
fix, say) and should not be silently overwritten by whatever build.yaml says today.

Usage: update_manifest.py <guid> <version> <checksum> <timestamp> <source_url> <changelog> <target_abi>
"""

import json
import sys


def main() -> int:
    if len(sys.argv) != 8:
        print(
            "::error::update_manifest.py expects exactly 7 arguments "
            "(guid, version, checksum, timestamp, source_url, changelog, target_abi); "
            f"got {len(sys.argv) - 1}.",
            file=sys.stderr,
        )
        return 1

    guid, version, checksum, timestamp, source_url, changelog, target_abi = sys.argv[1:8]

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

    plugin.setdefault("versions", [])
    entry = next((v for v in plugin["versions"] if v.get("version") == version), None)

    if entry is None:
        entry = {
            "version": version,
            "changelog": changelog,
            "targetAbi": target_abi,
            "sourceUrl": source_url,
            "checksum": checksum,
            "timestamp": timestamp,
        }
        # Newest first, matching manifest.json's existing ordering - Jellyfin itself does
        # not require any particular order, but a human scanning the file for "what's
        # current" should not have to read to the bottom.
        plugin["versions"].insert(0, entry)
        created = True
    else:
        entry["checksum"] = checksum
        entry["timestamp"] = timestamp
        entry["sourceUrl"] = source_url
        created = False

    with open("manifest.json", "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
        f.write("\n")

    action = "created" if created else "updated"
    print(f"manifest.json {action}: {guid} v{version} checksum={checksum}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
