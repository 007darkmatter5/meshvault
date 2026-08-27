using MeshVault.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeshVault.Data;

/// <summary>What actually happened when a plan was applied.</summary>
public record OrganizeResult(
    int FilesMoved,
    int FoldersCreated,
    int FilesDeleted,
    int ModelsCreated,
    int ModelsRemoved,
    IReadOnlyList<string> Problems)
{
    public bool Clean => Problems.Count == 0;
}

/// <summary>Progress while a plan runs, for a page that has to keep rendering.</summary>
public record OrganizeProgress(int Done, int Total, string? Current);

/// <summary>
/// Carries out an <see cref="OrganizePlan"/>.
/// </summary>
/// <remarks>
/// The only thing in MeshVault that writes into somebody's library, so it is
/// deliberately narrow about what it will do.
///
/// It moves files rather than folders. A folder move is one call that either
/// works or does not, and when it does not it can leave half a set behind with
/// nothing recorded; file by file, every step is either done and written down
/// or not attempted.
///
/// It rewrites the database alongside the disk, per destination. Leaving that
/// to the next scan would be a disaster: <see cref="LibraryIndexer"/> reconciles
/// on <see cref="ModelEntry.RelativePath"/>, so a folder that moved reads as one
/// model deleted and another added — taking that model's tags, notes,
/// collections, favourites and grouping with it.
/// </remarks>
public class OrganizeExecutor(
    IDbContextFactory<MeshVaultDbContext> factory,
    ILogger<OrganizeExecutor> log)
{
    public async Task<OrganizeResult> ApplyAsync(
        int libraryId,
        OrganizePlan plan,
        IProgress<OrganizeProgress>? progress = null,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var library = await db.Libraries.FirstOrDefaultAsync(l => l.Id == libraryId, ct)
            ?? throw new InvalidOperationException($"Library {libraryId} not found.");

        // The switch on the library is the gate, checked here rather than only
        // in the page: this class must be safe to call from anywhere.
        if (!library.AllowOrganize)
            throw new InvalidOperationException(
                $"{library.Name} does not allow MeshVault to move files. Turn that on first.");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(library.Path));
        if (!Directory.Exists(root))
            throw new InvalidOperationException($"{library.Name} is not reachable at {root}.");

        var problems = new List<string>();
        int moved = 0, created = 0, deleted = 0, added = 0, removed = 0;

        // One unit of work per destination folder, because that is what a
        // ModelEntry will stand for. A pack splitting into ninety-eight and four
        // folders merging into one are the same shape seen from here.
        var byDestination = plan.Moves
            .Where(m => m.Outcome == MoveOutcome.Move && m.To.Length > 0)
            .GroupBy(m => m.To, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var toDelete = plan.Deletions.ToDictionary(d => d.FileId);
        var toSkip = plan.Conflicts.Select(c => c.FileId).ToHashSet();
        var touchedSources = new HashSet<int>();
        var sourceFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Counted in files rather than folders. A folder holding one mesh and a
        // folder holding two hundred take wildly different times, so a bar that
        // steps per folder sits still and then jumps, which reads as finished.
        var total = plan.Moves
            .Where(m => m.Outcome == MoveOutcome.Move)
            .Sum(m => m.FileIds.Count > 0 ? m.FileIds.Count : 1);
        var done = 0;

        foreach (var group in byDestination)
        {
            ct.ThrowIfCancellationRequested();

            var destination = Combine(root, group.Key);
            if (destination is null)
            {
                problems.Add($"{group.Key} would land outside the library, so it was skipped.");
                continue;
            }

            var sourceIds = group.Select(m => m.ModelId).Distinct().ToList();

            var sources = await db.Models
                .Include(m => m.Files)
                .Include(m => m.Tags)
                .Include(m => m.Collections)
                .Where(m => sourceIds.Contains(m.Id))
                .ToListAsync(ct);

            if (sources.Count == 0) continue;

            // A move that names no files is the whole folder going as one, which
            // is what every plan looked like before splitting existed.
            var fileIds = group
                .SelectMany(m => m.FileIds.Count > 0
                    ? m.FileIds
                    : sources.FirstOrDefault(s => s.Id == m.ModelId)?.Files.Select(f => f.Id) ?? [])
                .Distinct()
                .ToList();
            foreach (var source in sources)
            {
                touchedSources.Add(source.Id);
                sourceFolders.Add(source.RelativePath);
            }

            try
            {
                if (!Directory.Exists(destination))
                {
                    Directory.CreateDirectory(destination);
                    created++;
                }
            }
            catch (Exception ex)
            {
                problems.Add($"Could not create {group.Key}: {ex.Message}");
                continue;
            }

            var owner = await OwnerForAsync(db, library.Id, group.Key, sources, fileIds, ct);
            if (owner.IsNew) added++;

            var renames = group.SelectMany(m => m.Renames).ToDictionary(r => r.FileId, r => r.To);

            foreach (var file in sources.SelectMany(m => m.Files).Where(f => fileIds.Contains(f.Id)))
            {
                ct.ThrowIfCancellationRequested();

                done++;
                progress?.Report(new OrganizeProgress(done, total, $"{group.Key}/{file.FileName}"));

                var from = Combine(root, file.RelativePath);
                if (from is null || !File.Exists(from))
                {
                    problems.Add($"{file.RelativePath} is no longer there, so it was left out.");
                    continue;
                }

                if (toSkip.Contains(file.Id))
                {
                    problems.Add(
                        $"{file.RelativePath} stayed put: something different already claims that name.");
                    continue;
                }

                var name = renames.GetValueOrDefault(file.Id, file.FileName);
                var to = Path.Combine(destination, name);

                if (toDelete.TryGetValue(file.Id, out var deletion))
                {
                    try
                    {
                        // A suspected copy was picked out by length alone, which
                        // is a good guess and not good enough to delete on. Both
                        // are read through first, and one that turns out to
                        // differ is left exactly where it is.
                        if (deletion.Verify)
                        {
                            if (!File.Exists(to))
                            {
                                problems.Add(
                                    $"{file.RelativePath} looked like a copy, but nothing is at "
                                    + $"{group.Key}/{name} to compare it with, so it was left alone.");
                                continue;
                            }

                            // Said out loud, and the count held where it is. A
                            // pair of large files is minutes of reading, and a
                            // bar that had already stepped on looked hung for
                            // all of it.
                            progress?.Report(new OrganizeProgress(done - 1, total,
                                $"Checking {file.FileName} is a copy "
                                + $"({file.SizeBytes / 1048576:N0} MB, twice)"));

                            var mine = await HashAsync(db, file, from, ct);
                            var theirs = await HashAsync(
                                db, await FileAtAsync(db, library.Id, $"{group.Key}/{name}", ct), to, ct);

                            if (mine != theirs)
                            {
                                problems.Add(
                                    $"{file.RelativePath} is not the same file as {group.Key}/{name} "
                                    + "after all, so it was left alone. Rename one to keep both.");
                                continue;
                            }
                        }

                        File.Delete(from);
                        db.Files.Remove(file);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        problems.Add($"Could not remove {file.RelativePath}: {ex.Message}");
                    }
                    continue;
                }

                // Never overwrite. Two files that would land on the same name is
                // a planning mistake, and silently losing one is far worse than
                // saying so and leaving both where they can be found.
                if (File.Exists(to) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{group.Key}/{name} already exists, so {file.FileName} stayed put.");
                    continue;
                }

                try
                {
                    if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                        File.Move(from, to);

                    file.RelativePath = $"{group.Key}/{name}";
                    file.FileName = name;
                    file.ModelEntryId = owner.Model.Id;
                    moved++;
                }
                catch (Exception ex)
                {
                    problems.Add($"Could not move {file.RelativePath}: {ex.Message}");
                }
            }

            owner.Model.RelativePath = group.Key;

            // Saved per destination rather than once at the end. A failure part
            // way through then leaves everything before it both moved and
            // recorded, instead of a library full of files the database has
            // never heard of.
            await db.SaveChangesAsync(ct);
        }

        added += await RehomeStrandedAsync(db, library.Id, ct);
        removed = await RemoveEmptiedAsync(db, touchedSources, ct);
        await RefreshTotalsAsync(db, ct);
        PruneEmptyFolders(root, sourceFolders, problems);

        library.LastScannedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        progress?.Report(new OrganizeProgress(total, total, null));

        log.LogInformation(
            "Organized {Library}: {Moved} file(s) moved, {Created} folder(s), {Deleted} deleted, "
            + "{Added} model(s) added, {Removed} removed, {Problems} problem(s)",
            library.Name, moved, created, deleted, added, removed, problems.Count);

        // Written out in full, not just counted. The result is held in memory by
        // whoever started the run and is gone the moment they navigate away, so
        // a bare "16 problem(s)" leaves nothing behind to work out what happened
        // from — the log is the only lasting record of what did not move.
        foreach (var problem in problems)
            log.LogWarning("Organizing {Library}: {Problem}", library.Name, problem);

        return new OrganizeResult(moved, created, deleted, added, removed, problems);
    }

    private record Owner(ModelEntry Model, bool IsNew);

    /// <summary>
    /// The model that will own a destination folder.
    /// </summary>
    /// <remarks>
    /// Reuses a source wherever it can, because a reused row keeps its id and so
    /// its notes, favourites and snapshot. A source qualifies when everything it
    /// has is going to this one place — otherwise it is splitting, and the parts
    /// that leave need rows of their own.
    /// </remarks>
    private static async Task<Owner> OwnerForAsync(
        MeshVaultDbContext db, int libraryId, string destination,
        List<ModelEntry> sources, List<int> fileIds, CancellationToken ct)
    {
        var existing = await db.Models
            .Include(m => m.Tags)
            .FirstOrDefaultAsync(m => m.LibraryId == libraryId && m.RelativePath == destination, ct);

        if (existing is not null) return new Owner(existing, false);

        var reusable = sources
            .Where(s => s.Files.All(f => fileIds.Contains(f.Id)))
            .OrderBy(s => s.Id)
            .FirstOrDefault();

        if (reusable is not null)
        {
            MergeInto(reusable, sources);
            return new Owner(reusable, false);
        }

        // A split: this mini needs a row, and inherits how the pack was
        // described. Whoever tagged the pack meant it for everything in it.
        var donor = sources[0];
        var model = new ModelEntry
        {
            LibraryId = libraryId,
            RelativePath = destination,
            Name = destination.Split('/')[^1],
            DesignerId = donor.DesignerId,
            SourceUrl = donor.SourceUrl,
            SourceSite = donor.SourceSite,
            License = donor.License,
            AddedUtc = DateTimeOffset.UtcNow,
            Tags = [.. donor.Tags],
            Collections = [.. donor.Collections],
        };

        db.Models.Add(model);
        await db.SaveChangesAsync(ct);
        return new Owner(model, true);
    }

    /// <summary>
    /// Folds what the other sources knew into the row that survives a merge, so
    /// four folders becoming one does not quietly drop three sets of tags.
    /// </summary>
    private static void MergeInto(ModelEntry keeper, List<ModelEntry> sources)
    {
        foreach (var other in sources.Where(s => s.Id != keeper.Id))
        {
            foreach (var tag in other.Tags.Where(t => keeper.Tags.All(k => k.Id != t.Id)))
                keeper.Tags.Add(tag);

            foreach (var collection in other.Collections
                         .Where(c => keeper.Collections.All(k => k.Id != c.Id)))
                keeper.Collections.Add(collection);

            keeper.DesignerId ??= other.DesignerId;
            keeper.SourceUrl ??= other.SourceUrl;
            keeper.SourceSite ??= other.SourceSite;
            keeper.License ??= other.License;
            if (string.IsNullOrWhiteSpace(keeper.Notes)) keeper.Notes = other.Notes;
        }
    }

    /// <summary>
    /// Gives a home to files left outside the folder their model moved to.
    /// </summary>
    /// <remarks>
    /// A model's path moves to the destination whether or not every one of its
    /// files got there — a file blocked by a name clash stays behind while the
    /// row that owns it walks off. The model then claims a folder it is not
    /// wholly in, and the next scan indexes the leftovers as a brand new model,
    /// taking none of the tags with them.
    ///
    /// Rather than refuse to move the model for one straggler, the stragglers
    /// are given a row of their own at the folder they are actually in,
    /// inheriting how the model they came from was described. That is what a
    /// scan would produce, arrived at without losing anything.
    /// </remarks>
    private static async Task<int> RehomeStrandedAsync(
        MeshVaultDbContext db, int libraryId, CancellationToken ct)
    {
        var models = await db.Models
            .Include(m => m.Files)
            .Include(m => m.Tags)
            .Include(m => m.Collections)
            .Where(m => m.LibraryId == libraryId)
            .ToListAsync(ct);

        var byPath = models.ToDictionary(m => m.RelativePath, StringComparer.OrdinalIgnoreCase);
        var created = 0;

        foreach (var model in models.ToList())
        {
            var stranded = model.Files
                .Where(f => !f.RelativePath.StartsWith(model.RelativePath + "/", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var group in stranded.GroupBy(Parent, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Key.Length == 0) continue;

                if (!byPath.TryGetValue(group.Key, out var home))
                {
                    home = new ModelEntry
                    {
                        LibraryId = libraryId,
                        RelativePath = group.Key,
                        Name = group.Key.Split('/')[^1],
                        DesignerId = model.DesignerId,
                        SourceUrl = model.SourceUrl,
                        SourceSite = model.SourceSite,
                        License = model.License,
                        AddedUtc = DateTimeOffset.UtcNow,
                        Tags = [.. model.Tags],
                        Collections = [.. model.Collections],
                    };

                    db.Models.Add(home);
                    byPath[group.Key] = home;
                    created++;
                }

                if (home.Id == model.Id) continue;

                foreach (var file in group)
                {
                    model.Files.Remove(file);
                    home.Files.Add(file);
                }
            }
        }

        if (created > 0 || db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>The folder a library-relative file path sits in.</summary>
    private static string Parent(ModelFile file)
    {
        var cut = file.RelativePath.LastIndexOf('/');
        return cut <= 0 ? "" : file.RelativePath[..cut];
    }

    /// <summary>Drops source models nothing is left in.</summary>
    private static async Task<int> RemoveEmptiedAsync(
        MeshVaultDbContext db, HashSet<int> touched, CancellationToken ct)
    {
        if (touched.Count == 0) return 0;

        var empty = await db.Models
            .Where(m => touched.Contains(m.Id) && !m.Files.Any())
            .ToListAsync(ct);

        if (empty.Count == 0) return 0;

        db.Models.RemoveRange(empty);
        await db.SaveChangesAsync(ct);
        return empty.Count;
    }

    /// <summary>Brings each surviving model's size and date back in line with its files.</summary>
    private static async Task RefreshTotalsAsync(MeshVaultDbContext db, CancellationToken ct)
    {
        foreach (var model in db.ChangeTracker.Entries<ModelEntry>()
                     .Select(e => e.Entity)
                     .Where(m => m.Files.Count > 0))
        {
            model.TotalBytes = model.Files.Sum(f => f.SizeBytes);
            model.FileModifiedUtc = model.Files.Max(f => f.ModifiedUtc);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes source folders that are now empty, deepest first.
    /// </summary>
    /// <remarks>
    /// Only ever folders the plan emptied, and only when nothing at all is left
    /// in them — a stray file somebody put there by hand keeps its folder alive
    /// rather than being swept up with it.
    /// </remarks>
    private static void PruneEmptyFolders(
        string root, HashSet<string> folders, List<string> problems)
    {
        foreach (var relative in folders.OrderByDescending(f => f.Count(c => c == '/')).ThenByDescending(f => f))
        {
            var full = Combine(root, relative);
            if (full is null || !Directory.Exists(full)) continue;

            try
            {
                // Walk up while each level is empty: splitting a nested pack
                // leaves a chain of husks, not one.
                var directory = new DirectoryInfo(full);
                while (directory is not null
                       && directory.FullName.Length > root.Length
                       && !directory.EnumerateFileSystemInfos().Any())
                {
                    var parent = directory.Parent;
                    directory.Delete();
                    directory = parent;
                }
            }
            catch (Exception ex)
            {
                problems.Add($"Could not tidy away {relative}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Content hash of a file, remembered on the row it belongs to.
    /// </summary>
    /// <remarks>
    /// Only ever reached at a collision. Hashing a whole library would mean
    /// reading every byte of it, which on a share measured at 1.4 MB/s is hours;
    /// hashing the two files actually in dispute is bounded and worth it, since
    /// the alternative is asking someone to compare them by hand.
    ///
    /// Bounded is still not cheap — a pair of 117 MB grids is three minutes of
    /// reading — so the answer is kept. <see cref="LibraryIndexer"/> already
    /// clears it when a file's bytes move, so a stored hash is either current or
    /// absent, never stale.
    /// </remarks>
    private static async Task<string> HashAsync(
        MeshVaultDbContext db, ModelFile? file, string path, CancellationToken ct)
    {
        if (file?.Sha256 is { Length: > 0 } known) return known;

        var hash = await Task.Run(() =>
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }, ct);

        if (file is not null) file.Sha256 = hash;
        return hash;
    }

    /// <summary>
    /// The indexed row for a path, so a hash worked out for the file already at
    /// a destination is kept as well as the one arriving.
    /// </summary>
    /// <remarks>
    /// The change tracker is asked first, and it has to be. Files move and are
    /// saved a destination at a time, so a file that arrived moments ago in this
    /// very group has its new path in memory and its old one in the database — a
    /// query would miss it, and the hash it just paid for would be thrown away.
    /// </remarks>
    private static async Task<ModelFile?> FileAtAsync(
        MeshVaultDbContext db, int libraryId, string relativePath, CancellationToken ct)
    {
        var tracked = db.ChangeTracker.Entries<ModelFile>()
            .Select(e => e.Entity)
            .FirstOrDefault(f => string.Equals(
                f.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));

        return tracked ?? await db.Files.FirstOrDefaultAsync(
            f => f.ModelEntry!.LibraryId == libraryId && f.RelativePath == relativePath, ct);
    }

    /// <summary>
    /// A full path for a library-relative one, or null when it would escape the
    /// library. The last line of defence against a template or a name that
    /// climbs out with "..".
    /// </summary>
    private static string? Combine(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
    }
}
