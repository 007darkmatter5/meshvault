using System.Collections.Concurrent;
using MeshVault.Data;

namespace MeshVault.Web.Services;

public record OrganizeStatus(
    int LibraryId,
    bool Running,
    string? Message,
    OrganizeResult? Result,
    DateTimeOffset UpdatedUtc,
    OrganizeProgress? Progress = null);

/// <summary>
/// Runs an organize on a background task and exposes its status.
/// </summary>
/// <remarks>
/// Same shape as <see cref="ScanService"/>, and for the same reason: moving
/// several hundred files is minutes of blocking filesystem calls, and doing
/// that on the circuit's own thread leaves the page unable to render the
/// progress it is being told about. It looks finished while it is still going.
/// </remarks>
public class OrganizeService(IServiceScopeFactory scopeFactory, ILogger<OrganizeService> log)
{
    private readonly ConcurrentDictionary<int, OrganizeStatus> _status = new();
    private readonly ConcurrentDictionary<int, byte> _running = new();

    public event Action? Changed;

    public OrganizeStatus? GetStatus(int libraryId) => _status.GetValueOrDefault(libraryId);

    public bool IsRunning(int libraryId) => _running.ContainsKey(libraryId);

    /// <summary>
    /// Starts applying <paramref name="plan"/>. False when one is already
    /// running for this library, which is what stops a second click doubling up
    /// on a half-moved library.
    /// </summary>
    public bool TryStart(int libraryId, OrganizePlan plan)
    {
        if (!_running.TryAdd(libraryId, 0)) return false;

        Update(new OrganizeStatus(libraryId, true, "Starting...", null, DateTimeOffset.UtcNow));
        _ = Task.Run(() => RunAsync(libraryId, plan));
        return true;
    }

    private async Task RunAsync(int libraryId, OrganizePlan plan)
    {
        OrganizeStatus status;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var executor = scope.ServiceProvider.GetRequiredService<OrganizeExecutor>();

            var progress = new Progress<OrganizeProgress>(p => Update(new OrganizeStatus(
                libraryId, true, Describe(p), null, DateTimeOffset.UtcNow, p)));

            var result = await executor.ApplyAsync(libraryId, plan, progress);

            // Organizing creates, merges and empties model folders, and a group
            // is a set of models -- so the memberships have moved even though no
            // sculpt key did. Filing four folders of one mini into one is
            // exactly the case: what was a group of four is now a single model,
            // and the rows saying otherwise would outlive it.
            await scope.ServiceProvider.GetRequiredService<GroupReconciler>()
                .ReconcileAsync(libraryId);

            status = new OrganizeStatus(
                libraryId, false, Describe(result), result, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Organizing library {LibraryId} failed", libraryId);
            status = new OrganizeStatus(
                libraryId, false, $"Failed: {ex.Message}", null, DateTimeOffset.UtcNow);
        }
        finally
        {
            // Cleared before announcing, so a subscriber asking IsRunning while
            // handling the event does not see it stuck at "Moving...".
            _running.TryRemove(libraryId, out _);
        }

        Update(status);
    }

    private static string Describe(OrganizeProgress p) =>
        p.Current is null
            ? "Finishing up..."
            : $"{p.Done:N0} of {p.Total:N0} files - {p.Current}";

    private static string Describe(OrganizeResult r) =>
        r.Clean
            ? $"Moved {r.FilesMoved:N0} file(s) into {r.FoldersCreated:N0} folder(s)."
            : $"Moved {r.FilesMoved:N0} file(s), with {r.Problems.Count} problem(s).";

    private void Update(OrganizeStatus status)
    {
        _status[status.LibraryId] = status;

        // One at a time: a subscriber whose circuit has gone must not stop the
        // rest being told.
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "An organize status subscriber threw; continuing");
            }
        }
    }
}
