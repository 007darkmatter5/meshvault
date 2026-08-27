using MeshVault.Core.Models;
using MeshVault.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Data;

/// <summary>How a library should be laid out.</summary>
public record OrganizeRules
{
    /// <summary>Folder each model lands in, relative to the library root.</summary>
    public string FolderTemplate { get; init; } = "{designer}/{model}";

    /// <summary>
    /// Name for each file inside that folder. Ignored unless
    /// <see cref="RenameFiles"/> is set, and extensions are always kept.
    /// </summary>
    public string FileTemplate { get; init; } = "{model} - {file}";

    /// <summary>
    /// Off by default. A folder move is reversible and loses nothing; a rename
    /// throws away whatever the original name encoded, which is often the only
    /// record that a mesh was pre-supported or was version two.
    /// </summary>
    public bool RenameFiles { get; init; }
}

public enum MoveOutcome
{
    /// <summary>Already where the rules want it.</summary>
    AlreadyThere,
    Move,

    /// <summary>Something else is already using that path.</summary>
    Collides,

    /// <summary>The rules produced nothing usable, so the model is left alone.</summary>
    Unusable,

    /// <summary>
    /// Still in the inbox and missing something the template needs, so filing
    /// it now would scatter it under a fallback and cost a second pass.
    /// </summary>
    Incomplete,
}

public record PlannedMove(
    int ModelId,
    string ModelName,
    string From,
    string To,
    MoveOutcome Outcome,
    string? Problem = null,
    IReadOnlyList<PlannedRename>? Renames = null)
{
    public IReadOnlyList<PlannedRename> Renames { get; init; } = Renames ?? [];

    /// <summary>
    /// The sculpt this move carries, when one folder is being split into
    /// several. Null when the whole model moves as one, which is the ordinary
    /// case and how the planner behaved before splitting existed.
    /// </summary>
    public string? Sculpt { get; init; }

    /// <summary>
    /// The files this move takes with it. Empty means the whole folder goes,
    /// which is what a move without a split does.
    /// </summary>
    public IReadOnlyList<int> FileIds { get; init; } = [];

    /// <summary>
    /// Files that will not survive the move, because a byte-for-byte copy of
    /// each is going to the same place.
    /// </summary>
    public IReadOnlyList<PlannedDeletion> Deletions { get; init; } = [];

    /// <summary>
    /// Files that will stay where they are, because something different already
    /// claims the name they would land under.
    /// </summary>
    public IReadOnlyList<PlannedConflict> Conflicts { get; init; } = [];

    /// <summary>
    /// Template tokens this model had nothing for, so a placeholder stood in.
    /// "Unsorted" and "Unfiled" are real folders once this runs, and a model
    /// landing in one is almost never what was wanted -- it just cannot be seen
    /// from a destination path that reads like any other.
    /// </summary>
    public IReadOnlyList<string> Fallbacks { get; init; } = [];

    public bool IsSplit => Sculpt is not null;
}

public record PlannedRename(int FileId, string From, string To);

/// <summary>A file the plan would remove rather than move, and why.</summary>
/// <param name="Verify">
/// Whether the file must be proved byte-identical to what is already at the
/// destination before it goes. True for a suspected copy, where only the length
/// matched and the length is not proof. False for a sidecar, which is removed
/// because of what it is rather than what it duplicates -- there is nothing to
/// compare it against, since every copy of it is going too.
/// </param>
public record PlannedDeletion(int FileId, string Path, string Reason, bool Verify = true);

/// <summary>A file the plan cannot move, and what would let it.</summary>
public record PlannedConflict(int FileId, string Path, string Reason);

public record OrganizePlan(IReadOnlyList<PlannedMove> Moves)
{
    public int Moving => Moves.Count(m => m.Outcome == MoveOutcome.Move);
    public int AlreadyThere => Moves.Count(m => m.Outcome == MoveOutcome.AlreadyThere);
    public int Colliding => Moves.Count(m => m.Outcome == MoveOutcome.Collides);
    public int Unusable => Moves.Count(m => m.Outcome == MoveOutcome.Unusable);
    public int Incomplete => Moves.Count(m => m.Outcome == MoveOutcome.Incomplete);
    public int Renames => Moves.Sum(m => m.Renames.Count);

    /// <summary>How many folders a split would produce out of pack folders.</summary>
    public int Splitting => Moves.Count(m => m is { IsSplit: true, Outcome: MoveOutcome.Move });

    /// <summary>Source folders that break apart into more than one destination.</summary>
    public int PacksSplit => Moves
        .Where(m => m.IsSplit && m.To.Length > 0)
        .GroupBy(m => m.ModelId)
        .Count(g => g.Select(m => m.To).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);

    /// <summary>Destinations that gather files from more than one source folder.</summary>
    public int FoldersMerged => Moves
        .Where(m => m.IsSplit && m.To.Length > 0)
        .GroupBy(m => m.To, StringComparer.OrdinalIgnoreCase)
        .Count(g => g.Select(m => m.ModelId).Distinct().Count() > 1);

    /// <summary>Every file the plan would delete. Shown in full before anything runs.</summary>
    public IReadOnlyList<PlannedDeletion> Deletions => [.. Moves.SelectMany(m => m.Deletions)];

    /// <summary>Every file the plan cannot place, and why.</summary>
    public IReadOnlyList<PlannedConflict> Conflicts => [.. Moves.SelectMany(m => m.Conflicts)];

    /// <summary>
    /// How many models would land under each placeholder, worst first. What
    /// turns "some things went to Unfiled" into something anyone can act on.
    /// </summary>
    public IReadOnlyList<(string Token, int Models)> FallbackCounts =>
    [
        .. Moves.Where(m => m.Outcome == MoveOutcome.Move)
            .SelectMany(m => m.Fallbacks.Select(f => new { m.ModelId, Token = f }))
            .GroupBy(x => x.Token)
            .Select(g => (g.Key, g.Select(x => x.ModelId).Distinct().Count()))
            .OrderByDescending(x => x.Item2),
    ];

    /// <summary>Nothing to do, so applying would be a no-op.</summary>
    public bool IsEmpty => Moving == 0 && Renames == 0 && Deletions.Count == 0;

    /// <summary>Every model this plan has something to say about.</summary>
    public IReadOnlyList<int> ModelIds => [.. Moves.Select(m => m.ModelId).Distinct()];

    /// <summary>
    /// The same plan narrowed to a chosen set of models.
    /// </summary>
    /// <remarks>
    /// Narrowing here rather than planning again is deliberate: what is applied
    /// is then literally a subset of the rows that were read on screen, so a
    /// destination cannot quietly differ between looking and pressing. Deletions,
    /// conflicts and the fallback counts all hang off <see cref="Moves"/>, so
    /// they narrow with it.
    ///
    /// A plan is only ever narrowed, never widened, so nothing here can invent a
    /// move the planner did not already agree to.
    /// </remarks>
    public OrganizePlan For(IReadOnlySet<int> modelIds) =>
        new([.. Moves.Where(m => modelIds.Contains(m.ModelId))]);

    /// <summary>
    /// Left-out models whose folder a chosen model is being sent into.
    /// </summary>
    /// <remarks>
    /// The planner let that destination through because the model sitting in it
    /// was moving out in the same run. Leave that one behind and the folder is
    /// still occupied: nothing is overwritten — the executor refuses that — but
    /// the two sets end up sharing a folder and a row, which is not what either
    /// row on screen said would happen. Worth saying before the button, not
    /// after.
    /// </remarks>
    public IReadOnlyList<PlannedMove> VacancyNeeded(IReadOnlySet<int> modelIds)
    {
        var wanted = Moves
            .Where(m => modelIds.Contains(m.ModelId) && m.Outcome == MoveOutcome.Move)
            .Select(m => m.To)
            .Where(to => to.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. Moves
                .Where(m => !modelIds.Contains(m.ModelId) && m.Outcome == MoveOutcome.Move)
                .Where(m => wanted.Contains(m.From))
                .GroupBy(m => m.ModelId)
                .Select(g => g.First()),
        ];
    }
}

/// <summary>
/// Works out what organising a library would do, without touching a single
/// file.
/// </summary>
/// <remarks>
/// Separate from applying it on purpose. This is the first thing in MeshVault
/// that would write to somebody's library, and a plan that can be read in full
/// before anything happens is the difference between a tool and a gamble.
/// </remarks>
public class OrganizePlanner(
    IDbContextFactory<MeshVaultDbContext> factory,
    ICurrentUser user,
    VariantRules variants)
{
    public async Task<OrganizePlan> PlanAsync(
        int libraryId, OrganizeRules rules, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var userId = user.UserId;

        var models = await db.Models.AsNoTracking()
            .Where(m => m.LibraryId == libraryId)
            .Include(m => m.Designer)
            .Include(m => m.Tags)
            .Include(m => m.Files)
            .Include(m => m.Collections.Where(c => c.OwnerId == userId))
            .OrderBy(m => m.RelativePath)
            .ToListAsync(ct);

        var moves = new List<PlannedMove>();

        // Folders the plan will occupy, so two models sent to the same place are
        // reported rather than one silently landing inside the other.
        var claimed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            var staying = Destination(model, rules) == model.RelativePath;
            if (staying) claimed[model.RelativePath] = model.Id;
        }

        var library = await db.Libraries.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == libraryId, ct);

        // Which model holds the mesh for each sculpt, anywhere in the library.
        //
        // A companion left in an emptied folder is filed by this rather than by
        // its own folder's metadata. Rendering the template against the husk it
        // was left in would put it near its mesh but not with it — the folder it
        // came from need not share the mesh's collection or designer — and
        // "nearly there" is no better than stranded.
        var meshOwners = models
            .SelectMany(m => m.Files.Select(f => new { Model = m, File = f }))
            .Where(x => x.File.Kind is FileKind.Mesh or FileKind.Cad && x.File.SculptKey is not null)
            .GroupBy(x => x.File.SculptKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Model, StringComparer.OrdinalIgnoreCase);

        foreach (var model in models)
        {
            // Something dropped in the inbox and not yet described would file
            // under the template's fallbacks — Unsorted, Untagged — which is
            // the inbox problem again one folder along, and a second pass to
            // undo. Say what is missing instead of scattering it.
            if (Inbox.Holds(library?.InboxPath, model.RelativePath)
                && Inbox.Missing(model, rules.FolderTemplate) is { Count: > 0 } missing)
            {
                moves.Add(new PlannedMove(model.Id, model.Name, model.RelativePath, "",
                    MoveOutcome.Incomplete,
                    $"Still in the inbox and needs {Readable(missing)}."));
                continue;
            }

            // A template naming the sculpt asks for a folder per mini, which is
            // both how a pack of ninety-eight is broken up and how four folders
            // holding one mini between them are brought together.
            if (SplitsBySculpt(rules))
            {
                moves.AddRange(PlanSplit(model, rules, claimed, meshOwners));
                continue;
            }

            var destination = Destination(model, rules);

            if (string.IsNullOrEmpty(destination))
            {
                moves.Add(new PlannedMove(model.Id, model.Name, model.RelativePath, "",
                    MoveOutcome.Unusable, "The template produced an empty path for this model."));
                continue;
            }

            if (destination == model.RelativePath)
            {
                moves.Add(new PlannedMove(model.Id, model.Name, model.RelativePath, destination,
                    MoveOutcome.AlreadyThere, Renames: PlanRenames(model, rules)));
                continue;
            }

            if (claimed.TryGetValue(destination, out var other) && other != model.Id)
            {
                moves.Add(new PlannedMove(model.Id, model.Name, model.RelativePath, destination,
                    MoveOutcome.Collides,
                    "Another model is already going there. Give them different names first."));
                continue;
            }

            claimed[destination] = model.Id;
            moves.Add(new PlannedMove(model.Id, model.Name, model.RelativePath, destination,
                MoveOutcome.Move, Renames: PlanRenames(model, rules))
            {
                Fallbacks = FallbacksIn(rules.FolderTemplate, TokensFor(model)),
            });
        }

        return new OrganizePlan(MarkColliding(moves, models));
    }

    /// <summary>"a designer", "a designer and a tag", "a designer, a tag and a licence".</summary>
    private static string Readable(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        2 => $"{items[0]} or {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} or {items[^1]}",
    };

    /// <summary>
    /// Tokens the template asked for that this model has nothing for, so the
    /// placeholder will stand in. Asked of the values rather than matched
    /// against the rendered path: a designer genuinely called "Unsorted" must
    /// not read as a gap.
    /// </summary>
    private static List<string> FallbacksIn(
        string template, IReadOnlyDictionary<string, string?> tokens) =>
    [
        .. PathTemplate.TokenNames(template)
            .Where(t => tokens.TryGetValue(t, out var value) && string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    private static string Destination(ModelEntry model, OrganizeRules rules) =>
        PathTemplate.Render(rules.FolderTemplate, TokensFor(model), forFile: false);

    private static List<PlannedRename> PlanRenames(
        ModelEntry model, OrganizeRules rules, IEnumerable<ModelFile>? only = null)
    {
        if (!rules.RenameFiles) return [];

        var renames = new List<PlannedRename>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var file in (only ?? model.Files).OrderBy(f => f.RelativePath))
        {
            index++;
            var tokens = TokensFor(model);
            tokens["file"] = Path.GetFileNameWithoutExtension(file.FileName);
            tokens["index"] = index.ToString();
            tokens["kind"] = file.Kind.ToString();

            var stem = PathTemplate.Render(rules.FileTemplate, tokens, forFile: true);
            if (string.IsNullOrEmpty(stem)) continue;

            // The extension is never templated. It is what tells every other
            // program on the machine what the file is.
            var name = stem + file.Extension;

            // Two files rendering to the same name would have one overwrite the
            // other, so the later ones are numbered instead.
            var candidate = name;
            var suffix = 2;
            while (!used.Add(candidate))
            {
                candidate = $"{stem} ({suffix}){file.Extension}";
                suffix++;
            }

            if (!string.Equals(candidate, file.FileName, StringComparison.Ordinal))
                renames.Add(new PlannedRename(file.Id, file.FileName, candidate));
        }

        return renames;
    }

    /// <summary>Whether the folder template asks for a folder per sculpt.</summary>
    private static bool SplitsBySculpt(OrganizeRules rules) =>
        PathTemplate.TokenNames(rules.FolderTemplate)
            .Any(t => string.Equals(t, "sculpt", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Plans one destination folder per sculpt the model holds.
    /// </summary>
    /// <remarks>
    /// The same rule serves both shapes a library arrives in. A pack folder of
    /// ninety-eight minis yields ninety-eight moves; four folders each holding
    /// one export of the same mini yield four moves that all land in the same
    /// place, which merges them. Neither needs a special case.
    ///
    /// Companion files are placed by reading their names the same way, so a
    /// slicer project sitting beside its mesh follows it. Anything the reading
    /// cannot place — a readme covering the whole pack — stays put rather than
    /// being filed under an arbitrary mini.
    /// </remarks>
    private IEnumerable<PlannedMove> PlanSplit(
        ModelEntry model, OrganizeRules rules, Dictionary<string, int> claimed,
        Dictionary<string, ModelEntry> meshOwners)
    {
        var classifier = variants.Current;

        // Every file, not just meshes: a .lys beside its .stl belongs with it.
        var bySculpt = model.Files
            .Select(f => new
            {
                File = f,
                Read = classifier.Classify(
                    model.Name, VariantClassifier.WithinModel(model.RelativePath, f.RelativePath)),
            })
            .GroupBy(x => x.File.SculptKey ?? x.Read.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A sculpt is real when a mesh carries it — here, or anywhere else in
        // the library.
        //
        // The "anywhere else" half matters more than it looks. A slicer project
        // whose mesh was filed on an earlier pass is left behind in a folder
        // that now holds nothing but companions, and a rule that only counted
        // meshes in this folder would call that unusable and strand it for
        // good: the meshes are never coming back to fetch it.
        var real = bySculpt
            .Where(g => g.Any(x => x.File.Kind is FileKind.Mesh or FileKind.Cad)
                     || meshOwners.ContainsKey(g.Key))
            .ToList();

        if (real.Count == 0)
        {
            yield return new PlannedMove(model.Id, model.Name, model.RelativePath, "",
                MoveOutcome.Unusable, "Nothing in this folder reads as a mini to file it under.");
            yield break;
        }

        // Files whose names place them nowhere: a readme, a sidecar describing
        // the folder itself. When the folder holds one mini they are plainly
        // that mini's and follow it, which is what dissolves the folder
        // completely. When it holds many they are the pack's, and filing them
        // under whichever mini sorted first would be a guess, so they stay.
        var placed = real.SelectMany(g => g.Select(x => x.File.Id)).ToHashSet();
        var orphans = real.Count == 1
            ? model.Files.Where(f => !placed.Contains(f.Id)).ToList()
            : [];

        foreach (var group in real.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var name = group.Select(x => x.File.SculptName)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                ?? group.First().Read.DisplayName;

            // Rendered against whoever holds the mesh, which is this model in
            // every ordinary case and somewhere else only for a companion whose
            // mesh has already been filed. Following the mesh's metadata is what
            // puts the two in the same folder rather than merely nearby.
            var owner = group.Any(x => x.File.Kind is FileKind.Mesh or FileKind.Cad)
                ? model
                : meshOwners.GetValueOrDefault(group.Key) ?? model;

            var tokens = TokensFor(owner);
            tokens["sculpt"] = name;
            var destination = PathTemplate.Render(rules.FolderTemplate, tokens, forFile: false);

            if (string.IsNullOrEmpty(destination))
            {
                yield return new PlannedMove(model.Id, name, model.RelativePath, "",
                    MoveOutcome.Unusable, "The template produced an empty path for this mini.")
                {
                    Sculpt = name,
                };
                continue;
            }

            var files = group.Select(x => x.File).Concat(orphans).ToList();
            var already = destination == model.RelativePath && real.Count == 1;

            // Several folders landing here is the point of a split, not a
            // collision: it is how four exports of one mini come together.
            claimed[destination] = model.Id;

            yield return new PlannedMove(
                model.Id, name, model.RelativePath, destination,
                already ? MoveOutcome.AlreadyThere : MoveOutcome.Move,
                Renames: PlanRenames(model, rules, files))
            {
                Sculpt = name,
                FileIds = [.. files.Select(f => f.Id)],
                Fallbacks = FallbacksIn(rules.FolderTemplate, tokens),
            };
        }
    }

    /// <summary>
    /// Marks sidecars that cannot come along because their name is not theirs
    /// alone once several folders land in the same place.
    /// </summary>
    /// <remarks>
    /// Four folders merging into one bring four files called datapackage.json.
    /// They described the folders being dissolved, nothing reads them, and what
    /// they held is already in the database — so they go rather than being
    /// numbered into meaninglessness. Every one is listed in the plan first.
    ///
    /// Only ever on a real collision. A sidecar arriving somewhere nothing else
    /// claims travels with its folder untouched: deleting it would be tidying
    /// up somebody's library uninvited.
    /// </remarks>
    private static List<PlannedMove> MarkColliding(List<PlannedMove> moves, List<ModelEntry> models)
    {
        var byId = models.SelectMany(m => m.Files).ToDictionary(f => f.Id);

        // Names files will land under, which is not their current name when the
        // rules rename as well as move.
        var renamed = moves.SelectMany(m => m.Renames).ToDictionary(r => r.FileId, r => r.To);
        string LandsAs(int id) => renamed.GetValueOrDefault(id, byId[id].FileName);

        // Everything already sitting where something is heading. A file filed on
        // an earlier pass is not in this plan at all, so a collision with it is
        // invisible unless the paths that exist today are looked at too — which
        // is exactly the case that stranded the duplicate grids.
        var occupied = byId.Values.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);

        var deletions = new Dictionary<int, PlannedDeletion>();
        var conflicts = new Dictionary<int, PlannedConflict>();

        var landings = moves
            .Where(m => m.Outcome == MoveOutcome.Move)
            .SelectMany(m => m.FileIds.Where(byId.ContainsKey)
                .Select(id => new { m.To, Id = id, Name = LandsAs(id) }))
            .GroupBy(x => (To: x.To, FileName: x.Name), TupleComparer);

        foreach (var landing in landings)
        {
            var arriving = landing.Select(x => byId[x.Id]).OrderBy(f => f.Id).ToList();

            // Whoever is already there wins the spot, and is not itself moving.
            occupied.TryGetValue($"{landing.Key.To}/{landing.Key.FileName}", out var sitting);
            if (sitting is not null && arriving.Any(f => f.Id == sitting.Id)) sitting = null;

            // Sidecars are the one case where none of them is kept. Four folders
            // merging bring four files called datapackage.json, each describing
            // a folder about to stop existing, and keeping whichever sorted
            // first would leave a file claiming the whole sculpt is one variant.
            // They differ byte for byte, so the copy rule below would call them
            // a clash and strand all four.
            if (SidecarNames.Contains(landing.Key.FileName)
                && arriving.Count + (sitting is null ? 0 : 1) > 1)
            {
                foreach (var sidecar in arriving)
                {
                    deletions[sidecar.Id] = new PlannedDeletion(sidecar.Id, sidecar.RelativePath,
                        "Describes a folder being dissolved, and nothing reads it.", Verify: false);
                }
                continue;
            }

            var keeper = sitting ?? arriving[0];
            var losers = arriving.Where(f => f.Id != keeper.Id).ToList();

            foreach (var loser in losers)
            {
                // Size is free and settles most of it. Two files of different
                // lengths are certainly not the same file, so that is a clash
                // to be told about rather than a copy to drop. Equal lengths
                // are only a candidate — the executor hashes both before it
                // removes anything.
                if (loser.SizeBytes == keeper.SizeBytes)
                {
                    deletions[loser.Id] = new PlannedDeletion(loser.Id, loser.RelativePath,
                        $"Looks like a copy of {keeper.RelativePath}, which is going to the same "
                        + "place. Checked byte for byte before anything is removed.");
                }
                else
                {
                    conflicts[loser.Id] = new PlannedConflict(loser.Id, loser.RelativePath,
                        $"A different file called {landing.Key.FileName} is already going to "
                        + $"{landing.Key.To}. Turn on renaming, or rename one of them, to keep both.");
                }
            }
        }

        if (deletions.Count == 0 && conflicts.Count == 0) return moves;

        return [.. moves.Select(m =>
            m.FileIds.Any(id => deletions.ContainsKey(id) || conflicts.ContainsKey(id))
                ? m with
                {
                    Deletions = [.. m.FileIds.Where(deletions.ContainsKey).Select(id => deletions[id])],
                    Conflicts = [.. m.FileIds.Where(conflicts.ContainsKey).Select(id => conflicts[id])],
                }
                : m)];
    }

    private static readonly HashSet<string> SidecarNames =
        new(StringComparer.OrdinalIgnoreCase) { "datapackage.json" };

    private static readonly IEqualityComparer<(string To, string FileName)> TupleComparer =
        new DestinationNameComparer();

    private sealed class DestinationNameComparer : IEqualityComparer<(string To, string FileName)>
    {
        public bool Equals((string To, string FileName) a, (string To, string FileName) b) =>
            string.Equals(a.To, b.To, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string To, string FileName) x) =>
            HashCode.Combine(x.To.ToLowerInvariant(), x.FileName.ToLowerInvariant());
    }

    private static Dictionary<string, string?> TokensFor(ModelEntry model) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["model"] = model.Name,
        ["designer"] = model.Designer?.Name,
        ["source"] = model.SourceSite,
        ["collection"] = model.Collections.OrderBy(c => c.Name).FirstOrDefault()?.Name,
        ["tag"] = model.Tags.OrderBy(t => t.Name).FirstOrDefault()?.Name,
        ["year"] = model.AddedUtc.Year.ToString(),
        ["license"] = model.License,
    };
}
