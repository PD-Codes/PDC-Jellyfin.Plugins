# Adding a plugin to this repository

This repo hosts more than one Jellyfin plugin. Every plugin follows the same convention,
established by the first one, [`PdCodesApi`](PdCodesApi/). Copy its layout as your starting
point.

## Folder layout

One folder per plugin under `plugins/`, named after the plugin (e.g. `plugins/SomeNewThing/`):

```
plugins/<PluginName>/
  README.md                              # plugin-specific docs (see below)
  build.yaml                             # jprm config (see below)
  Jellyfin.Plugin.<PluginName>/
    Jellyfin.Plugin.<PluginName>.csproj
    Plugin.cs
    ... source, following PdCodesApi's layout where it fits:
    Api/            (typed API client, models, exceptions)
    Configuration/  (PluginConfiguration.cs, configPage.html)
    Providers/      (IRemoteMetadataProvider / IRemoteImageProvider implementations)
    ExternalIds/    (IExternalId implementations)
```

`Api/`, `Configuration/`, `Providers/` and `ExternalIds/` are **conventions, not requirements** —
adapt them to what the new plugin actually needs. What is NOT optional is that the plugin's
`.csproj` and all of its source live under its own `Jellyfin.Plugin.<PluginName>/` folder,
directly inside its own `plugins/<PluginName>/` folder, so that `jprm plugin build
plugins/<PluginName>` and the CI workflow's per-plugin `project:` path both resolve without any
folder having to know about any other plugin's existence.

### `build.yaml`

Copy `plugins/PdCodesApi/build.yaml` as a starting template. It is consumed by
[`jprm`](https://github.com/oddstr13/jellyfin-plugin-repository-manager) to build a versioned ZIP
and to describe the plugin to a self-hosted repository manifest.

**The new plugin's `build.yaml` must declare its OWN fresh `guid` — NEVER reuse another plugin's
GUID.** Jellyfin identifies a plugin by that GUID alone; two plugins sharing one means Jellyfin
treats them as the same plugin, and each one's install silently overwrites the other's settings
and update state. Generate a new GUID (e.g. `uuidgen` or any RFC 4122 generator) for every new
plugin, and make sure it appears consistently in that plugin's own `Plugin.cs`, `build.yaml`,
`configPage.html` and its entry in the root `manifest.json` — see below.

### `README.md`

Plugin-specific, following `plugins/PdCodesApi/README.md` as a model: what it does, what it does
NOT do, install options (manual DLL drop and self-hosted repository), and a configuration table.
Do not duplicate the repo-wide install instructions here — those live in the root `README.md`.

## What else must be updated when a plugin is added

Three places outside the plugin's own folder. All three are easy to forget because forgetting
any one of them fails **silently** — the new plugin's own folder looks complete and its own CI
build can even pass, while the plugin itself never becomes visible or reachable.

1. **Root `manifest.json`.** It is a flat JSON array of plugin objects — Jellyfin's repository
   format supports any number of them in one file. Append a new entry with the new plugin's own
   GUID, name, description, and a `versions[]` array with a placeholder `checksum`/`timestamp`
   entry for the version you are about to release (leave those two fields as any placeholder
   text — CI overwrites them). Its `sourceUrl` should already follow this repo's tag-prefix
   convention (see below), even before the release exists. **Forgetting this step means the
   plugin builds and releases in CI but Jellyfin's catalog never lists it** — a plugin that
   exists and cannot be found is invisible in exactly the way this project is careful never to
   be: no error, no log, just a catalog that looks complete without it.

   After a release is published, `.github/workflows/build.yml` fills in that version's
   `checksum`, `timestamp` and `sourceUrl` itself (`.github/scripts/update_manifest.py`) and
   pushes the update to `main` as `github-actions[bot]`. It does **not** invent the `versions[]`
   entry itself — changelog text is a human decision — so that entry (version/changelog/
   targetAbi, with placeholder checksum/timestamp) must exist before you tag, or this step fails
   loudly instead of guessing at what to write.

2. **`.github/workflows/build.yml`.** Add a new entry to the `strategy.matrix.plugin` list: `id`,
   `tag_prefix`, `project` (path to the new plugin's `.csproj`), `assembly`, `zip_name` — AND add a
   matching option to `workflow_dispatch.inputs.plugin`'s dropdown (its `id` must be typed
   identically in both places; the "Gate on selected plugin" step compares them as plain
   strings — this lives in a step rather than the job's own `if` because the `matrix` context is
   not available there at all, a hard workflow-parse error if you try). Forgetting the matrix
   entry means the new plugin is **never built or released by CI** — pushing a tag for it does
   nothing, silently; there is no error, because nothing was told to look for that tag. Forgetting
   the dropdown option only means nobody can manually build/release it from the Actions tab — tag
   pushes still work — but it is the same one-string-two-places trap either way.

3. **This repo's tag-prefix convention.** Each plugin's GitHub Releases are told apart by a tag
   PREFIX unique to that plugin — e.g. `pdcodesapi-v1.0.0.0` for PdCodesApi. Pick a short,
   lowercase, unique prefix for the new plugin and use it consistently in three places that must
   all agree:
   - the tag you push (`<prefix>-v<version>`),
   - the workflow matrix entry's `tag_prefix` (point 2 above),
   - `manifest.json`'s `sourceUrl` for that plugin's version (point 1 above).

   A mismatch between these three does not error either: it produces a release that the workflow
   made but the manifest never points at, or a manifest `sourceUrl` that 404s because the tag it
   names was never pushed.

## Every plugin needs its own GUID — stated again because it is the one invariant worth repeating

A GUID collision between two plugins is not a theoretical risk here: this repo started as a
single-plugin layout where the GUID appeared in four places for exactly one plugin, and every one
of those places has to be right for that one plugin to work. Multiplying the plugin count without
multiplying the GUID is the most likely way to reintroduce that exact class of bug, silently: two
plugins would look installed, and Jellyfin would treat updates, settings and enable/disable state
for one as belonging to both.
