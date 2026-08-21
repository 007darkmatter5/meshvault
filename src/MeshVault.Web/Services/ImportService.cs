using System.Collections.Concurrent;
using MeshVault.Data;

namespace MeshVault.Web.Services;

public record ImportStatus(
    int LibraryId, bool Running, string? Message, ImportResult? Result, DateTimeOffset UpdatedUtc);

/// <summary>
/// Runs datapackage imports in the background, mirroring how scans work so the
/// UI can start one and keep rendering.
/// </summary>
public class ImportService(IServiceScopeFactory scopeFactory, ILogger<ImportService> log)
{
    private readonly ConcurrentDictionary<int, ImportStatus> _status = new();
    private readonly ConcurrentDictionary<int, byte> _running = new();

    public event Action? Changed;

    public ImportStatus? GetStatus(int libraryId) => _status.GetValueOrDefault(libraryId);

    public bool IsRunning(int libraryId) => _running.ContainsKey(libraryId);

    public bool TryStart(int libraryId)
    {
        if (!_running.TryAdd(libraryId, 0)) return false;

        Update(new ImportStatus(libraryId, true, "Starting...", null, DateTimeOffset.UtcNow));
        _ = Task.Run(() => RunAsync(libraryId));
        return true;
    }

    private async Task RunAsync(int libraryId)
    {
        ImportStatus status;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var importer = scope.ServiceProvider.GetRequiredService<DatapackageImporter>();

            var progress = new Progress<ImportProgress>(p => Update(new ImportStatus(
                libraryId, true,
                p.Current == "Saving..." ? "Saving..." : $"{p.Done:N0} of {p.Total:N0} checked",
                null, DateTimeOffset.UtcNow)));

            var result = await importer.ImportAsync(libraryId, progress);
            status = new ImportStatus(libraryId, false, Describe(result), result, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Datapackage import for library {LibraryId} failed", libraryId);
            status = new ImportStatus(libraryId, false, $"Failed: {ex.Message}", null, DateTimeOffset.UtcNow);
        }
        finally
        {
            // Cleared before announcing, so subscribers that ask IsRunning while
            // handling the event see the finished state.
            _running.TryRemove(libraryId, out _);
        }

        Update(status);
    }

    private static string Describe(ImportResult r) =>
        r.Changed == 0
            ? $"Nothing to add ({r.Scanned:N0} checked, {r.Skipped:N0} without a sidecar)."
            : $"{r.Renamed:N0} renamed, {r.Tagged:N0} tagged, {r.Collected:N0} collected, "
              + $"{r.DesignersSet:N0} designers, {r.SourcesSet:N0} sources.";

    private void Update(ImportStatus status)
    {
        _status[status.LibraryId] = status;

        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch (Exception ex) { log.LogWarning(ex, "An import subscriber threw; continuing"); }
        }
    }
}
