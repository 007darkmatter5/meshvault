using MeshVault.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Data;

/// <summary>
/// Reads a grouping. <see cref="GroupReconciler"/> is what writes one.
/// </summary>
/// <remarks>
/// Purely questions now. This used to apply and undo groupings chosen on a page
/// of its own; grouping is derived from the files instead, so there is nothing
/// left to approve and nothing to undo -- correcting a sculpt is how a grouping
/// is changed.
/// </remarks>
public class GroupStore(IDbContextFactory<MeshVaultDbContext> factory)
{
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
