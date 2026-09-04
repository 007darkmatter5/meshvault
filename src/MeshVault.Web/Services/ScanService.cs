using System.Collections.Concurrent;
using MeshVault.Data;

namespace MeshVault.Web.Services;

public record ScanStatus(
    int LibraryId,
    bool Running,
    string? Message,
    IndexResult? Result,
    DateTimeOffset UpdatedUtc,
    ScanProgress? Progress = null);

/// <summary>
/// Runs library scans on background tasks and exposes their status, so the UI
/// can start a scan and keep rendering while a large share is walked.
/// </summary>
public class ScanService(IServiceScopeFactory scopeFactory, ILogger<ScanService> log)
{
    private readonly ConcurrentDictionary<int, ScanStatus> _status = new();
    private readonly ConcurrentDictionary<int, byte> _running = new();

    public event Action? Changed;

    public ScanStatus? GetStatus(int libraryId) => _status.GetValueOrDefault(libraryId);

    public bool IsRunning(int libraryId) => _running.ContainsKey(libraryId);

    /// <summary>
    /// Starts a scan, of the whole library or of one folder inside it.
    /// </summary>
    /// <param name="subPath">
    /// A folder relative to the library root -- the inbox, in practice -- or
    /// null for the whole library.
    /// </param>
    /// <remarks>
    /// Both kinds take the same per-library slot, so a folder scan and a full
    /// one cannot run against the same library at once. They would be reading
    /// and writing the same rows while sharing one progress display, and
    /// nothing good is on the other side of letting them.
    /// </remarks>
    public bool TryStart(int libraryId, string? subPath = null)
    {
        if (!_running.TryAdd(libraryId, 0)) return false;

        Update(new ScanStatus(libraryId, true, "Starting...", null, DateTimeOffset.UtcNow));
        _ = Task.Run(() => RunAsync(libraryId, subPath));
        return true;
    }

    private async Task RunAsync(int libraryId, string? subPath)
    {
        ScanStatus status;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var indexer = scope.ServiceProvider.GetRequiredService<LibraryIndexer>();

            var progress = new Progress<ScanProgress>(p => Update(new ScanStatus(
                libraryId, true, Describe(p), null, DateTimeOffset.UtcNow, p)));

            var result = string.IsNullOrWhiteSpace(subPath)
                ? await indexer.IndexAsync(libraryId, progress)
                : await indexer.IndexFolderAsync(libraryId, subPath, progress);

            // Grouping is read from the files, so a scan that added the fourth
            // cut of a mini has to settle the library it just changed. Whole
            // library even after an inbox scan: the folders the new one joins
            // are outside the inbox, and this is rows rather than a walk of the
            // share, so it costs nothing next to what has just been done.
            await scope.ServiceProvider.GetRequiredService<GroupReconciler>()
                .ReconcileAsync(libraryId);

            // Which folder was looked at, because "removed 0" reads very
            // differently depending on whether the whole library was walked.
            var where = string.IsNullOrWhiteSpace(subPath) ? "" : $"{subPath}/: ";

            status = new ScanStatus(libraryId, false,
                $"{where}Added {result.Added}, updated {result.Updated}, removed {result.Removed}.",
                result, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Scan of library {LibraryId} failed", libraryId);
            status = new ScanStatus(libraryId, false, $"Failed: {ex.Message}", null, DateTimeOffset.UtcNow);
        }
        finally
        {
            // Cleared *before* the change is announced. Subscribers call
            // IsRunning() while handling the event, so announcing first would
            // leave the UI showing "Scanning..." with no further event to come.
            _running.TryRemove(libraryId, out _);
        }

        Update(status);
    }

    private static string Describe(ScanProgress p) =>
        p.CurrentFolder == "Saving..."
            ? $"Saving {p.ModelsSeen:N0} models..."
            : $"{p.ModelsSeen:N0} models, {p.FilesSeen:N0} files so far";

    private void Update(ScanStatus status)
    {
        _status[status.LibraryId] = status;

        // Handlers are invoked one at a time: a subscriber whose circuit has
        // gone away must not stop the remaining subscribers from being told.
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "A scan status subscriber threw; continuing");
            }
        }
    }
}
