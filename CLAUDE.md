# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build                                  # whole solution
dotnet test                                   # all 570 tests, ~5s
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

Migrations apply automatically at startup via `DatabaseInitializer`, and `DatabaseBackup` copies the
database aside first whenever any are pending. **A failed backup stops the server**, deliberately:
some migrations move rows rather than columns and cannot be undone, so starting anyway would apply
one with no way back. A server that refuses to start with a clear reason is recoverable in a way a
merged database is not. Copies land in `<DataPath>/backups/`, newest five kept, taken with
`VACUUM INTO` rather than a file copy — SQLite holds recent writes in a `-wal` sidecar, so copying
the `.db` alone can miss exactly the newest data.

When a migration drops a
column whose data must survive, hand-write the transfer SQL into the generated `Up()` **before**
the `DropColumn` calls — see `DesignersCollectionsAndFavorites`, which moved `IsFavorite` into a
per-user table and a `Designer` string into an entity without losing anything.

`SharedCollections` is the other one, and the harder case: dropping `Collection.OwnerId` means two
accounts' "To Print" become one, so it unions the memberships onto the survivor **before** the new
unique index exists. `StarTheFilingCollection` then backfills `PrimaryCollectionId` to whatever the
old alphabetical rule was already choosing, so the pair moves nothing on disk.

**Why that is two migrations and not one.** The SQLite provider rebuilds a table both to drop a
column and to add one carrying a foreign key, and raw SQL issued once a rebuild is pending reads a
database midway through being rearranged. EF warns (`Migrations[30200]`), and the fix it suggests is
a subsequent migration — which starts with nothing pending. Written as one migration it warns twice;
split, both are silent. Worth checking after adding any data-moving `Up()`:

```bash
dotnet ef database update -p src/MeshVault.Data -s src/MeshVault.Web 2>&1 | grep -c pending
```

28 is the baseline from migrations predating this note, so only an increase means something.

`SharedCollectionsMigrationTests` covers the pair by actually running migrations against a file
database — every other test builds its schema with `EnsureCreated`, which skips migrations entirely,
so nothing else here would notice a data-moving `Up()` being wrong until it ran against a real
library, once.

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

`ICurrentUser.UserId` is the owner of **favorites**, resolved from claims by `SignedInUser`.
Anonymous requests fall back to `Users.LocalUserId` (`"local"`), which predates authentication; the
first account to register adopts that data (`AccountSetup.AdoptLegacyDataAsync`). Tags, designers,
source metadata **and collections** are shared — they describe the model, not the viewer.

Collections used to be per-user and are not any more, because `{collection}` is a level in the
folder template: a collection names a folder on disk, which makes it a fact about the model. While
it was owned, the layout of the share depended on who was signed in. Favorites are the contrast and
stay per-user — two people can disagree about what is a favourite and nothing moves.

Consequences that are easy to undo by accident: deleting an account must **not** delete collections
(`UserAdmin` deletes only favorites now), `AdoptLegacyDataAsync` has nothing to adopt for them, and
a public visitor sees the library's collections the way they already see its tags.

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
- `GroupReconciler` does the same one level up, linking *separate models* that hold the same sculpt.
  Browse lists `GroupKey IS NULL OR GroupPrimary` so a group shows once.

**`GroupKey`/`GroupName`/`GroupPrimary` are derived, and stored anyway.** Derived because a group is
just "these folders hold one sculpt", which the files already say; stored because Browse has to
filter on it, and computing it per query is the correlated sub-select SQLite cannot do (see
*Testing notes*). So they are a cache, and the rule is that nothing writes them except
`GroupReconciler.ReconcileAsync`, which is called after everything that can change the answer:

| after | why |
|---|---|
| `ScanService` | a scan added the fourth cut of a mini |
| `OrganizeService` | folders were created, merged or emptied — the memberships moved |
| `VariantReindexer` | the vocabulary changed, so sculpt keys did |
| `ModelDetail.SaveVariant` | somebody named a sculpt by hand |

This replaced a `Regroup` page where `GroupPlanner` proposed and you approved. The reasoning for
approval was that "a library that rearranges itself after every scan is worse than one that never
does" — true of anything that moves files, and **grouping moves nothing**; it changes how Browse
lists what is already there. Approval meant the library was grouped only if you remembered to ask,
and stopped being grouped correctly as soon as a scan found another cut.

The property that makes it safe to run unattended is that it **settles**:
`Reconciling_a_settled_library_changes_nothing` pins that a second pass returns 0. It can only
settle because sculpt keys are stable — organizing pins them, and a hand-set one is never
overwritten.

There is no Ungroup. Correcting a file's sculpt is what separates two folders, because that is where
the grouping comes from; a button that overrode the reading would be undone by the next scan.

`ModelFile.VariantSetByUser` and `ModelEntry.NameSetByUser` are the same bargain: the app proposes,
the person decides, and the decision outlives the proposal. Never overwrite a row carrying one.

### The vocabulary

Settled with the library's owner, and worth using consistently in the UI:

| term | is | in code |
|---|---|---|
| **sculpt** | which mini — the grouping, and the folder once organized | `ModelFile.SculptKey`, and `ModelEntry` in the organized shape |
| **model** | a sculpt plus the variant combination that makes one copy unique | one `ModelFile` |
| **variant** | a tag on a model; several combine — Supported + Hollowed + No logo | `ModelFile.VariantLabel` |
| **collection** | a pack, or any curated group; names a folder level | `Collection` |

"Orc Warband holding Orc Chief, Orc Grunt and Orc Shaman" is a *collection* of three *sculpts*, each
with several *models* in it. `SculptPanel` is where that shape is shown and edited, and it says so
out loud when a folder holds more than one sculpt — everywhere else in the app such a folder reads
as one thing under the pack's name, which is the confusion this vocabulary exists to end.

**`SculptPanel` replaced an editor inside a row of the file table.** That one could only correct a
single file, so renaming a sculpt with six exports meant typing the same name into six dialogs — one
slip leaving two sculpts a letter apart with nothing on screen saying why they had stopped grouping.
`ModelEditor` now has the sculpt-level verbs: `RenameSculptAsync`, `SetSculptAsync` (many files at
once) and `SetVariantsAsync`. **Renaming onto a name another sculpt already has merges them**, which
is not a special case — the key is what groups — so there is no separate merge to drift out of step.

A sculpt's display name is *derived from its file's name on every scan*, and the app renames files
itself. Organizing under a case convention therefore used to relabel the library on the next scan —
`UD 067 Hole Trap` coming back as `ud 067 hole trap`, read off a filename the app had rewritten.
`VariantClassifier.Apply` now keeps the stored spelling when the new one differs by **case alone**;
the key is lowercased regardless, so nothing groups differently. `SculptNameRestorer` repairs a
library already caught, out of the `OrganizeStep.From` paths the undo record keeps anyway.

That was a repair, not a fix. `OrganizeExecutor` now **pins** every mesh it moves or renames
(`ModelFile.VariantSetByOrganize`), and `Apply` skips a pinned row. Both halves of what the
classifier reads are destroyed by organizing — the words in the name, and the folders standing
above the file inside its model — so a file filed out of `Supported/goblin.stl` into
`Goblin/goblin.stl` came back from the next scan as a plain export. Kept apart from
`VariantSetByUser` because only one of the two is a decision somebody made and worth showing as
such; `ResetVariantAsync` clears both, or the bookkeeping flag would be the reason a person could
not get detection back.

**A name that is nothing but variant words yields no sculpt at all.** `presupported.stl` says which
flavour it is and never which mini. It used to borrow the model's folder name, which reads well for
`Goblin/supported.stl` and disastrously for a container: a loose download in the inbox became a
sculpt called `inbox` — the inbox directly contains a mesh, so `FolderScanner` makes it a model
named after itself — and `STLs/presupported.stl` a sculpt called `STLs`. The folder is a sculpt
often enough to be tempting and a container often enough to be wrong, and nothing there can tell
which. `SculptKey` is left null, the variant is still recorded, and `OrganizePlanner` reports the
file `Incomplete` rather than filing it under the `{sculpt}` placeholder — which is also why that
token now falls back to **nothing** rather than to "Unsorted", the same bargain `{variant}` makes.

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

Pruning stops at it. `OrganizeExecutor.PruneEmptyFolders` walks up removing emptied source folders,
and once filing has done its job the inbox is always one of them -- so every successful organize
used to end with the inbox deleted and the next download with nowhere to land. It is a floor to
that walk exactly as the library root is, matched case-insensitively because the share may spell it
differently from the setting. Husks *inside* the inbox are still cleared.

### Organizing

`OrganizePlanner` proposes; `OrganizeExecutor` applies; nothing else calls either. `AllowOrganize`
is permission, not instruction, and is checked in the executor rather than only the page.

Every row carries `PlannedMove.Tokens` — what the folder template resolved, token by token. A
rendered destination reads like any other, so "why did it choose that path" was unanswerable from
the page; `TokenTrace` shows it under the path, marking a token that fell back rather than printing
its placeholder, since "Unsorted" the answer and "Unsorted" the designer look identical written
down. `MoveOutcome.Incomplete` renders alongside `Collides` and `Unusable`: it used to fall through
to the default arm and show an empty destination with its reason nowhere on screen.

`{sculpt}` in the folder template is the one token that changes how many folders come out of a
model: a pack of ninety-eight breaks apart, and four folders holding one mini between them come
together. Same rule, both directions.

**The shipped default is `{designer}/{collection}/{sculpt}`**, in `OrganizeRules` and in
`Organize.razor`'s `DefaultFolderTemplate` — change both together. It was `{designer}/{model}`,
which has no `{sculpt}` in it, so a library that had never had a template chosen **never split a
pack at all**: a folder called "Orc Warband" holding three minis planned one move and stayed one
folder called "Orc Warband", which is the shape organizing exists to undo.
`The_shipped_default_files_a_pack_by_sculpt` pins it, and goes to the planner directly rather than
through the test harness's `Plan` helper — that helper pins `{designer}/{model}`, because nearly
every planner test was written against it and inheriting the default would silently rewrite them
into asking a different question.

`{collection}` is the only token whose value a model can have **several** of, and it lives in one
folder — so `ModelEntry.PrimaryCollectionId` is the star that picks. It used to be whichever sorted
first alphabetically, which meant a collection called "Archive" quietly outranked the one somebody
organises by, and adding a model to a new collection could move it on disk for no visible reason.

The rules, all in `ModelEntry.PrimaryCollection` and `ModelEditor.SettleStar`:

- One collection is **implicitly** primary and stores no star — one less value to keep in step.
- Gaining a second **materialises** that implicit star, so nothing moves. Without this, adding a
  model to a second collection would leave it with two collections and no star, collapse the level,
  and quietly un-file it.
- Leaving the starred collection **clears** the star rather than moving it. Which survivor should
  name the folder is not guessable, and no star is a shape somebody can see and correct.
- A star pointing outside the memberships is ignored, so it can never name a folder the model has no
  claim to.
- `{collection}` falls back to **nothing**, like `{sculpt}` and `{variant}`, so a model with no
  primary loses the level instead of filling an `Unfiled/` folder with most of the library.

`ApplyBulkEditAsync` shares `SettleStar` deliberately: filing per collection instead would leave a
few hundred models with two collections and no star, and the next organize would un-file every one.

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

**A model's name is derived from its folder, so three places have to derive it.**
`FolderScanner` reads it, and it stopped tracking anywhere the row was written without a scan
watching. All three said one thing while the path said another:

- `LibraryIndexer` set `Name` **only on insert**, so nothing ever put a stale one right.
- `OrganizeExecutor` named a *new* model from its destination (`OwnerForAsync`) but left a **reused**
  one alone — so two cuts of a mini merging into `otto bismark` kept the name of whichever row won
  the merge, "Otto Bismark supported".
- `OrganizeUndo` restored `RelativePath` and not `Name`, undoing the half nobody could see.

All three now re-derive unless `NameSetByUser`, which is the same bargain as everywhere else.

**A pack's leftovers follow the pack.** Splitting a folder of several sculpts leaves files the
reading places nowhere — a readme, a licence — and filing them under whichever mini sorted first
would be a guess. They used to stay put, which left the pack folder standing holding nothing but
them: a model with no models in it, sitting in Browse beside the sculpts that came out of it, and a
row **a scan would never create** (a folder becomes a model by holding a mesh). They now go to the
folder template rendered with **no sculpt** — the same template one level up, which is the folder
every mini from that pack shares. An empty result means the template was nothing but `{sculpt}`, and
they stay put rather than being dropped at the library root.

Known and left alone: that folder usually holds no mesh of its own, so the readme is not indexed
once it gets there. It is in the right place for a person reading the share, and invisible to the
catalog — the same as before, minus the phantom model.

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

**`NameCasing.VariantSeparator` is `--`**, and it separates a sculpt from its variants:
`ud-001-wall--hollowed-supported`. One dash is indistinguishable from a word break — "wall-no-logo"
cannot say whether the sculpt is "Wall" or "Wall No" — and an underscore vanishes under snake_case,
which spends underscores on every word. It has to survive `NameCasing.Apply`, which treats every
other non-alphanumeric character as a word break, so that method splits on it and cases each side.
Only a **tight** separator counts: "Wall -- Door" is a creator padding a dash out and still collapses
to one break, while "wall--door" is structure. `Sanitize` trims it off a file with no variants, so a
plain export is `ud-001-wall`, not `ud-001-wall--`.

Variant labels are ordered **alphabetically**, not by `PreviewRank`. Rank is a display preference —
which export previews best — and letting it order the words in every filename meant nudging one
silently changed the other.

The last preset, "Rebuild the names", is the only one that uses this; everything above it keeps
`{file}` and only changes its case. `A_renaming_preset_never_throws_away_which_cut_of_a_mini_a_file_is`
pins the invariant that matters: a renaming preset must carry `{file}` *or* `{variant}`, or every cut
of a mini flattens onto one name and gets numbered.

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

### Downloads

`/download/{file,model,sculpt,collection}` hands back the originals — a single file as it
sits, the rest zipped on the way out. Plain minimal APIs and plain `<a href>` links in the UI:
the only way to start a download from a circuit is to marshal the whole file through SignalR
first, which for a 2 GB model means holding it in memory to hand it over.

`DownloadCatalog` resolves what a download covers **before** a byte is written. Once the first
chunk leaves, the status code is spent — "there is nothing here" has to be answerable while a
404 is still possible, which is why an empty set is a 404 rather than an empty zip.

- **Every download link needs `data-enhance-nav="false"`.** `blazor.web.js` turns on enhanced
  navigation, which intercepts internal `<a>` clicks, `fetch`es the URL, and only on finding the
  reply is not HTML falls back to a real navigation. So the archive is built and streamed
  **twice**, and the save dialog waits out the whole wasted first copy. Measured over CDP on a
  600 MB model: 2 requests, dialog after 12.62s, 32.87s total — against 1 request, 0.08s and
  16.82s with the attribute. curl cannot see this, because enhanced navigation only exists
  inside the page; it takes a real browser to catch it.
- **An account, not `Policies.View`.** Public browsing hands a visitor a decimated, quantised
  preview; this hands over the creator's file exactly as it was bought. The endpoint enforces
  it; `ModelDetail.CanDownload` only keeps buttons off a page where they would 401.
- **`AllowSynchronousIO` is required.** `ZipArchive` writes its headers and central directory
  synchronously, and Kestrel forbids blocking writes to a response body by default. Without it
  every archive download 500s — and no unit test catches it, because a `MemoryStream` allows
  what a response body does not. Verified over real HTTP.
- **`MinDataRate` is cleared per request.** Kestrel hangs up on a response below ~240 B/s. An
  archive read off a 1.4 MB/s share for hours will dip under that, and losing an hour-old
  download to a momentary pause defends against nothing.
- `ArchiveThrottle` caps concurrent archives at 2, and the stream claims `ForegroundActivity` —
  same scarce share, same reasoning as `ThumbnailService`.
- **A grouped model downloads its whole group.** The detail page already shows four folders as
  one thing, so fetching only the folder you arrived at hands back less than the page shows.
  Collections expand groups for the same reason: Browse lists only `GroupPrimary`, so that is
  the row that got added. `GetCollectionSizeAsync` must expand identically or the confirmation
  dialog under-promises.
- Entry paths keep a file's layout beneath its model folder; more than one model means a folder
  each, named from `ModelEntry.Name`. Duplicates are numbered — zip permits two entries with one
  name and most tools extract them over each other, quietly returning fewer files than asked for.
- `ArchiveWriter` is split out from the endpoint so a test can open the zip again. A missing file
  is skipped rather than thrown on: an archive short of a file beats a truncated one, which looks
  identical and says less.

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
`The_role_can_always_be_handed_on` pins this. The cap's original reason was not accounts at all:
`{collection}` was a folder token resolved per-user, so **two admins would have filed the same
library two different ways on disk** — measured on the author's library at the time, 2 models
unfiled as the owner and 106 as a second account.

**That reason is gone**: collections are shared now. The cap is kept deliberately rather than
because anything still needs it, so lifting it is a decision rather than a repair — remove the
"exactly one Admin" rule in `CreateAsync` and the role hand-over in `SetRoleAsync`, and keep the
last-admin and self-delete guards, which protect against something else entirely.

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
