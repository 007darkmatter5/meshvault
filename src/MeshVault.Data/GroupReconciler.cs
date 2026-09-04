using MeshVault.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Data;

/// <summary>
/// Keeps <see cref="ModelEntry.GroupKey"/> in step with what the files say, so
/// separate folders holding one sculpt show as one thing in Browse.
/// </summary>
/// <remarks>
/// This used to be <c>GroupPlanner</c> proposing and somebody approving, on a
/// page of its own. The reasoning then was that "a library that rearranges
/// itself after every scan is worse than one that never does" -- which is true
/// of anything that moves files, and grouping moves nothing. It changes how
/// Browse lists what is already there.
///
/// Leaving it as an approval step meant the library was only grouped if you
/// remembered to go and ask, and stopped being grouped correctly the moment a
/// scan added the fourth cut of a mini. Derived and recomputed, "four folders
/// holding one mini are one mini" is simply true rather than something you
/// once agreed to.
///
/// The result is deterministic and depends only on sculpt keys, which are
/// themselves stable: organizing pins them, and a hand-set one is never
/// overwritten. So this settles rather than oscillating -- and correcting a
/// sculpt is how you change a grouping you disagree with, which is the same
/// control that decides everything else about a sculpt.
/// </remarks>
public class GroupReconciler(IDbContextFactory<MeshVaultDbContext> factory)
{
    /// <summary>
    /// Recomputes every group in one library. Returns how many models had their
    /// grouping changed, which is zero on the overwhelming majority of runs.
    /// </summary>
    public async Task<int> ReconcileAsync(int libraryId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var models = await db.Models.Where(m => m.LibraryId == libraryId).ToListAsync(ct);
        if (models.Count == 0) return 0;

        // Two flat reads joined in memory rather than one query with a
        // correlated sub-select: SQLite cannot do the APPLY that "the best
        // ranked file of each model" compiles to, and EF throws rather than
        // falling back.
        var sculpts = await db.Files
            .Where(f => f.SculptKey != null && f.ModelEntry!.LibraryId == libraryId)
            .Select(f => new { f.ModelEntryId, f.SculptKey, f.VariantRank })
            .ToListAsync(ct);

        var byModel = sculpts
            .GroupBy(f => f.ModelEntryId)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.VariantRank).ToList());

        // Only a model whose meshes are all one sculpt can join a group. A
        // folder holding ninety-eight of them is a pack, not an export, and
        // folding it in would claim it is the same thing as a single mini.
        // Those want splitting first, which is organizing's job and not this.
        string? SoleSculpt(ModelEntry model)
        {
            if (!byModel.TryGetValue(model.Id, out var files)) return null;

            var distinct = files
                .Select(f => f.SculptKey!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return distinct.Count == 1 ? distinct[0] : null;
        }

        // Best-ranked file of the model, which is how one cut of a mini is
        // judged against another: the plain export beats the supported one.
        int Rank(ModelEntry model) =>
            byModel.TryGetValue(model.Id, out var files) && files.Count > 0 ? files[0].VariantRank : 0;

        var groups = models
            .Where(m => SoleSculpt(m) is not null)
            .GroupBy(SoleSculpt!, StringComparer.OrdinalIgnoreCase)

            // One folder holding one mini is not a group, it is a model.
            .Where(g => g.Count() > 1)
            .ToList();

        var placements = new Dictionary<int, (string Key, string Name, bool Primary)>();
        foreach (var group in groups)
        {
            var members = group
                .OrderBy(Rank)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // The plain export's name is the one without an abbreviation buried
            // in it -- "Is 130 Grid Garage Ground" rather than
            // "Is 130 Hol Grid Garage Ground".
            var name = members[0].Name;

            for (var i = 0; i < members.Count; i++)
                placements[members[i].Id] = (group.Key, name, i == 0);
        }

        var changed = 0;
        foreach (var model in models)
        {
            var (key, name, primary) = placements.TryGetValue(model.Id, out var placed)
                ? placed
                : (null, null, false);

            if (model.GroupKey == key && model.GroupName == name && model.GroupPrimary == primary)
                continue;

            model.GroupKey = key;
            model.GroupName = name;
            model.GroupPrimary = primary;
            changed++;
        }

        if (changed > 0) await db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>Recomputes every library, for the passes that touch them all.</summary>
    public async Task<int> ReconcileAllAsync(CancellationToken ct = default)
    {
        List<int> libraries;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            libraries = await db.Libraries.Select(l => l.Id).ToListAsync(ct);
        }

        var changed = 0;
        foreach (var id in libraries) changed += await ReconcileAsync(id, ct);
        return changed;
    }
}
