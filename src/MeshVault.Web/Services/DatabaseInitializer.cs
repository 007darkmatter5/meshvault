using MeshVault.Core.Imaging;
using MeshVault.Core.Meshes;
using MeshVault.Core.Models;
using MeshVault.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeshVault.Web.Services;

/// <summary>
/// Brings the schema up to date and seeds configured library roots. Runs to
/// completion before the server accepts traffic, so no request can arrive
/// against a half-migrated database.
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        var db = sp.GetRequiredService<MeshVaultDbContext>();
        await db.Database.MigrateAsync();

        var options = sp.GetRequiredService<IOptions<MeshVaultOptions>>().Value;
        var log = sp.GetRequiredService<ILogger<MeshVaultDbContext>>();

        foreach (var configured in options.Libraries)
        {
            if (string.IsNullOrWhiteSpace(configured.Path)) continue;

            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured.Path));
            if (await db.Libraries.AnyAsync(l => l.Path == path)) continue;

            db.Libraries.Add(new Library
            {
                Name = string.IsNullOrWhiteSpace(configured.Name) ? Path.GetFileName(path) : configured.Name,
                Path = path,
                AllowOrganize = configured.AllowOrganize,
            });
            log.LogInformation("Seeded library {Path}", path);
        }

        await db.SaveChangesAsync();

        // Payloads from a previous format version are unreadable now and would
        // otherwise sit on disk forever.
        var pruned = sp.GetRequiredService<GeometryCache>().PruneOldVersions();
        if (pruned > 0) log.LogInformation("Removed {Count} outdated geometry payloads", pruned);

        // Staged copies are transient by definition, so anything still on disk
        // was stranded by a process that did not shut down cleanly. Left alone
        // it grows without bound, and each stranded file is as large as the
        // model it came from.
        var reclaimed = StagedMeshFile.CleanUp(Path.Combine(options.DataPath, "staging"));
        if (reclaimed > 0)
        {
            log.LogInformation(
                "Cleared {Megabytes:N0} MB of staged model files left by a previous run",
                reclaimed / 1048576);
        }

        await RequeueThumbnailsIfRendererChangedAsync(sp, db, log);
    }

    /// <summary>
    /// Queues every thumbnail for re-rendering when the renderer has changed in
    /// a way that alters the output. Without this a fix to the renderer only
    /// reaches models added afterwards, and the library keeps showing images
    /// produced by the old, wrong code.
    /// </summary>
    private static async Task RequeueThumbnailsIfRendererChangedAsync(
        IServiceProvider sp, MeshVaultDbContext db, ILogger log)
    {
        var settings = sp.GetRequiredService<SettingsStore>();
        var rendered = await settings.GetIntAsync(SettingKeys.ThumbnailRenderVersion, fallback: 0);

        if (rendered == ThumbnailStore.RenderVersion) return;

        // Snapshots the user chose are theirs and are never regenerated.
        var queued = await db.Files
            .Where(f => f.ThumbnailState == ThumbnailState.Ready
                     || f.ThumbnailState == ThumbnailState.Failed)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.ThumbnailState, ThumbnailState.Pending));

        await settings.SetIntAsync(SettingKeys.ThumbnailRenderVersion, ThumbnailStore.RenderVersion);

        log.LogInformation(
            "Renderer changed from version {Old} to {New}; queued {Count} thumbnail(s) to be redone",
            rendered, ThumbnailStore.RenderVersion, queued);
    }
}
