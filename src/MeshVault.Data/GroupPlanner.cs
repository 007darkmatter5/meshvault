using MeshVault.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Data;

/// <summary>One model that would join a group, and what marks it out.</summary>
public record GroupMember(int ModelId, string Name, string RelativePath, string? VariantLabel, int Rank)
{
    public string Variant => VariantLabel ?? "Plain";
}

/// <summary>
/// A set of folders holding the same sculpt, proposed as one entry.
/// </summary>
public record GroupProposal(string Key, string Name, IReadOnlyList<GroupMember> Members)
{
    /// <summary>The member that will represent the group: the best export of it.</summary>
    public GroupMember Primary => Members[0];

    /// <summary>Where these folders sit, for showing what is being merged.</summary>
    public string CommonParent => Paths.CommonParent(Members.Select(m => m.RelativePath));

    /// <summary>True when applying this would change nothing.</summary>
    public bool AlreadyApplied { get; init; }
}

public record GroupPlan(IReadOnlyList<GroupProposal> Proposals)
{
    public IReadOnlyList<GroupProposal> Pending =>
        [.. Proposals.Where(p => !p.AlreadyApplied)];

    /// <summary>How many models the pending proposals would fold away.</summary>
    public int ModelsAffected => Pending.Sum(p => p.Members.Count);
}

/// <summary>
/// Works out which model folders are exports of the same sculpt.
///
/// Proposes only; nothing is written until someone approves it. Grouping
/// changes what the library looks like, and a library that rearranges itself
/// after every scan is worse than one that never does.
/// </summary>
public class GroupPlanner(IDbContextFactory<MeshVaultDbContext> factory)
{
    /// <summary>
    /// Groups candidates by the single sculpt they hold.
    /// </summary>
    /// <remarks>
    /// Only a model whose meshes are all one sculpt can join a group: a folder
    /// holding ninety-eight of them is a pack, not an export, and folding it in
    /// would claim it is the same thing as a single mini. Those are left alone —
    /// they want splitting first, which is a different job.
    /// </remarks>
    public async Task<GroupPlan> PlanAsync(int libraryId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // Two flat reads joined in memory rather than one query with a
        // correlated sub-select: SQLite cannot do the APPLY that "the best
        // ranked file of each model" compiles to, and EF throws rather than
        // falling back.
        var models = await db.Models
            .Where(m => m.LibraryId == libraryId)
            .Select(m => new { m.Id, m.Name, m.RelativePath, m.GroupKey })
            .ToListAsync(ct);

        var sculpts = await db.Files
            .Where(f => f.SculptKey != null && f.ModelEntry!.LibraryId == libraryId)
            .Select(f => new { f.ModelEntryId, f.SculptKey, f.VariantLabel, f.VariantRank })
            .ToListAsync(ct);

        var byModel = sculpts
            .GroupBy(f => f.ModelEntryId)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.VariantRank).ToList());

        var candidates = models
            .Select(m =>
            {
                var files = byModel.GetValueOrDefault(m.Id) ?? [];
                var distinct = files.Select(f => f.SculptKey!)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                return new
                {
                    m.Id,
                    m.Name,
                    m.RelativePath,
                    m.GroupKey,
                    Sculpts = distinct,
                    Label = files.Count == 0 ? null : files[0].VariantLabel,
                    Rank = files.Count == 0 ? 0 : files[0].VariantRank,
                };
            })
            .ToList();

        var proposals = candidates
            .Where(m => m.Sculpts.Count == 1)
            .GroupBy(m => m.Sculpts[0], StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g =>
            {
                var members = g
                    .OrderBy(m => m.Rank)
                    .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(m => new GroupMember(m.Id, m.Name, m.RelativePath, m.Label, m.Rank))
                    .ToList();

                // The plain export's name is the one without an abbreviation
                // buried in it — "Is 130 Grid Garage Ground" rather than
                // "Is 130 Hol Grid Garage Ground".
                return new GroupProposal(g.Key, members[0].Name, members)
                {
                    AlreadyApplied = g.All(m => string.Equals(
                        m.GroupKey, g.Key, StringComparison.OrdinalIgnoreCase)),
                };
            })
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new GroupPlan(proposals);
    }
}

/// <summary>Path arithmetic shared by the planner and what shows it.</summary>
public static class Paths
{
    /// <summary>
    /// Deepest folder every path sits under, or "" when they share no ancestor.
    /// </summary>
    public static string CommonParent(IEnumerable<string> paths)
    {
        string[]? common = null;

        foreach (var path in paths)
        {
            // The model's own folder is not an ancestor of itself.
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[..^1];

            if (common is null)
            {
                common = parts;
                continue;
            }

            var shared = 0;
            while (shared < common.Length && shared < parts.Length
                && string.Equals(common[shared], parts[shared], StringComparison.OrdinalIgnoreCase))
                shared++;

            common = common[..shared];
        }

        return common is null ? "" : string.Join('/', common);
    }
}
