# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build                                  # whole solution
dotnet test                                   # all 517 tests, ~6s
dotnet run --project src/MeshVault.Web        # http://localhost:5082 in Development

# One class, or one test
dotnet test --filter "FullyQualifiedName~MeshDecimatorTests"
dotnet test --filter "FullyQualifiedName~UserAdminTests.The_last_administrator_cannot_be_deleted"

# Full failure detail: -v q hides it, and the summary line alone will not say which test failed
dotnet test --nologo 2>&1 | grep -A8 "Error Message"
```

**Stop the running app before building.** It holds `MeshVault.Core.dll` and `MeshVault.Data.dll`
open, and the build fails with `MSB3021 ... being used by another process`:
`taskkill //F //IM MeshVault.Web.exe`.

### Migrations

`dotnet-ef` is installed globally; it needs `~/.dotnet/tools` on PATH.

```bash
dotnet ef migrations add <Name> -p src/MeshVault.Data -s src/MeshVault.Web -o Migrations
```

Migrations apply automatically at startup via `DatabaseInitializer`. When a migration drops a
column whose data must survive, hand-write the transfer SQL into the generated `Up()` **before**
the `DropColumn` calls — see `DesignersCollectionsAndFavorites`, which moved `IsFavorite` into a
per-user table and a `Designer` string into an entity without losing anything.

## Architecture

Four projects, dependencies pointing inward: `Web → Data → Core`, with `Tests` referencing all
three (it needs `Web` for the background services and Razor guard).

- **`MeshVault.Core`** — no EF, no ASP.NET. Domain models, `FolderScanner`, mesh parsing
  (`Meshes/`), and the software renderer (`Imaging/`).
- **`MeshVault.Data`** — `MeshVaultDbContext` (also the Identity store), the reconciling
  `LibraryIndexer`, read-side `ModelCatalog`, write-side `ModelEditor`, the variant vocabulary
  (`VariantStore`, `VariantReindexer`).
- **`MeshVault.Web`** — Blazor Server UI, auth, HTTP endpoints, background workers.

### Render modes — read this before touching `App.razor` or `MainLayout.razor`

Interactivity is declared **per page** (`@rendermode InteractiveServer`), never globally on
`<Routes />`. This is load-bearing:

- Sign-in must be **statically** rendered. `EditForm`'s `FormName` only participates in static
  SSR form mapping, and setting an auth cookie needs a live response. Making the router globally
  interactive breaks login with *"The POST request does not specify which form is being submitted"*.
- Because the layout is static, MudBlazor's providers live in `Layout/MudProviders.razor`, hosted
  as a single interactive island from `App.razor`. Dialogs and snackbars stop working if that is
  removed.
- For the same reason the app bar uses plain links and a form post rather than `MudMenu`.

No test covers this. Manually exercise sign-in after changing `App.razor`, `Routes.razor`,
`MainLayout.razor` or anything under `Components/Account/`.

### Diagnosing an unresponsive UI

A circuit that never connects looks exactly like working software: the prerendered HTML
paints a complete page that ignores every click. `ReconnectModal` only covers a circuit
that connected and then dropped, so `connection-check.js` shows a banner when one never
arrives — `MudProviders` calls `meshvaultInteractive()` from `OnAfterRenderAsync` once
`RendererInfo.IsInteractive`, and that island is on every page.

`/diagnostics` is **statically rendered on purpose**, and its browser checks are plain
JavaScript. A page that needed a circuit to report a missing circuit would go blank
exactly when it was wanted. Keep it that way.

The usual cause on a self-hosted box is a reverse proxy not passing WebSockets, which is
why `/diag/ws` exists to test the upgrade without disturbing a live circuit.

### Per-user data

`ICurrentUser.UserId` is the owner of collections and favorites, resolved from claims by
`SignedInUser`. Anonymous requests fall back to `Users.LocalUserId` (`"local"`), which predates
authentication; the first account to register adopts that data (`AccountSetup.AdoptLegacyDataAsync`).
Tags, designers and source metadata are shared — they describe the model, not the viewer.

`ModelCatalog` returns `ModelCard` (model + `IsFavorite`) rather than `ModelEntry`, because
"is this a favorite" depends on who is asking.

### Scanning and indexing

A folder becomes a model when it **directly** contains a mesh or CAD file; subfolders with no
models of their own are absorbed into it. `LibraryIndexer` **reconciles** rather than rebuilds, so
tags, notes, favorites and collections survive rescans — `LibraryIndexerTests` pins this. Editing a
file clears its derived data (hash, triangle count, thumbnail state) so it regenerates.

Reconciliation is keyed on `ModelEntry.RelativePath`. **Anything that moves a folder must rewrite
that path in the same operation**, or the next scan reads the move as one model deleted and another
added — taking its tags, notes, collections, favorites and grouping with it. This is why
`OrganizeExecutor` writes to the database as it goes rather than leaving it to a rescan.

### Variants, sculpts and groups

Creators ship the same sculpt several times over — supported, unsupported, hollowed, no-logo — and
say which is which in the filename or a containing folder. Three layers handle it, and they only
ever *propose*:

- `VariantClassifier` reads a file's name into a **sculpt key** (which mini) and a **variant label**
  (which flavour), against a vocabulary of `VariantDefinition` rows the user curates under
  Settings → Variants. Its `PreviewRank` decides which export a preview opens on and which supplies
  a card image, so "supports look bad" is a number on a row rather than a rule in the code.
- `VariantGrouper` gathers a model's files into sculpts for the viewer and file table.
- `GroupPlanner`/`GroupStore` do the same one level up, linking *separate models* that hold the same
  sculpt. `ModelEntry.GroupKey`/`GroupPrimary` are written only by an approved regroup, never by a
  scan, and Browse lists `GroupKey IS NULL OR GroupPrimary` so a group shows once.

`ModelFile.VariantSetByUser` and `ModelEntry.NameSetByUser` are the same bargain: the app proposes,
the person decides, and the decision outlives the proposal. Never overwrite a row carrying one.

A sculpt's display name is *derived from its file's name on every scan*, and the app renames files
itself. Organizing under a case convention therefore used to relabel the library on the next scan —
`UD 067 Hole Trap` coming back as `ud 067 hole trap`, read off a filename the app had rewritten.
`VariantClassifier.Apply` now keeps the stored spelling when the new one differs by **case alone**;
the key is lowercased regardless, so nothing groups differently. `SculptNameRestorer` repairs a
library already caught, out of the `OrganizeStep.From` paths the undo record keeps anyway.

`VariantClassifier.Version` plus a fingerprint of the definitions gates the startup recompute in
`VariantReindexer` — bump it when a pass would now produce a different result, including derived
things like which file a card image points at.

### The inbox

`Library.InboxPath` names a folder *inside* the library where downloads land. Being inside matters:
filing is then a rename on one volume rather than a copy between two, and the model keeps its id.

`LibraryIndexer.IndexFolderAsync` scans the inbox alone, so noticing three new files does not cost
a walk of the whole share. Two things make it safe, and both are easy to undo by accident:
`FolderScanner.Scan(root, subPath)` still reports paths **relative to the library root** (the
reconciliation key), and the removal set is narrowed to models inside that folder. A full scan
deletes every model it did not see; run that after looking only at the inbox and it empties the
library in one click. `An_inbox_scan_never_removes_the_rest_of_the_library` pins it. A partial scan
also leaves `LastScannedUtc` alone, or it would tell the startup scan the share had been walked.

Nothing promotes a model out of it. A folder template never yields a path inside the inbox, so
filing empties it as a side effect. `Inbox.Missing` blocks a model only for a **designer or tag** —
the tokens whose fallback would be a lie. "Unfiled" standing in for a collection is simply true.

### Organizing

`OrganizePlanner` proposes; `OrganizeExecutor` applies; nothing else calls either. `AllowOrganize`
is permission, not instruction, and is checked in the executor rather than only the page.

`{sculpt}` in the folder template is the one token that changes how many folders come out of a
model: a pack of ninety-eight breaks apart, and four folders holding one mini between them come
together. Same rule, both directions.

`{variant}` is the one token whose fallback is **empty rather than a word**. Every other names
something a model has — a designer, a year — so a gap is worth marking; a variant is a thing a file
either is or is not, and `otto-bismark-plain` says nothing `otto-bismark` does not. A marked file
still says so even as the only copy owned, so the rule is per file and not "does this sculpt need
disambiguating". `Sanitize` then trims a stray leading or trailing `-`, `_` or space, or
`{sculpt}-{variant}` would leave the template showing through as a dangling dash.

Names are numbered in **two** places, and both are needed. `PlanRenames` numbers within one model,
while the names are being rendered. `MarkColliding` numbers *across* models, because a destination
is not per model — `{sculpt}` exists to bring separate folders holding one mini together, and two
merging models are named one at a time with neither aware of the other. Only `MarkColliding` sees
every landing in the library at once, so it is the only place that can. With renaming **off** a
clash is still a `PlannedConflict`: the name is not ours to choose, so the file stays put.

Rules the executor will not bend:

- Files, never folders. A half-done folder move leaves nothing recorded; file by file, every step is
  either done and written down or not attempted. **One exception**, `MatchCaseOnDisk`: a folder that
  already exists under a different case is respelled with `Directory.Move`, which keeps its parent
  and its identity and is atomic. Without it a case-only change never reaches a case-insensitive
  share — `CreateDirectory` sees the old spelling and does nothing, `File.Move` resolves both paths
  to the same file — and the database is left recording a folder that is not the one on disk, which
  the next scan reads as a delete plus an add.
- Never overwrite — **except a file onto itself**. `File.Move(from, to)` asks the filesystem whether
  the destination exists, and on a case-insensitive mount `alco.stl` exists whenever `Alco.stl`
  does, so a case-only rename can never happen and reports a clash with itself. `MoveFile` counts
  the directory entries matching case-insensitively: one means it *is* this file and `overwrite:
  true` is safe, two means two files and it refuses. `MatchCaseOnDisk` hits the same wall without an
  overwrite overload and goes via a staging name. **Windows renames case in place and hides all of
  this** — it took the Linux container to surface it, so test case behaviour there.
- Never overwrite. Same name and same length is a *candidate* copy — both are hashed before either
  goes, and one that differs is left where it is. Hashes are cached in `ModelFile.Sha256`, which
  `LibraryIndexer` clears when bytes move, so a stored hash is current or absent, never stale.
- Every file must end up inside its model's folder. A file blocked by a clash while its model moved
  gets a row of its own at the folder it is actually in (`RehomeStrandedAsync`).

`NameCase` (kebab, snake, camel, Pascal, or `AsWritten`) is applied by `PathTemplate.Render` **per
segment and before `Sanitize`** — per segment because `NameCasing` treats every non-alphanumeric
character as a word break and would otherwise eat the slashes, and before sanitising so the last
word on length, trailing dots and reserved device names stays with `Sanitize`. Run it after and
`_CON` turns back into `con`. Folder and file casing are stored separately on `Library`;
`AsWritten` is zero, so every library predating the feature renders exactly as it always did.
`NameCasing.Words` deliberately does not split a letter from a digit — `Cinderwing3D` is one word —
and keeps the tail of each word for Pascal/camel, or `SUP` becomes `Sup`.

A row whose folder is **already right** can still be work: renaming applies to it. `AlreadyThere`
carrying renames is actionable in both `OrganizeExecutor.Actionable` and
`OrganizePlan.ActionableModelIds` — leaving it out of the first made the plan promise "6 renamed"
and do nothing, and out of the second hid those rows from the checkboxes. The per-file move guard
is `Ordinal`, not `OrdinalIgnoreCase`: `Wall.stl` → `wall.stl` is a real rename, and skipping it
leaves the database saying one name and a case-sensitive share holding another.

The page can run against a subset. Ticks are per model, and `OrganizePlan.For` narrows the plan
before it is handed over — narrowing only ever drops rows, so what runs is a subset of what was on
screen rather than a second plan. `VacancyNeeded` warns about the one case narrowing breaks: the
planner allows a destination whose occupant is leaving in the same run, and leaving that occupant
unticked means the folder is still there.

It runs on a background task via `OrganizeService`, like scans. Doing several hundred blocking file
moves on the circuit's thread leaves the page unable to render the progress it is being handed — it
looks finished while the share is plainly still working.

### Mesh pipeline

`StagedMeshFile` copies a mesh to local disk once before parsing. The renderer makes two passes
(framing, then rasterising), and the target library may be a slow network share — measured at
~1.4 MB/s on the author's setup, where re-reading turned a 1.1s render into 131s. Parallel readers
bought only 1.24x, so `ThumbnailService` uses concurrency 3 deliberately.

`MeshPayload` sends the browser quantised 16-bit positions (18 bytes/triangle vs 50 in a binary
STL); the shader derives normals. Over budget, `MeshDecimator` reduces by **vertex clustering** —
never by keeping every Nth triangle, which shreds dense models into disconnected facets.

Viewer geometry is Z-up mesh data rotated to three.js's Y-up on load. Skipping that rotation makes
horizontal drag spin the model about its front-to-back axis.

`GeometryCache.FormatVersion` invalidates cached payloads; bump it when the payload or the way it
is built changes, and `PruneOldVersions()` clears the orphans at startup.

Setting a card image also records the camera (`SnapshotView{X,Y,Z}`) so the viewer reopens on that
angle. It is stored as a **multiple of the bounding radius**, not in scene units — the viewer
frames every model to fit, so a raw distance would put the camera inside a smaller mesh.

### Background work

`ScanService`, `OrganizeService` and `ThumbnailService` all run off the request thread and raise a
`Changed` event that pages subscribe to.

- Clear "running" state **before** raising the completion event. Subscribers call `IsRunning()`
  while handling it, so announcing first leaves the UI stuck on "Scanning…" forever.
- Invoke handlers one at a time; one dead circuit must not stop the rest being notified.
- `ForegroundActivity` is the backpressure valve: the geometry endpoint claims it, and the
  thumbnail worker waits. Without it a model the user opened queues behind 32 background reads.

### HTTP endpoints

`MediaEndpoints` (`/thumb`, `/mesh`, `/snapshot`) and `/health` are plain minimal APIs because the
browser requests them directly. Media requires authentication; `/health` is anonymous and returns
nothing about the library.

They also **disable `IStatusCodePagesFeature`** per request. Left on, a 404 for a missing thumbnail
re-executes through Blazor and answers an `<img>` tag with 21 KB of HTML, and a 404 from a POST
becomes a content-type 400.

### Accounts

First registration becomes Admin and closes registration (`SettingKeys.RegistrationOpen`).
`UserAdmin` refuses to demote, suspend or delete the last administrator, or to delete the acting
account — on a self-hosted app that mistake means editing SQLite by hand. Deleting a user also
removes their collections and favorites, which reference the owner by id rather than a foreign key
and would otherwise linger unreachable.

**There is exactly one Admin.** `CreateAsync` refuses a second; `SetRoleAsync` *hands the role
over* — promoting anyone demotes the incumbent, atomically. Refusing the promotion instead would
deadlock against the last-admin guard: with demotion of the only admin refused too, no sequence of
single steps could move the role, and on a self-hosted box that is unrecoverable.
`The_role_can_always_be_handed_on` pins this. The reason for the cap is not really accounts —
`{collection}` is a folder token resolved per-user (`OrganizePlanner` filters
`Collections.Where(c => c.OwnerId == userId)`), so **two admins would file the same library two
different ways on disk**. Measured on the author's library: 2 models unfiled as the owner, 106 as a
second account. If collections ever become a property of the model rather than the viewer, this cap
is what can be lifted.

Password rules are **length-only** (10 characters, no composition requirements). They are declared
in `Program.cs`, `Register.razor`, `Account.razor` and each test harness — change all of them together.

Settings chosen in the UI go in the `Settings` table via `SettingsStore`; deployment settings come
from `MeshVaultOptions`/environment. Do not conflate them.

## Testing notes

- **SQLite will not `ORDER BY` a `DateTimeOffset`**, and EF throws rather than falling back. It
  compiles, reads fine, and dies the moment someone picks that sort — Browse's "Recently added"
  shipped broken this way. Order by `Id` (handed out at insert, so the same order as `AddedUtc`)
  or sort after loading. `ModelSortTests` runs every sort the UI offers for exactly this reason.
- **`Progress<T>` dispatches asynchronously.** Assertions on collected reports race the final
  report. Use `SyncProgress<T>` in tests; this caused a ~1-in-5 flake.
- **SQLite has no `APPLY`**, so a correlated sub-select in a projection — "the best-ranked file of
  each model" — throws at runtime rather than falling back. Read the two sets flat and join them in
  memory; `GroupPlanner` does exactly that and says why.
- **EF queries do not see uncommitted changes.** `OrganizeExecutor` saves a destination at a time, so
  a file that moved moments ago has its new path in memory and its old one in the database. Ask the
  change tracker before the database, or a lookup by path silently misses it.
- **Razor does not warn about unresolved components** — it emits them as literal markup, so a
  removed MudBlazor component renders as dead HTML with no build error. `RazorComponentTests`
  reflects over the assembly to catch this. Generic components reflect as ``MudSelect`1``.
- Identity test harnesses need `services.AddDataProtection()`, or `AddDefaultTokenProviders` fails
  to resolve.
- Rendering tests decode the PNG back to pixels and assert coverage. A test that only checks "a PNG
  came out" passed happily while the depth buffer was inverted and nothing drew at all.
- The library share is slow enough that performance intuitions are usually wrong here. Measure
  before optimising — a scratch console project referencing `MeshVault.Core` is the quickest way.

## Deployment

Ships as a container image to GHCR via `.github/workflows/ci.yml`; `templates/meshvault.xml` is the
Unraid template. `/data` holds the SQLite database, thumbnails, geometry cache **and the data
protection keys** — without persisting those, every image update signs everyone out.
`README.md` has the install steps and the full configuration table.
