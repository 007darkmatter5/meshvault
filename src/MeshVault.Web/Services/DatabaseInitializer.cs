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
    }
}
