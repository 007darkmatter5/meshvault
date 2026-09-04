using MeshVault.Core.Models;
using MeshVault.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Data;

/// <summary>
/// A model plus the current user's view of it. Favorites are per-user, so
/// "is this a favorite" is not a property of the model itself.
/// </summary>
public record ModelCard(ModelEntry Model, bool IsFavorite);

/// <summary>Read-side queries for browsing the catalog.</summary>
public class ModelCatalog(IDbContextFactory<MeshVaultDbContext> factory, ICurrentUser user)
{
    public async Task<PagedResult<ModelCard>> SearchAsync(ModelQuery query, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var userId = user.UserId;

        var models = Filtered(db, query, userId);

        var total = await models.CountAsync(ct);

        models = query.Sort switch
        {
            // By id rather than by AddedUtc, which is what this means but
            // cannot be asked for: SQLite will not ORDER BY a DateTimeOffset and
            // EF throws rather than falling back, so picking "Recently added"
            // took the whole page down. Ids are handed out when the row is
            // created, which is the moment AddedUtc records, so the order is the
            // same one.
            ModelSort.Newest => models.OrderByDescending(m => m.Id),
            ModelSort.Largest => models.OrderByDescending(m => m.TotalBytes).ThenBy(m => m.Id),
            _ => models.OrderBy(m => m.Name).ThenBy(m => m.Id),
        };

        var page = Math.Max(1, query.Page);
        var items = await models
            .Skip((page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(m => new ModelCard(m, m.Favorites.Any(f => f.UserId == userId)))
            .ToListAsync(ct);

        // Loaded separately so the projection above stays a single flat query.
        var ids = items.Select(i => i.Model.Id).ToList();
        var withRelations = await db.Models.AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .Include(m => m.Tags)
            .Include(m => m.Files)
            .Include(m => m.Designer)
            .Include(m => m.Library)
            .ToDictionaryAsync(m => m.Id, ct);

        var cards = items
            .Select(i => withRelations.TryGetValue(i.Model.Id, out var full)
                ? i with { Model = full }
                : i)
            .ToList();

        return new PagedResult<ModelCard>(cards, total, page, query.PageSize);
    }

    /// <summary>
    /// Every model the query matches, ignoring paging.
    /// </summary>
    /// <remarks>
    /// For "select everything that matches these filters" in the browser, where
    /// the point is to act on the whole result rather than the page in front of
    /// you. Ids only: a bulk edit needs nothing else, and the full result could
    /// be the entire library.
    /// </remarks>
    public async Task<List<int>> GetMatchingIdsAsync(ModelQuery query, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await Filtered(db, query, user.UserId)
            .OrderBy(m => m.Id)
            .Select(m => m.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// The filter half of a browse query, shared so that selecting everything
    /// that matches cannot drift from what the page actually showed.
    /// </summary>
    private static IQueryable<ModelEntry> Filtered(MeshVaultDbContext db, ModelQuery query, string userId)
    {
        // One entry per group. A sculpt shipped supported, unsupported, hollowed
        // and no-logo is four folders and so four rows, but it is one thing to
        // browse; the primary member stands for the rest. Ungrouped models —
        // most of them — are unaffected.
        var models = db.Models.AsNoTracking()
            .Where(m => m.GroupKey == null || m.GroupPrimary);

        if (query.LibraryId is { } libraryId)
            models = models.Where(m => m.LibraryId == libraryId);

        if (query.DesignerId is { } designerId)
            models = models.Where(m => m.DesignerId == designerId);

        if (query.CollectionId is { } collectionId)
            models = models.Where(m => m.Collections.Any(c => c.Id == collectionId));

        if (!string.IsNullOrWhiteSpace(query.SourceSite))
            models = models.Where(m => m.SourceSite == query.SourceSite);

        if (query.MissingDesigner)
            models = models.Where(m => m.DesignerId == null);

        if (query.MissingSource)
            models = models.Where(m => m.SourceUrl == null);

        // One answer for everybody, now that collections are shared. This used
        // to mean "in none of the asker's own collections", so the same model
        // read as unfiled or filed depending on who asked -- which was the same
        // split that let two accounts organise one library two ways.
        if (query.MissingCollection)
            models = models.Where(m => !m.Collections.Any());

        // Unfiled is a fact about where a model sits, not a flag on it, so it is
        // asked of the path against its own library's inbox rather than stored.
        //
        // Lowered on both sides: the stored path keeps whatever case was typed,
        // which need not be the case the folder actually has on disk.
        if (query.UnfiledOnly)
        {
            models = models.Where(m =>
                m.Library!.InboxPath != null
                && (m.RelativePath.ToLower() == m.Library.InboxPath.ToLower()
                    || m.RelativePath.ToLower().StartsWith(m.Library.InboxPath.ToLower() + "/")));
        }

        if (query.FavoritesOnly)
            models = models.Where(m => m.Favorites.Any(f => f.UserId == userId));

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            models = models.Where(m =>
                EF.Functions.Like(m.Name, $"%{term}%")
                || EF.Functions.Like(m.RelativePath, $"%{term}%")
                || (m.Description != null && EF.Functions.Like(m.Description, $"%{term}%"))
                || (m.Notes != null && EF.Functions.Like(m.Notes, $"%{term}%"))
                || (m.Designer != null && EF.Functions.Like(m.Designer.Name, $"%{term}%")));
        }

        // Tags are ANDed: selecting "dragon" and "supported" narrows, not widens.
        foreach (var tag in query.Tags)
        {
            var normalized = tag.ToLowerInvariant();
            models = models.Where(m => m.Tags.Any(t => t.NormalizedName == normalized));
        }

        return models;
    }

    public async Task<ModelCard?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var userId = user.UserId;

        var model = await db.Models
            .AsNoTracking()
            .Include(m => m.Tags)
            .Include(m => m.Files)
            .Include(m => m.Library)
            .Include(m => m.Designer)
            .Include(m => m.Collections)
            .FirstOrDefaultAsync(m => m.Id == id, ct);

        if (model is null) return null;

        var isFavorite = await db.Favorites
            .AnyAsync(f => f.ModelEntryId == id && f.UserId == userId, ct);

        return new ModelCard(model, isFavorite);
    }

    /// <summary>
    /// Every model sharing a group with this one, itself included, ordered best
    /// export first. Empty when the model stands on its own.
    /// </summary>
    /// <remarks>
    /// What lets one page stand for four folders. Returning empty rather than a
    /// single-element list keeps "is this a group" a question the caller can ask
    /// without comparing counts.
    /// </remarks>
    public async Task<List<ModelEntry>> GetGroupMembersAsync(int modelId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var group = await db.Models.AsNoTracking()
            .Where(m => m.Id == modelId)
            .Select(m => new { m.LibraryId, m.GroupKey })
            .FirstOrDefaultAsync(ct);

        if (group?.GroupKey is null) return [];

        var members = await db.Models.AsNoTracking()
            .Include(m => m.Tags)
            .Include(m => m.Files)
            .Where(m => m.LibraryId == group.LibraryId && m.GroupKey == group.GroupKey)
            .ToListAsync(ct);

        return [.. members.OrderBy(m => m.Files.Count == 0 ? int.MaxValue : m.Files.Min(f => f.VariantRank))
                          .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// How many models are still sitting in each library's inbox, keyed by
    /// library id. Libraries with no inbox do not appear.
    /// </summary>
    public async Task<Dictionary<int, int>> GetUnfiledCountsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        return await db.Models.AsNoTracking()
            .Where(m => m.Library!.InboxPath != null
                && (m.RelativePath.ToLower() == m.Library.InboxPath.ToLower()
                    || m.RelativePath.ToLower().StartsWith(m.Library.InboxPath.ToLower() + "/")))
            .GroupBy(m => m.LibraryId)
            .Select(g => new { LibraryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.LibraryId, x => x.Count, ct);
    }

    public async Task<List<Library>> GetLibrariesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Libraries.AsNoTracking().OrderBy(l => l.Name).ToListAsync(ct);
    }

    /// <summary>Tags with their usage counts, most-used first, for the filter sidebar.</summary>
    public async Task<List<(Tag Tag, int Count)>> GetTagCountsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Tags
            .AsNoTracking()
            .Select(t => new { Tag = t, Count = t.Models.Count })
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count).ThenBy(x => x.Tag.Name)
            .ToListAsync(ct);
        return rows.Select(x => (x.Tag, x.Count)).ToList();
    }

    public async Task<List<(Designer Designer, int Count)>> GetDesignersAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Designers
            .AsNoTracking()
            .Select(d => new { Designer = d, Count = d.Models.Count })
            .OrderByDescending(x => x.Count).ThenBy(x => x.Designer.Name)
            .ToListAsync(ct);
        return rows.Select(x => (x.Designer, x.Count)).ToList();
    }

    public async Task<List<(Collection Collection, int Count)>> GetCollectionsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Collections
            .AsNoTracking()
            .Select(c => new { Collection = c, Count = c.Models.Count })
            .OrderBy(x => x.Collection.Name)
            .ToListAsync(ct);
        return rows.Select(x => (x.Collection, x.Count)).ToList();
    }

    /// <summary>Source sites present in the catalog, with counts, for filtering.</summary>
    public async Task<List<(string Site, int Count)>> GetSourceSitesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Models
            .AsNoTracking()
            .Where(m => m.SourceSite != null)
            .GroupBy(m => m.SourceSite!)
            .Select(g => new { Site = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count).ThenBy(x => x.Site)
            .ToListAsync(ct);
        return rows.Select(x => (x.Site, x.Count)).ToList();
    }

    public async Task<CatalogStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return new CatalogStats(
            Models: await db.Models.CountAsync(ct),
            Files: await db.Files.CountAsync(ct),
            TotalBytes: await db.Models.SumAsync(m => m.TotalBytes, ct),
            Libraries: await db.Libraries.CountAsync(ct),
            Designers: await db.Designers.CountAsync(ct),
            MissingSource: await db.Models.CountAsync(m => m.SourceUrl == null, ct));
    }
}

public record CatalogStats(
    int Models, int Files, long TotalBytes, int Libraries, int Designers, int MissingSource);
