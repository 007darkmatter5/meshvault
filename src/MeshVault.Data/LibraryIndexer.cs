using MeshVault.Core.Models;
using MeshVault.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeshVault.Data;

public record IndexResult(int Added, int Updated, int Removed);

/// <summary>Progress of an in-flight scan, for display while it runs.</summary>
public record ScanProgress(int ModelsSeen, int FilesSeen, string? CurrentFolder);

/// <summary>
/// Reconciles what is on disk with what is in the database. Reconciling rather
/// than rebuilding is what keeps user-owned data — tags, notes, favourites —
/// attached to a model across rescans.
/// </summary>
public class LibraryIndexer(
    MeshVaultDbContext db,
    FolderScanner scanner,
    VariantClassifier variants,
    ILogger<LibraryIndexer> log)
{
    /// <summary>How often progress is reported. Walking a network share can run for
    /// minutes, and reporting per model would flood the UI.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>Reconciles the whole library with what is on disk.</summary>
    public Task<IndexResult> IndexAsync(
        int libraryId,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default) =>
        IndexCoreAsync(libraryId, null, progress, ct);

    /// <summary>
    /// Reconciles one folder inside the library â the inbox, in practice â
    /// leaving everything outside it untouched.
    /// </summary>
    /// <remarks>
    /// Noticing three new files should not cost a walk of the whole share. The
    /// inbox is small and is where new things land, so scanning just that is
    /// the difference between seconds and minutes.
    ///
    /// <b>The removal set is narrowed to the same folder, and that is the whole
    /// safety of this method.</b> A full scan deletes every model it did not
    /// see, which is how a folder deleted on the share is noticed. Run that
    /// reasoning after looking at the inbox alone and it would delete the
    /// entire library, instantly and without asking. Within the folder,
    /// removal still works as it should: delete something from the inbox, scan
    /// the inbox, and it goes.
    /// </remarks>
    public Task<IndexResult> IndexFolderAsync(
        int libraryId,
        string subPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default) =>
        IndexCoreAsync(libraryId, subPath, progress, ct);

    private async Task<IndexResult> IndexCoreAsync(
        int libraryId,
        string? subPath,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var library = await db.Libraries.FirstOrDefaultAsync(l => l.Id == libraryId, ct)
            ?? throw new InvalidOperationException($"Library {libraryId} not found.");

        // Only the models inside the folder being scanned. This set is also
        // what gets pruned at the end, so narrowing it here is what stops an
        // inbox scan reading the rest of the library as deleted.
        var scope = Inbox.Normalize(subPath);

        var models = db.Models.Where(m => m.LibraryId == libraryId);
        if (scope.Length > 0)
        {
            models = models.Where(m =>
                m.RelativePath == scope || m.RelativePath.StartsWith(scope + "/"));
        }

        var existing = await models
            .Include(m => m.Files)
            .ToDictionaryAsync(m => m.RelativePath, ct);

        var seen = new HashSet<string>();
        int added = 0, updated = 0, filesSeen = 0;
        var now = DateTimeOffset.UtcNow;
        var nextReport = DateTimeOffset.UtcNow + ProgressInterval;

        foreach (var scanned in scanner.Scan(library.Path, subPath, ct))
        {
            ct.ThrowIfCancellationRequested();
            seen.Add(scanned.RelativePath);
            filesSeen += scanned.Files.Count;

            if (progress is not null && DateTimeOffset.UtcNow >= nextReport)
            {
                progress.Report(new ScanProgress(seen.Count, filesSeen, scanned.RelativePath));
                nextReport = DateTimeOffset.UtcNow + ProgressInterval;
            }

            var isNew = !existing.TryGetValue(scanned.RelativePath, out var entry);
            if (isNew)
            {
                entry = new ModelEntry
                {
                    LibraryId = libraryId,
                    RelativePath = scanned.RelativePath,
                    Name = scanned.Name,
                    AddedUtc = now,
                };
                db.Models.Add(entry);
            }

            var filesChanged = MergeFiles(entry!, scanned);
            if (isNew) added++;
            else if (filesChanged) updated++;
            else continue;

            entry!.TotalBytes = scanned.TotalBytes;
            entry.FileModifiedUtc = scanned.ModifiedUtc;
        }

        var removed = existing.Where(kv => !seen.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        db.Models.RemoveRange(removed);

        // Only a full pass counts as "the library was scanned". Stamping this
        // after looking at the inbox would tell the startup scan the share had
        // been walked, and the real rescan would be skipped for the next
        // RescanIntervalHours.
        if (scope.Length == 0) library.LastScannedUtc = now;

        // Saving a whole network share's worth of changes takes a moment; say so
        // rather than appearing to stall at the last reported count.
        progress?.Report(new ScanProgress(seen.Count, filesSeen, "Saving..."));
        await db.SaveChangesAsync(ct);

        log.LogInformation("Indexed {Library}: +{Added} ~{Updated} -{Removed}",
            library.Name, added, updated, removed.Count);
        return new IndexResult(added, updated, removed.Count);
    }

    /// <summary>Returns true when anything about the model's files changed.</summary>
    private bool MergeFiles(ModelEntry entry, ScannedModel scanned)
    {
        var changed = false;
        var byPath = entry.Files.ToDictionary(f => f.RelativePath);

        foreach (var sf in scanned.Files)
        {
            if (byPath.TryGetValue(sf.RelativePath, out var file))
            {
                // Reading a name costs nothing, so it is redone every scan. That
                // is what carries an edit to the variant rules through to files
                // whose bytes have not moved.
                variants.Apply(entry, file);

                if (file.SizeBytes == sf.SizeBytes && file.ModifiedUtc == sf.ModifiedUtc)
                    continue;

                file.SizeBytes = sf.SizeBytes;
                file.ModifiedUtc = sf.ModifiedUtc;
                // Contents moved, so any derived data is stale.
                file.Sha256 = null;
                file.TriangleCount = null;
                file.ThumbnailState = InitialThumbnailState(sf.Extension);
                changed = true;
            }
            else
            {
                var added = new ModelFile
                {
                    RelativePath = sf.RelativePath,
                    FileName = sf.FileName,
                    Extension = sf.Extension,
                    Kind = sf.Kind,
                    SizeBytes = sf.SizeBytes,
                    ModifiedUtc = sf.ModifiedUtc,
                    ThumbnailState = InitialThumbnailState(sf.Extension),
                };
                variants.Apply(entry, added);
                entry.Files.Add(added);
                changed = true;
            }
        }

        var gone = byPath.Keys.Except(scanned.Files.Select(f => f.RelativePath)).ToList();
        foreach (var path in gone)
        {
            entry.Files.Remove(byPath[path]);
            changed = true;
        }

        return changed;
    }

    private static ThumbnailState InitialThumbnailState(string extension) =>
        FileKinds.CanThumbnail(extension) ? ThumbnailState.Pending : ThumbnailState.NotApplicable;
}
