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

    public bool TryStart(int libraryId)
    {
        if (!_running.TryAdd(libraryId, 0)) return false;

        Update(new ScanStatus(libraryId, true, "Starting...", null, DateTimeOffset.UtcNow));
        _ = Task.Run(() => RunAsync(libraryId));
        return true;
    }

    private async Task RunAsync(int libraryId)
    {
        ScanStatus status;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var indexer = scope.ServiceProvider.GetRequiredService<LibraryIndexer>();

            var progress = new Progress<ScanProgress>(p => Update(new ScanStatus(
                libraryId, true, Describe(p), null, DateTimeOffset.UtcNow, p)));

            var result = await indexer.IndexAsync(libraryId, progress);

            status = new ScanStatus(libraryId, false,
                $"Added {result.Added}, updated {result.Updated}, removed {result.Removed}.",
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
