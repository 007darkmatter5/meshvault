using MeshVault.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Data;

/// <summary>Reads and curates the variant vocabulary.</summary>
public class VariantStore(IDbContextFactory<MeshVaultDbContext> factory)
{
    /// <summary>Best-ranked first, which is the order they are offered in.</summary>
    public async Task<List<VariantDefinition>> GetAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        return await db.VariantDefinitions.AsNoTracking()
            .OrderBy(d => d.PreviewRank).ThenBy(d => d.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Puts the starter vocabulary in place the first time this instance runs.
    /// Returns what is now stored either way.
    /// </summary>
    /// <remarks>
    /// Only ever seeds an empty table. Somebody who deletes every definition
    /// meant to, and having them reappear on the next restart would be a bug
    /// rather than a kindness.
    /// </remarks>
    public async Task<List<VariantDefinition>> SeedIfEmptyAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        if (!await db.Settings.AnyAsync(s => s.Key == SettingKeys.VariantsSeeded, ct))
        {
            if (!await db.VariantDefinitions.AnyAsync(ct))
            {
                foreach (var definition in VariantDefinition.Starter())
                {
                    definition.NormalizedName = Normalize(definition.Name);
                    db.VariantDefinitions.Add(definition);
                }
            }

            db.Settings.Add(new Setting
            {
                Key = SettingKeys.VariantsSeeded,
                Value = "1",
                UpdatedUtc = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync(ct);
        }

        return await db.VariantDefinitions.AsNoTracking()
            .OrderBy(d => d.PreviewRank).ThenBy(d => d.Name)
            .ToListAsync(ct);
    }

    /// <summary>Adds or updates a definition. Returns null when the name is taken.</summary>
    public async Task<VariantDefinition?> SaveAsync(VariantDefinition edited, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var normalized = Normalize(edited.Name);
        if (normalized.Length == 0) return null;

        var clash = await db.VariantDefinitions
            .AnyAsync(d => d.NormalizedName == normalized && d.Id != edited.Id, ct);
        if (clash) return null;

        var row = edited.Id == 0
            ? null
            : await db.VariantDefinitions.FirstOrDefaultAsync(d => d.Id == edited.Id, ct);

        if (row is null)
        {
            row = new VariantDefinition();
            db.VariantDefinitions.Add(row);
        }

        row.Name = edited.Name.Trim();
        row.NormalizedName = normalized;
        row.MatchTerms = edited.MatchTerms.Trim();
        row.PreviewRank = edited.PreviewRank;
        row.IsFiller = edited.IsFiller;

        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.VariantDefinitions.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
    }

    private static string Normalize(string name) => name.Trim().ToLowerInvariant();
}
