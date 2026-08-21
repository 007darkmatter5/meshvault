using MeshVault.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeshVault.Web.Services;

/// <summary>
/// Scans libraries at startup, skipping any scanned recently.
/// </summary>
/// <remarks>
/// This used to rescan unconditionally. On a library held on a slow share a
/// full walk takes several minutes, so every restart re-read the whole tree and
/// left the Libraries page showing "Scanning..." for no reason, while competing
/// for bandwidth with everything the user was trying to do.
/// </remarks>
public class StartupIndexer(
    IServiceScopeFactory scopeFactory,
    ScanService scans,
    IOptions<MeshVaultOptions> options,
    ILogger<StartupIndexer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.ScanOnStartup) return;

        var interval = TimeSpan.FromHours(Math.Max(0, options.Value.RescanIntervalHours));
        var now = DateTimeOffset.UtcNow;

        List<(int Id, string Name, DateTimeOffset? LastScanned)> libraries;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeshVaultDbContext>();
            libraries = await db.Libraries
                .Where(l => l.Enabled)
                .Select(l => new { l.Id, l.Name, l.LastScannedUtc })
                .ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(x => (x.Id, x.Name, x.LastScannedUtc)).ToList(), ct);
        }

        var queued = 0;
        foreach (var (id, name, lastScanned) in libraries)
        {
            if (ct.IsCancellationRequested) return;

            if (interval > TimeSpan.Zero && lastScanned is { } when && now - when < interval)
            {
                log.LogInformation(
                    "Skipping startup scan of {Library}: last scanned {Ago:0.#} h ago",
                    name, (now - when).TotalHours);
                continue;
            }

            scans.TryStart(id);
            queued++;
        }

        log.LogInformation(
            "Startup scan queued for {Queued} of {Total} librarie(s)", queued, libraries.Count);
    }
}
