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

        var models = db.Models.AsNoTracking().AsQueryable();

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

        var total = await models.CountAsync(ct);

        models = query.Sort switch
        {
            ModelSort.Newest => models.OrderByDescending(m => m.AddedUtc).ThenBy(m => m.Id),
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
            .ToDictionaryAsync(m => m.Id, ct);

        var cards = items
            .Select(i => withRelations.TryGetValue(i.Model.Id, out var full)
                ? i with { Model = full }
                : i)
            .ToList();

        return new PagedResult<ModelCard>(cards, total, page, query.PageSize);
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
            .Include(m => m.Collections.Where(c => c.OwnerId == userId))
            .FirstOrDefaultAsync(m => m.Id == id, ct);

        if (model is null) return null;

        var isFavorite = await db.Favorites
            .AnyAsync(f => f.ModelEntryId == id && f.UserId == userId, ct);

        return new ModelCard(model, isFavorite);
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
        var userId = user.UserId;
        var rows = await db.Collections
            .AsNoTracking()
            .Where(c => c.OwnerId == userId)
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
