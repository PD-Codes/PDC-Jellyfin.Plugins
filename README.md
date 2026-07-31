# PDC-Jellyfin.Plugins

This repository hosts multiple [Jellyfin](https://jellyfin.org) plugins, each maintained in its
own folder under [`plugins/`](plugins/), built and released independently of one another.

## Plugins in this repository

| Plugin | Folder | What it does |
|---|---|---|
| PD-Codes API | [`plugins/PdCodesApi`](plugins/PdCodesApi/README.md) | Metadata and images from a self-hosted PD-Codes API v5 instance. |

Each plugin's own README covers what it does and does not do, installation, and configuration.
This file only covers what is common across all of them.

## Adding a plugin

See [`plugins/README.md`](plugins/README.md) for the folder layout new plugins must follow, the
per-plugin GUID and tag-prefix conventions, and the three places outside a plugin's own folder
that must be updated when one is added.

## Installing from this repository

Every plugin here is published through the same self-hosted plugin repository, described by the
`manifest.json` at the root of this repo:

1. Jellyfin Dashboard → **Plugins** → **Repositories** → **Add**.
2. Paste the raw URL to this repo's `manifest.json`, e.g.
   `raw.githubusercontent.com/PD-Codes/PDC-Jellyfin.Plugins/main/manifest.json`.
3. The plugins listed in the table above appear in the catalog; install whichever you need.

`manifest.json`'s `checksum` and `timestamp` fields are **never filled in automatically** by any
workflow in this repo — they are placeholders until updated by hand after each tagged release.
See [`plugins/README.md`](plugins/README.md) for why, and each plugin's own README for the exact
release steps.
