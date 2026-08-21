using MeshVault.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Data;

/// <summary>Reads and writes persisted UI preferences.</summary>
public class SettingsStore(IDbContextFactory<MeshVaultDbContext> factory)
{
    public async Task<bool> GetBoolAsync(string key, bool fallback = false, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var value = await db.Settings.AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        return value is null ? fallback : value == "1";
    }

    public async Task SetBoolAsync(string key, bool value, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var existing = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (existing is null)
        {
            db.Settings.Add(new Setting
            {
                Key = key,
                Value = value ? "1" : "0",
                UpdatedUtc = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.Value = value ? "1" : "0";
            existing.UpdatedUtc = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }
}
