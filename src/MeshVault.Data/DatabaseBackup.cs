using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeshVault.Data;

/// <summary>
/// Copies the database aside before a migration is allowed to change it.
/// </summary>
/// <remarks>
/// Migrations apply themselves at startup, unattended, on somebody's own
/// server. Most only add a column and could be undone by hand; some move rows,
/// and those cannot. <c>SharedCollections</c> unions two accounts' collections
/// of the same name onto one survivor -- its <c>Down()</c> puts the column back
/// but has no way to un-merge what it merged.
///
/// So the rule is: nothing that cannot be undone runs without a copy of what it
/// is about to change. A person who has never read a release note still gets to
/// go back.
/// </remarks>
public static class DatabaseBackup
{
    /// <summary>How many copies to keep. Enough to go back a few upgrades.</summary>
    private const int Keep = 5;

    /// <summary>
    /// Takes a copy when there is a migration waiting, and returns where it
    /// went. Null when nothing is pending or there is no database yet.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The copy could not be made. Deliberately fatal: starting anyway would
    /// apply an irreversible migration with no way back, and a server that
    /// refuses to start with a clear reason is recoverable in a way a merged
    /// database is not.
    /// </exception>
    public static async Task<string?> BeforeMigratingAsync(
        DbContext db, string dataPath, ILogger? log = null, CancellationToken ct = default)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count == 0) return null;

        // A file that is not there yet holds nothing to lose, which is every
        // first run.
        var source = db.Database.GetDbConnection().DataSource;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return null;

        var folder = Path.Combine(dataPath, "backups");
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var target = Path.Combine(folder, $"meshvault-{stamp}-before-{pending[0]}.db");

        try
        {
            Directory.CreateDirectory(folder);

            // VACUUM INTO rather than copying the file. SQLite keeps recent
            // writes in a -wal sidecar, so a plain copy of the .db can be
            // missing whatever has not been checkpointed into it yet -- which
            // is exactly the newest data, and exactly what somebody restoring
            // would be trying to get back.
            await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{Escape(target)}'", ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"MeshVault has {pending.Count} database change(s) to apply and could not save a "
                + $"copy of the database first, so it has stopped rather than risk your data. "
                + $"Tried to write: {target}. "
                + $"Check that the data folder is writable and has space free. ({ex.Message})",
                ex);
        }

        log?.LogInformation(
            "Saved a copy of the database to {Path} before applying {Count} change(s), first {Name}",
            target, pending.Count, pending[0]);

        Prune(folder, log);
        return target;
    }

    /// <summary>
    /// Drops all but the newest few copies.
    /// </summary>
    /// <remarks>
    /// Failing to tidy up is not a reason to refuse to start: the copy that
    /// matters has already been written by the time this runs.
    /// </remarks>
    private static void Prune(string folder, ILogger? log)
    {
        try
        {
            var stale = new DirectoryInfo(folder)
                .GetFiles("meshvault-*-before-*.db")
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(Keep)
                .ToList();

            foreach (var file in stale) file.Delete();

            if (stale.Count > 0)
                log?.LogInformation("Removed {Count} older database backup(s)", stale.Count);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Could not tidy older database backups in {Folder}", folder);
        }
    }

    /// <summary>
    /// A path safe to sit inside SQL quotes. VACUUM INTO takes a literal and
    /// not a parameter, so this is the one place a path is spliced into a
    /// statement -- and a directory with an apostrophe in it is somebody's
    /// ordinary Windows folder rather than an attack.
    /// </summary>
    private static string Escape(string path) => path.Replace("'", "''");
}
