# Jellyfin plugin: PD-Codes API (v5)

A metadata and image provider for [Jellyfin](https://jellyfin.org) 10.10.x that reads from a
**self-hosted PD-Codes API v5** instance.

There is no public instance of this API. You must point the plugin at your own deployment;
the plugin ships with no default URL and does nothing until you set one.

---

## What it does

| Jellyfin item | Metadata | Images |
|---|---|---|
| Movie | title, overview, original title, year, premiere date, genres, keywords (as tags), studios, community rating, age rating | poster, backdrop, logo |
| Series | all of the above, plus series status (continuing / ended / unreleased) | poster, backdrop, logo |
| Episode | title, overview, air date, runtime | episode stills |

Also:

- **Search** in the "Identify" dialog, via `/v5/search`.
- **Identification by id.** If Jellyfin already holds a TMDB, IMDb, TheTVDB, MyAnimeList or
  AniList id, the plugin resolves through `/v5/lookup/{source}/{id}` before ever trying a
  name search.
- **A stable provider id.** The v5 ULID is stored under the key `PdCodesApi` and shown in the
  item's metadata editor via `IExternalId`, so re-scans are idempotent.

### The anime mapping, stated plainly

v5 treats **anime as a medium**, containing both films (Spirited Away) and series (Death Note).
Jellyfin splits by **shape**: Spirited Away is a `Movie`, Death Note is a `Series`, and there is
no separate "Anime" item type in Jellyfin at all.

So the mapping is many-to-many at the type level:

```
Jellyfin Movie   ->  v5 "movie"  OR  v5 "anime"
Jellyfin Series  ->  v5 "tv"     OR  v5 "anime"
```

The plugin therefore tries **both** v5 types for every item, and always passes `?type=` to
`/v5/lookup`. The settings only control which is tried first. See `PdCodesIds.MovieTypes()` and
`PdCodesIds.SeriesTypes()`.

### The namespaced ids, and why they matter

Jellyfin stores one `Tmdb` id and one `Tvdb` id per item. v5 does not have those sources:

| Jellyfin key | v5 source, for a Movie | v5 source, for a Series |
|---|---|---|
| `Tmdb` | `tmdb_movie` | `tmdb_tv` |
| `Tvdb` | `tvdb_movie` | `tvdb_series` |

The bare `tmdb` and `tvdb` sources are **retired** and answer HTTP 400.

This is not pedantry. TMDB movie 79744 is an Italian comedy; TMDB TV 79744 is *The Rookie*.
TheTVDB series 121361 is *Game of Thrones*; TheTVDB movie 121361 is a German comedy. Both
numbers are real in both id spaces, so the only thing that recovers the correct namespace is
the **shape of the Jellyfin item** — and getting it wrong does not error, it resolves
confidently to a completely different work.

## What it does NOT do

- **No manga, no music, no books.** v5 serves them; Jellyfin has no matching item types.
- **No Season metadata.** v5 has no season entity — episodes simply carry a season number.
  Registering a season provider that always returns nothing would look installed and do
  nothing, so there isn't one.
- **No cast and crew.** `/v5/{type}/{id}/credits` exists and is not consumed yet.
- **No availability / streaming links.** The country setting selects which availability and
  which age rating v5 returns; Jellyfin has nowhere to display availability itself.
- **No writes.** The plugin never posts to the API.
- **No guessing.** If an id is ambiguous, if a name search returns several plausible hits with
  no year to arbitrate, or if the API cannot be reached, the plugin returns **no result** and
  logs why. It will never return a half-populated item.
- **No laundering a guess into a fact.** When an item is identified by title rather than by a
  confirmed id, the v5 ULID is stored (it is *our* key — a wrong one is fixed by re-identifying
  the item) but the work's IMDb / TMDB / TheTVDB / MAL / AniList ids are **not** written to the
  item. Otherwise a fuzzy name match here would hand every other provider on the server an
  authoritative-looking id and they would all fetch the wrong film.

---

## Installing

### Option A — manual DLL drop

1. Build:
   ```
   dotnet publish plugins/PdCodesApi/Jellyfin.Plugin.PdCodesApi/Jellyfin.Plugin.PdCodesApi.csproj -c Release -o ./publish
   ```
2. Create a folder on the server: `<jellyfin-data>/plugins/PD-Codes API_1.0.0.0/`
   (Linux packages: `/var/lib/jellyfin/plugins/`; Docker: inside your mapped config volume.)
3. Copy **only** `Jellyfin.Plugin.PdCodesApi.dll` into it. Do not copy the `MediaBrowser.*`
   assemblies — the server provides those, and shipping copies makes the plugin implement a
   different set of types than the one loading it.
4. Restart Jellyfin.
5. Dashboard → Plugins → the plugin should be listed as **Active**. If it is listed as
   *Malfunctioned*, the server log names the type-load failure.

### Option B — self-hosted plugin repository

The manifest ships pointed at this project's own GitHub Releases
(`github.com/PD-Codes/PDC-Jellyfin.Plugins`), which is what the included workflow
publishes to. If you forked the repo or want to host elsewhere, adjust step 3 below.

1. Tag a commit `pdcodesapi-v1.0.0.0` (matching `build.yaml`'s `version`, with the
   `pdcodesapi-` prefix that identifies this plugin among the repo's other plugins) and
   push the tag. The workflow builds the ZIP, uploads it as a CI artifact, **and**
   creates a GitHub Release with the ZIP attached — but only for a tag push; a branch
   push or a pull request never runs that step. The Package step's log line prints
   the ZIP's MD5.

   **Without pushing a tag:** Actions tab → **build** → **Run workflow**, pick
   `PdCodesApi` from the "plugin" dropdown, and fill "release_tag" with the same
   `pdcodesapi-v1.0.0.0` string. Leaving "release_tag" empty just builds — useful as a
   quick compile check on a branch without waiting for CI to run on push. Only
   PdCodesApi's own job runs for either kind of manual dispatch; the other plugins in
   this repo are skipped, not built for nothing.
2. Once the release is published, the workflow itself fills in that version's
   `checksum` (the ZIP's MD5 — not SHA, Jellyfin's repository format uses MD5),
   `timestamp` and `sourceUrl` in `manifest.json`, commits as `github-actions[bot]`, and
   pushes straight to `main` — nothing to copy-paste. For a **version bump**: add the new
   `versions[]` entry to `manifest.json` by hand first (version, changelog, targetAbi,
   any placeholder checksum/timestamp) — this step only fills in those two fields on an
   entry that already exists, it will not invent the changelog text for you, and fails
   loudly if the entry is missing rather than guess.
3. Host `manifest.json` at a stable URL — the raw GitHub URL to this file works
   (`raw.githubusercontent.com/PD-Codes/PDC-Jellyfin.Plugins/main/manifest.json`),
   or host it wherever you prefer if you point `sourceUrl` elsewhere too.
4. Dashboard → Plugins → Repositories → **Add**, and paste the `manifest.json` URL.
5. The plugin now appears in the catalog under **Metadata**.

`build.yaml` is provided for [`jprm`](https://github.com/oddstr13/jellyfin-plugin-repository-manager)
if you would rather have the ZIP and manifest generated for you:

```
jprm plugin build plugins/PdCodesApi
jprm repo add /path/to/your/repo ./artifacts/<the zip jprm printed>
# jprm names the artifact by slugifying build.yaml's "name", so check the path it
# printed rather than assuming it matches the workflow's pdcodesapi.zip.
```

> The GUID `3f9c2a17-8d54-4e63-b0a1-5c7de2149f8b` appears in `Plugin.cs`, `build.yaml`,
> `manifest.json` and `configPage.html`. If you fork this, change it in **all four** or the
> mismatch will fail quietly and differently in each place.

---

## Configuration

Dashboard → Plugins → **PD-Codes API**.

| Setting | Notes |
|---|---|
| **API base URL** | Required. Root of your deployment *without* `/v5`, e.g. `https://media.example.org/jikan`. No default, on purpose. |
| **Preferred metadata language** | Optional ISO code. Leave empty to use each library's own language — better on a multi-language server. Sent as `Accept-Language`. |
| **Country** | Optional ISO 3166-1 alpha-2. Selects the availability subset and the age rating. **When empty, no age rating is imported at all**, because a rating from the wrong country would drive parental controls incorrectly. |
| **Accept uncertain matches** | Default **off**. Governs *three* things, all of which are title matches: (1) a lookup the API itself reports as `certain: false`; (2) a `tmdb_*_id_uncertain` row returned by the name search; (3) with it off, the name-search fallback additionally requires an **exact** (case-insensitive) title match rather than accepting the API's best-ranked hit. Enabling it lets a guess bind your library item permanently — the item then looks correctly identified and is never re-checked. |
| **Look under "anime" first** | Order only; both v5 types are always tried. Defaults: on for Series, off for Movies. |
| **Fall back to absolute episode numbering (anime)** | Default **on**. When an `SxxEyy` episode reference misses on an **anime** work filed under **Season 1**, retry it once as `E{n}`. The answer is only accepted when the API reports that work's numbering as non-continuous, so it cannot turn a miss into a guess. See below. |
| **Request timeout** | Seconds. Default 30. |

### Episode numbering, and the absolute fallback

An episode has no id of its own: the plugin addresses it as
`/v5/{type}/{ulid}/episodes/{ref}`, where `ref` is `S02E01`, `SP3`, or the absolute form
`E62`. **Which of those is right depends on the work.** TMDB numbers episodes *within*
seasons and does not always restart at 1 — One Piece season 2 is episodes 62–77 — so on such
a show `S01E62` and `E62` are two different episodes, and the API reports which convention a
work uses in its `numbering.continuous` field.

Anime released with absolutely-numbered files (`Show - 62.mkv`) is normally filed by Jellyfin
under **Season 1**, so the plugin asks for `S01E62`, which on a non-continuously numbered work
does not exist. The file then gets no metadata and nothing says why. With **Fall back to
absolute episode numbering** on, that miss is retried once as `E62`, and the retry's answer is
accepted **only** when the API's own `numbering.continuous` for that work is `false`. A work
that declares continuous numbering would have answered `S01E62` in the first place, so a hit on
the absolute form after a miss on the seasonal one is a contradiction — it is refused and logged
as a warning rather than resolved by picking a side. A missing `numbering` block is treated the
same way.

Scope, deliberately narrow:

- **Anime only.** A live-action series does not have this filing convention.
- **Season 1 only.** A file the user deliberately placed in Season 3 that misses is far more
  likely a genuine gap in the catalog than a numbering-convention mismatch, and re-reading its
  number as an absolute one would attach a confidently wrong episode.
- **One extra request per episode that missed under *both* candidate types.** The retry runs
  once, after both v5 types have been tried — not once per type — so a live-action series never
  pays it at all, and an anime episode that the ordinary reference already answers never
  reaches it. It is paid **once per episode, not once per scan**: the reference that actually
  produced the episode (`E62` — the *absolute* one, not the `S02E01` the API answers with, which
  is on the season axis and unreachable from the numbers Jellyfin holds) is stored on the item,
  so later scans address it directly. A stored reference is re-checked against the item's
  current season and episode numbers on every scan and discarded if it no longer matches, so
  renumbering or renaming a file self-corrects instead of pinning the old episode's metadata.

When the fallback is what produced an episode's metadata it is logged at **Information**,
naming both references and the work's ULID — this path is never silent.

### Why `Accept-Language` and not `?lang=`

Both work. The API sends `Vary: Accept-Language`, which means any cache in front of it keys
on the **header**. Passing the language in the query string instead would produce one cache
entry per URL that is nevertheless served to every language — a German client fills the
cache and the next English request gets German titles. The plugin sends the header.

### Enabling it on a library

Libraries → your library → **Manage Library** → Metadata downloaders / Image fetchers. The
plugin registers with `Order = 10`, so by default it runs **after** the built-in providers
rather than pre-empting a working setup. Drag it above TMDB if you want it to win.

---

## Troubleshooting

Everything below is visible in Dashboard → **Logs**. The plugin logs a reason for every
decision not to return metadata; if an item is not being identified and there is no log line,
the provider is not enabled on that library.

### "base URL is not configured" (warning, every item)
The plugin is installed and enabled but has no API URL. Set it in the plugin settings.

### HTTP 404 — no such work
- On `/v5/lookup/...`: normal. Your catalog simply does not hold that id.
- On `/v5/{type}/{ulid}`: the stored ULID no longer resolves. If the id in the log looks
  **numeric**, something wrote a MAL or TMDB id into the `PdCodesApi` field. Clear that
  provider id in the item's metadata editor and re-identify.
- On `/v5/{type}/{ulid}/episodes/{ref}`: **nothing has been ingested** for this work. Run
  `php artisan ingest:episodes tmdb --type=<medium>` on the API host.

### HTTP 409 — two different meanings, read the log line
These are not the same condition and the plugin logs them differently:

- **409 on `/v5/lookup/{source}/{id}`** — *more than one work carries that id*. A MAL id is
  both an anime and a manga; a TMDB number is both a movie and a series. The response body
  carries `candidates[]`. **The plugin does not pick one**, by design: a coin flip written
  into `ProviderIds` is permanent and invisible. Identify the item manually, or make sure
  Jellyfin has a more specific id (IMDb ids cannot collide this way).
- **409 on `/v5/.../episodes/{ref}`** — *the episodes were fetched but never merged*. The
  data is on the API host in `episode_fragments`; there is no merged index. This is **not**
  "no episodes". The response body names the command to run — it will be
  `php artisan ingest:episode-merge --work=<key>`.

### HTTP 400 — bad request
- **"retired source"**: the plugin sent `tmdb` or `tvdb` instead of `tmdb_movie` /
  `tmdb_tv` / `tvdb_series` / `tvdb_movie`. That is a bug in this plugin, not a
  misconfiguration — please report it with the log line, which contains the exact source key.
- **On search**: a filter meaningless for that medium. v5 answers 400 rather than an empty
  200 on purpose.
- **On `/v5/{type}/{ulid}`**: a page or parameter past an internal limit.

### HTTP 503 — the API is degraded
The ingest pipeline is unhealthy. Check `GET /v5/status` on the API host. Items will not be
identified until it recovers; **do not** interpret this as "the catalog does not have my
show" — nothing is written either way, so a later scan will pick them up.

### Timeouts / "could not reach"
Check the base URL from the Jellyfin server itself, not from your desktop:
```
curl -sS -o /dev/null -w '%{http_code}\n' https://your-host/jikan/v5/status
```
If Jellyfin runs in Docker, `localhost` is the container, not the host.

### Titles come back in the wrong language
The plugin sends `Accept-Language`. Check the library's metadata language, or set the
plugin-level override. `language` in the API response says which language was actually used
after fallback (`en` → `ja-romaji` → `ja`); if it does not match what you asked for, the
catalog has no title in that language for that work.

### Items are identified but with the wrong show
Almost always the TMDB/TVDB namespace, and it is worth checking rather than assuming. Look
at the item's ids in the metadata editor: a `Movie` should only ever have received a
`tmdb_movie`-sourced id and a `Series` only a `tmdb_tv`-sourced one. Also check whether
**Accept uncertain matches** is on — with it off, a title-only match is refused and logged
at Information level.

---

## Verified against

Every interface used here was checked against the **Jellyfin 10.10.6** source tree
(`github.com/jellyfin/jellyfin`, tag `v10.10.6`); the exact file is cited in a comment at
each implementation. Notably:

- `BasePlugin<TConfigurationType>` — `MediaBrowser.Common/Plugins/BasePluginOfT.cs`
- `IHasWebPages` — `MediaBrowser.Model/Plugins/IHasWebPages.cs`
- `IRemoteMetadataProvider<,>`, `IRemoteSearchProvider<>` — `MediaBrowser.Controller/Providers/IRemoteMetadataProvider.cs`
- `IRemoteImageProvider` — `MediaBrowser.Controller/Providers/IRemoteImageProvider.cs`
- `IExternalId` — `MediaBrowser.Controller/Providers/IExternalId.cs` (`UrlFormatString` is
  `[Obsolete]` in 10.10 but still on the interface, so it is implemented and suppressed)
- `IHttpClientFactory` injection — `MediaBrowser.Providers/Plugins/Omdb/OmdbImageProvider.cs`.
  The old `IHttpClient` was removed in 10.8 and does not exist in the 10.10 package.
- NuGet package `Jellyfin.Controller` **10.10.6**, `net8.0`.
