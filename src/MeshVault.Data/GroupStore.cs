using MeshVault.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Data;

/// <summary>Applies and undoes variant groupings.</summary>
/// <remarks>
/// Every operation here is a write to three columns. No file is touched, no row
/// is deleted, and each member keeps its own folder, files and metadata — which
/// is what makes ungrouping a complete undo rather than a best effort.
/// </remarks>
public class GroupStore(IDbContextFactory<MeshVaultDbContext> factory)
{
    /// <summary>Applies the chosen proposals. Returns how many models were grouped.</summary>
    public async Task<int> ApplyAsync(
        IEnumerable<GroupProposal> proposals, CancellationToken ct = default)
    {
        var wanted = proposals.ToList();
        if (wanted.Count == 0) return 0;

        await using var db = await factory.CreateDbContextAsync(ct);

        var ids = wanted.SelectMany(p => p.Members.Select(m => m.ModelId)).ToHashSet();
        var models = await db.Models.Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);

        var grouped = 0;
        foreach (var proposal in wanted)
        {
            var primary = proposal.Primary.ModelId;
            foreach (var member in proposal.Members)
            {
                if (!models.TryGetValue(member.ModelId, out var model)) continue;

                model.GroupKey = proposal.Key;
                model.GroupName = proposal.Name;
                model.GroupPrimary = model.Id == primary;
                grouped++;
            }
        }

        await db.SaveChangesAsync(ct);
        return grouped;
    }

    /// <summary>
    /// Breaks a group apart, returning every member to standing on its own.
    /// </summary>
    public async Task<int> UngroupAsync(int libraryId, string groupKey, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        return await db.Models
            .Where(m => m.LibraryId == libraryId && m.GroupKey == groupKey)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.GroupKey, (string?)null)
                .SetProperty(m => m.GroupName, (string?)null)
                .SetProperty(m => m.GroupPrimary, false), ct);
    }

    /// <summary>Every model in the same group as <paramref name="modelId"/>, itself included.</summary>
    /// <remarks>
    /// The single place that answers "what does this model stand for", so
    /// reading a group's files and fanning a tag out to it cannot disagree.
    /// </remarks>
    public async Task<List<ModelEntry>> MembersAsync(int modelId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await MembersAsync(db, modelId, ct);
    }

    internal static async Task<List<ModelEntry>> MembersAsync(
        MeshVaultDbContext db, int modelId, CancellationToken ct = default)
    {
        var model = await db.Models.AsNoTracking()
            .Where(m => m.Id == modelId)
            .Select(m => new { m.Id, m.LibraryId, m.GroupKey })
            .FirstOrDefaultAsync(ct);

        if (model?.GroupKey is null) return [];

        return await db.Models
            .Include(m => m.Files)
            .Where(m => m.LibraryId == model.LibraryId && m.GroupKey == model.GroupKey)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Ids of every model that shares a group with <paramref name="modelId"/>.
    /// A model standing on its own answers with just itself, so callers can fan
    /// a write out without asking whether there is a group at all.
    /// </summary>
    public static async Task<List<int>> MemberIdsAsync(
        MeshVaultDbContext db, int modelId, CancellationToken ct = default)
    {
        var model = await db.Models.AsNoTracking()
            .Where(m => m.Id == modelId)
            .Select(m => new { m.LibraryId, m.GroupKey })
            .FirstOrDefaultAsync(ct);

        if (model?.GroupKey is null) return [modelId];

        return await db.Models
            .Where(m => m.LibraryId == model.LibraryId && m.GroupKey == model.GroupKey)
            .Select(m => m.Id)
            .ToListAsync(ct);
    }
}
