# MeshVault

A self-hosted catalog for a 3D printing model collection.

It indexes your model folders **in place** — files are never moved or renamed unless
you explicitly allow it — and adds search, tags, collections, designers, source links,
per-model notes, rendered thumbnails, and an in-browser 3D preview you can rotate and
snapshot as the card image.

It also understands that a pack ships the same mini several times over. Supported,
unsupported, hollowed and no-logo copies are recognised as **variants of one sculpt**
rather than four unrelated files, so a folder of two hundred files reads as forty minis
with four flavours each. When you are ready, it can lay the library out to match.

Built with .NET 10, Blazor Server, MudBlazor, EF Core and SQLite. No GPU, no native
imaging libraries, no external services.

---

## Running it on Unraid

The old **Template Repositories** field on the Docker tab is gone — it was deprecated in
favour of Community Applications. Until this is listed there, add the container by hand.

### By hand

Docker tab → **Add Container** → turn on **Advanced View**:

| Field | Value |
| --- | --- |
| Name | `MeshVault` |
| Repository | `ghcr.io/007darkmatter5/meshvault:latest` |
| Network Type | `Bridge` |
| WebUI | `http://[IP]:[PORT:8080]/` |

Then add:

- **Port** — `8080` → `8080`, TCP
- **Path** — `/mnt/user/appdata/meshvault` → `/data`, Read/Write
- **Path** — your models folder → `/models`, **Read Only**
- **Variable** — `MeshVault__Libraries__0__Name` = `Models`
- **Variable** — `MeshVault__Libraries__0__Path` = `/models`

Apply, then open the WebUI and **create the first account** — it becomes the administrator.
Unraid saves this as a user template, so later edits are one click.

### Via Community Applications

`templates/meshvault.xml` and `ca_profile.xml` are ready for submission at
[ca.unraid.net/submit](https://ca.unraid.net/submit). Once accepted, MeshVault installs
from the **Apps** tab with the paths and variables pre-filled.

### Or with compose

```bash
git clone https://github.com/007darkmatter5/meshvault.git
cd meshvault
# edit docker-compose.yml: set your library path
docker compose up -d
```

---

## Two things worth getting right

**Keep `/data` on local storage.** It holds a SQLite database. SQLite over SMB or NFS
is unreliable and will eventually corrupt. Cache drive or an unassigned SSD.

**Back up `/data`.** Your models are safe on the array regardless, but everything
MeshVault knows *about* them — tags, collections, designers, notes, favorites, chosen
preview images — lives only there.

**Library speed matters more than you'd expect.** Building previews reads every model
once. On a fast local share that is minutes; over a slow link it can be hours. It runs
in the background, steps aside whenever you are using the app, and can be paused from
the Libraries page.

---

## First run

1. Open the web UI. It will say nobody has claimed it — **create the first account**,
   which becomes the administrator and takes ownership of anything already indexed.
2. Registration closes automatically afterwards. Add people from **Accounts**, or
   re-open self-signup there if you prefer.
3. The library scans, then previews build in the background.

---

## Variants, the inbox, and organizing

Three features that work together, all of them opt-in and none of them automatic.

**Variants.** MeshVault reads filenames to work out which files are the same mini in a
different flavour — `UD-001-SUP-Wall.stl` and `UD-001-Wall.stl` are one sculpt, supported
and plain. The vocabulary is yours to edit under **Settings → Variants**: add whatever
shorthand your creators use, and set the order that decides which copy a preview opens on.
Get one wrong and you can correct it by hand on the model page; corrections are pinned and
survive every later pass.

Where a creator split those flavours across *separate folders*, **Regroup** on the
Libraries page offers to show them as one entry without moving a thing.

**The inbox.** Name a folder inside your library — `inbox` — under a library's settings.
Drop new downloads there and they appear badged **Unfiled**, with a count on the Libraries
page and a filter in Browse. Give them a designer and tags, and they stop being unfiled the
moment you file them.

**Organizing.** The **Organize** page works out where every model would go from a folder
template such as `{designer}/{sculpt}`, and shows you the whole plan before anything
happens. `{sculpt}` is the interesting one: it gives each mini its own folder, breaking a
pack apart and gathering its flavours together in the same pass.

Nothing moves until you press **Move the files**, and only when the library has
*"Allow MeshVault to move and rename files"* turned on. The plan names every file it would
delete — only ever a byte-for-byte copy of a file going to the same place, checked in full
before it goes — and every file it cannot place. **There is no undo. Back up your library
before the first run.**

---

## Configuration

Every setting can be supplied as an environment variable using `__` for nesting.

| Variable | Default | What it does |
| --- | --- | --- |
| `MeshVault__DataPath` | `/data` | Database, thumbnails, preview cache, sign-in keys |
| `MeshVault__Libraries__0__Name` | — | Display name for the first library |
| `MeshVault__Libraries__0__Path` | — | Path to it inside the container |
| `MeshVault__Libraries__0__AllowOrganize` | `false` | Let MeshVault move and rename files |
| `MeshVault__ScanOnStartup` | `true` | Scan libraries when the app starts |
| `MeshVault__RescanIntervalHours` | `12` | Skip that scan if one ran this recently |

Library settings can be changed after the fact from **Libraries** in the app, so the two
`Libraries__0__*` variables above only matter on first run.

### Behind a reverse proxy

Forwarded headers are honoured, so SWAG, Nginx Proxy Manager and Traefik work without
extra configuration. Terminate TLS at the proxy and forward to port 8080.

**Your proxy must pass WebSockets.** The whole UI runs over one, and several proxies ship
with the upgrade switched off — in Nginx Proxy Manager it is the *Websockets Support*
toggle, off by default. Without it every page still loads and looks completely normal, and
every button, dialog and filter silently does nothing.

### Behind Cloudflare

Cloudflare Tunnel works, and passes WebSockets by default. Three of Cloudflare's optional
features do break MeshVault, all with the same symptom — a page that looks perfect and
ignores every click:

- **Rocket Loader** (Speed → Optimization). It defers and reorders every script on the
  page, including the one that starts Blazor. **Turn this off** for the hostname.
  `/diagnostics` detects it and says so.
- **Bot Fight Mode** (Security → Bots). It can challenge the `/_blazor` connection, which
  no browser can answer on the app's behalf.
- **"I'm Under Attack" mode**, for the same reason.

A page rule or configuration rule scoped to the MeshVault hostname is enough; there is no
need to change them account-wide.

---

## Letting people look without an account

**Settings → Features → Public browsing** lets anyone who can reach the address read the
catalog without signing in. It is off by default.

Read means read. A signed-out visitor gets Browse, the model pages, the 3D viewer and the
designer list. They cannot change, rename, tag, favourite, upload or organise anything, and
collections, favourites and paint racks belong to an account and stay hidden — as do
Libraries, Accounts and Diagnostics.

Turn it on only if you are content for whoever can reach that address to see your whole
library, **including your model notes**. If MeshVault is exposed to the internet rather than
a home network, that is everybody.

---

## When something is not working

Sign in as an administrator and open **Diagnostics** (`/diagnostics`).

It is deliberately a plain, statically rendered page, so it still works when the rest of
the app has gone unresponsive. It reports:

- **Browser checks**, measured in the browser you are holding: whether Blazor and
  MudBlazor loaded, whether the interactive connection is live, and whether a WebSocket
  can be opened at all.
- **Server report**: version, environment, uptime, whether `/data` is writable, whether
  each library path is reachable from inside the container, catalog counts and preview
  progress.
- **Recent warnings and errors**, so you do not have to reach for `docker logs`.

**Copy report** puts the lot on the clipboard, ready to paste into an issue.

If MeshVault ever stops responding to clicks, a banner appears along the bottom of the
page saying so and linking here — that state is almost always a proxy dropping the
WebSocket rather than a broken feature.

---

## Development

```bash
dotnet run --project src/MeshVault.Web
dotnet test
```

| Project | Purpose |
| --- | --- |
| `src/MeshVault.Core` | Domain model, filesystem scanning, mesh parsing, software renderer |
| `src/MeshVault.Data` | EF Core, indexing, catalog queries, metadata import |
| `src/MeshVault.Web` | Blazor UI, authentication, background workers, HTTP endpoints |
| `tests/MeshVault.Tests` | 437 tests |

### How some of it works

**Scanning.** A folder becomes a model when it directly contains a mesh or CAD file.
Subfolders without models of their own (photos, docs) are absorbed into it. Rescans
*reconcile* rather than rebuild, so tags, notes and favorites stay attached across scans.

**Thumbnails** are rendered by a software rasteriser — z-buffer, supersampling, PNG
written via `ZLibStream` — so there is no GPU or native imaging dependency and the
container stays slim.

**The 3D viewer** receives quantised 16-bit positions rather than the original file
(18 bytes per triangle against 50 in a binary STL), decimated by vertex clustering when
a model exceeds the triangle budget. Payloads are cached, so opening a model is a local
read rather than a trip to the library share.

**Variants.** Filenames are read into a *sculpt* (which mini) and a *variant* (which
flavour) against a vocabulary you curate. Detection only ever proposes: anything you set
by hand is pinned and never revisited, and an empty vocabulary simply means every file
stands on its own.

**Organizing** plans first and applies second, always by hand. It moves files rather than
folders, so a run stopped part way leaves every completed step both done and recorded; it
rewrites the catalog as it goes, because a scan that found a moved folder would otherwise
read it as one model deleted and another added, losing its tags. It will not overwrite: a
same-name, same-length pair is hashed in full before either copy is removed, and one that
turns out to differ is left alone.

**Accounts.** The first is the administrator; registration then closes. Collections and
favorites are per-user. The last administrator cannot be demoted, suspended or deleted.

---

## Licence

MIT. See [LICENSE](LICENSE).
