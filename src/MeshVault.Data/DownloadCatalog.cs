using MeshVault.Core.Models;
using MeshVault.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Data;

/// <summary>One file on its way out, and where it lands inside the archive.</summary>
public record DownloadItem(string FullPath, string EntryPath, long SizeBytes);

/// <summary>
/// Everything a download covers, resolved to real paths before a single byte
/// is written.
/// </summary>
/// <remarks>
/// Resolved up front on purpose. A zip is streamed straight to the response, so
/// the status code is gone the moment the first byte leaves: "there is nothing
/// here" has to be answerable while a 404 is still possible.
/// </remarks>
public record DownloadSet(string Name, IReadOnlyList<DownloadItem> Items)
{
    public long TotalBytes => Items.Sum(i => i.SizeBytes);
}

/// <summary>
/// What a download would cost, without resolving every path. Read from the
/// counts the index already keeps, so a confirmation dialog does not touch the
/// library share.
/// </summary>
public record DownloadSize(string Name, int Models, int Files, long TotalBytes);

/// <summary>
/// Works out which files a download covers and where each one sits inside the
/// archive. Read-side only: nothing here opens a file.
/// </summary>
public class DownloadCatalog(IDbContextFactory<MeshVaultDbContext> factory, ICurrentUser user)
{
    /// <summary>A single file, or null if it is gone or would escape its library.</summary>
    public async Task<DownloadItem?> GetFileAsync(int fileId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var file = await db.Files.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new
            {
                f.RelativePath,
                f.FileName,
                f.SizeBytes,
                LibraryPath = f.ModelEntry!.Library!.Path,
            })
            .FirstOrDefaultAsync(ct);

        if (file is null) return null;

        var full = ResolveWithin(file.LibraryPath, file.RelativePath);
        return full is null ? null : new DownloadItem(full, file.FileName, file.SizeBytes);
    }

    /// <summary>
    /// Every file of a model, or of its whole group when it has one.
    /// </summary>
    /// <remarks>
    /// Spanning the group is not generosity, it is honesty: the detail page
    /// already shows a grouped model's four folders as one thing, so a Download
    /// button there that fetched only the folder you happened to arrive at would
    /// hand back less than the page is showing.
    /// </remarks>
    public async Task<DownloadSet?> GetModelAsync(int modelId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var found = await LoadWithFilesAsync(db, m => m.Id == modelId, ct);
        if (found.Count == 0) return null;

        var subject = found[0];
        var members = await ExpandGroupAsync(db, found, ct);
        var name = members.Count > 1 ? subject.GroupName ?? subject.Name : subject.Name;

        return new DownloadSet(name, Gather(members, _ => true));
    }

    /// <summary>
    /// Every export of one sculpt held by a model, or by its group.
    /// </summary>
    /// <remarks>
    /// The reason to have this at all: a pack of ninety-eight minis is one
    /// model, and wanting to print one of them is the ordinary case.
    ///
    /// Matched on <see cref="ModelFile.SculptKey"/> rather than through
    /// <see cref="VariantGrouper"/>. The grouper falls back to a file's path for
    /// anything indexed before variants existed, and those files head no sculpt
    /// on the page — reading the stored key instead means the headings you can
    /// download are exactly the headings that say a sculpt's name, meshes and
    /// CAD alike.
    /// </remarks>
    public async Task<DownloadSet?> GetSculptAsync(
        int modelId, string sculptKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sculptKey)) return null;

        await using var db = await factory.CreateDbContextAsync(ct);

        var found = await LoadWithFilesAsync(db, m => m.Id == modelId, ct);
        if (found.Count == 0) return null;

        var members = await ExpandGroupAsync(db, found, ct);

        bool IsWanted(ModelFile f) =>
            string.Equals(f.SculptKey, sculptKey, StringComparison.OrdinalIgnoreCase);

        var matched = members.SelectMany(m => m.Files).Where(IsWanted).ToList();
        if (matched.Count == 0) return null;

        // The best-ranked export carries the spelling worth showing; the key is
        // lowercased, so using it as the archive's name would hand back
        // "ud 067 hole trap".
        var name = matched
            .OrderBy(f => f.VariantRank)
            .Select(f => f.SculptName)
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? sculptKey;

        return new DownloadSet(name, Gather(members, IsWanted));
    }

    /// <summary>
    /// Every file of every model in one of the current user's collections.
    /// Null when the collection does not exist or belongs to somebody else.
    /// </summary>
    public async Task<DownloadSet?> GetCollectionAsync(
        int collectionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var name = await OwnedCollectionNameAsync(db, collectionId, ct);
        if (name is null) return null;

        var models = await LoadWithFilesAsync(db, m => m.Collections.Any(c => c.Id == collectionId), ct);
        var members = await ExpandGroupAsync(db, models, ct);

        return new DownloadSet(name, Gather(members, _ => true, foldPerModel: true));
    }

    /// <summary>
    /// How big a collection download would be. Counted from the index rather
    /// than from disk: the share is slow enough that stat-ing every file to fill
    /// in a dialog would be felt.
    /// </summary>
    public async Task<DownloadSize?> GetCollectionSizeAsync(
        int collectionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var name = await OwnedCollectionNameAsync(db, collectionId, ct);
        if (name is null) return null;

        var chosen = await db.Models.AsNoTracking()
            .Where(m => m.Collections.Any(c => c.Id == collectionId))
            .Select(m => new { m.Id, m.LibraryId, m.GroupKey })
            .ToListAsync(ct);

        var ids = chosen.Select(m => m.Id).ToHashSet();

        // The same expansion the download itself does, in one query for the same
        // reason, or the dialog would promise a smaller archive than arrives.
        var wanted = chosen
            .Where(m => m.GroupKey is not null)
            .Select(m => (m.LibraryId, m.GroupKey))
            .ToHashSet();

        if (wanted.Count > 0)
        {
            var keys = wanted.Select(w => w.GroupKey).Distinct().ToList();

            var siblings = await db.Models.AsNoTracking()
                .Where(m => m.GroupKey != null && keys.Contains(m.GroupKey))
                .Select(m => new { m.Id, m.LibraryId, m.GroupKey })
                .ToListAsync(ct);

            // A group key is only unique within its library.
            ids.UnionWith(siblings
                .Where(s => wanted.Contains((s.LibraryId, s.GroupKey)))
                .Select(s => s.Id));
        }

        var bytes = await db.Models.AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .SumAsync(m => m.TotalBytes, ct);

        var files = await db.Files.AsNoTracking()
            .CountAsync(f => ids.Contains(f.ModelEntryId), ct);

        return new DownloadSize(name, ids.Count, files, bytes);
    }

    private async Task<string?> OwnedCollectionNameAsync(
        MeshVaultDbContext db, int collectionId, CancellationToken ct) =>
        await db.Collections.AsNoTracking()
            .Where(c => c.Id == collectionId && c.OwnerId == user.UserId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(ct);

    private static async Task<List<ModelEntry>> LoadWithFilesAsync(
        MeshVaultDbContext db,
        System.Linq.Expressions.Expression<Func<ModelEntry, bool>> where,
        CancellationToken ct) =>
        await db.Models.AsNoTracking()
            .Include(m => m.Files)
            .Include(m => m.Library)
            .Where(where)
            .ToListAsync(ct);

    /// <summary>
    /// Adds the rest of each model's group. A grouped model is shown as one
    /// thing everywhere else, and Browse lists only the primary, so a collection
    /// holding the primary means the sculpt rather than that one folder.
    /// </summary>
    private static async Task<List<ModelEntry>> ExpandGroupAsync(
        MeshVaultDbContext db, List<ModelEntry> models, CancellationToken ct)
    {
        var wanted = models
            .Where(m => m.GroupKey is not null)
            .Select(m => (m.LibraryId, m.GroupKey))
            .ToHashSet();

        if (wanted.Count == 0) return models;

        // One query for every group at once. A collection of a hundred grouped
        // models asking a hundred times over is the sort of thing that is free
        // on a local SQLite file right up until it is not.
        var keys = wanted.Select(w => w.GroupKey).Distinct().ToList();

        var siblings = await db.Models.AsNoTracking()
            .Include(m => m.Files)
            .Include(m => m.Library)
            .Where(m => m.GroupKey != null && keys.Contains(m.GroupKey))
            .ToListAsync(ct);

        var byId = models.ToDictionary(m => m.Id);

        foreach (var sibling in siblings)
        {
            // A group key is only unique within its library, so the pairing has
            // to be checked rather than the key alone.
            if (wanted.Contains((sibling.LibraryId, sibling.GroupKey)))
                byId.TryAdd(sibling.Id, sibling);
        }

        return [.. byId.Values
            .OrderBy(m => m.Files.Count == 0 ? int.MaxValue : m.Files.Min(f => f.VariantRank))
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Turns models into archive entries. Files keep their layout beneath the
    /// model folder; more than one model means each gets a folder of its own,
    /// named after the model rather than after its path, so unzipping a
    /// collection gives a shelf of models rather than a copy of the library's
    /// tree with one folder filled in at each depth.
    /// </summary>
    private static List<DownloadItem> Gather(
        List<ModelEntry> models, Func<ModelFile, bool> wanted, bool foldPerModel = false)
    {
        var prefixed = foldPerModel || models.Count > 1;
        var items = new List<DownloadItem>();

        foreach (var model in models)
        {
            var libraryPath = model.Library?.Path;
            if (string.IsNullOrEmpty(libraryPath)) continue;

            var prefix = prefixed ? FolderNameFor(model) : "";

            foreach (var file in model.Files.Where(wanted))
            {
                var full = ResolveWithin(libraryPath, file.RelativePath);
                if (full is null) continue;

                var inside = EntryPathFor(model, file);
                items.Add(new DownloadItem(
                    full, prefix.Length == 0 ? inside : prefix + "/" + inside, file.SizeBytes));
            }
        }

        return Deduplicate(items);
    }

    /// <summary>The model's name, made safe to be a folder inside the archive.</summary>
    private static string FolderNameFor(ModelEntry model)
    {
        var name = PathTemplate.Sanitize(model.Name);
        if (name.Length > 0) return name;

        name = PathTemplate.Sanitize(model.RelativePath.Split('/').LastOrDefault() ?? "");
        return name.Length > 0 ? name : "model-" + model.Id;
    }

    /// <summary>
    /// Where a file sits relative to its model's folder, so subfolders survive
    /// the round trip.
    /// </summary>
    private static string EntryPathFor(ModelEntry model, ModelFile file)
    {
        var folder = model.RelativePath;

        if (folder.Length > 0
            && file.RelativePath.Length > folder.Length + 1
            && file.RelativePath.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
            return file.RelativePath[(folder.Length + 1)..];

        // RehomeStrandedAsync keeps files inside their model's folder, so this
        // is the odd one out rather than the rule. Flattening to the bare name
        // beats writing an entry that climbs out of the archive.
        return file.FileName;
    }

    /// <summary>
    /// Numbers any entry path claimed twice. Zip allows duplicates and most
    /// tools extract them over each other, quietly handing back fewer files than
    /// were asked for.
    /// </summary>
    private static List<DownloadItem> Deduplicate(List<DownloadItem> items)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DownloadItem>(items.Count);

        foreach (var item in items)
        {
            var path = item.EntryPath;

            if (!taken.Add(path))
            {
                // The extension is a dot in the *last* segment. Splitting on the
                // last dot anywhere turns "Otto v1.2/otto.stl" into a stem of
                // "Otto v1" and an extension of ".2/otto.stl", and the numbered
                // copy lands in a folder nobody named.
                var slash = path.LastIndexOf('/');
                var dot = path.LastIndexOf('.');
                var extended = dot > slash + 1;

                var stem = extended ? path[..dot] : path;
                var extension = extended ? path[dot..] : "";

                var n = 2;
                do { path = stem + " (" + n++ + ")" + extension; } while (!taken.Add(path));
            }

            result.Add(item with { EntryPath = path });
        }

        return result;
    }

    /// <summary>
    /// The file's real path, or null when the stored relative path would climb
    /// out of the library.
    /// </summary>
    /// <remarks>
    /// Belt and braces. These paths come from the scanner rather than from a
    /// request, but this is the one place the app hands raw file bytes to a
    /// browser, and a single bad row would otherwise be a read of anything the
    /// process can open.
    /// </remarks>
    public static string? ResolveWithin(string libraryPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(libraryPath) || string.IsNullOrWhiteSpace(relativePath))
            return null;

        var root = Path.GetFullPath(libraryPath);
        var full = Path.GetFullPath(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        var relative = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relative) || relative == ".") return null;

        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => s == "..") ? null : full;
    }
}
