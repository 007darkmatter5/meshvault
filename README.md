# MeshVault

A self-hosted catalog for a 3D printing model collection.

It indexes your model folders **in place** — files are never moved or renamed unless
you explicitly allow it — and adds search, tags, collections, designers, source links,
per-model notes, rendered thumbnails, and an in-browser 3D preview you can rotate and
snapshot as the card image.

Built with .NET 10, Blazor Server, MudBlazor, EF Core and SQLite. No GPU, no native
imaging libraries, no external services.

---

## Running it on Unraid

### Using the template

1. Go to the **Docker** tab → **Add Container**.
2. Paste this into **Template** (or add the repo as a template source):
   `https://raw.githubusercontent.com/007darkmatter5/meshvault/main/unraid/meshvault.xml`
3. Check the two paths:
   - **App Data** → `/mnt/user/appdata/meshvault` (put this on **cache or an SSD**)
   - **Model Library** → wherever your models live, mounted **read-only** by default
4. Apply, then open the WebUI and **create the first account** — it becomes the administrator.

### Or by hand

Docker tab → Add Container → toggle to advanced view:

| Field | Value |
| --- | --- |
| Repository | `ghcr.io/007darkmatter5/meshvault:latest` |
| Network Type | `Bridge` |
| Port | `8080` → `8080` |
| Path | `/mnt/user/appdata/meshvault` → `/data` (rw) |
| Path | `/mnt/user/models` → `/models` (ro) |
| Variable | `MeshVault__Libraries__0__Name` = `Models` |
| Variable | `MeshVault__Libraries__0__Path` = `/models` |

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

If your models came out of [Manyfold](https://manyfold.app), hit **Import** on the
Libraries page: it reads each `datapackage.json` and fills in real titles, tags,
collections and designers. It never overwrites anything you have set by hand.

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

### Behind a reverse proxy

Forwarded headers are honoured, so SWAG, Nginx Proxy Manager and Traefik work without
extra configuration. Terminate TLS at the proxy and forward to port 8080.

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
| `tests/MeshVault.Tests` | 176 tests |

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

**Accounts.** The first is the administrator; registration then closes. Collections and
favorites are per-user. The last administrator cannot be demoted, suspended or deleted.

---

## Licence

MIT. See [LICENSE](LICENSE).
