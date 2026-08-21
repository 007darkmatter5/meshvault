using MeshVault.Core.Models;
using MeshVault.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Data;

/// <summary>Write-side operations for user-owned metadata.</summary>
public class ModelEditor(IDbContextFactory<MeshVaultDbContext> factory, ICurrentUser user)
{
    // Favorites -------------------------------------------------------------

    public async Task<bool> ToggleFavoriteAsync(int modelId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var userId = user.UserId;

        var existing = await db.Favorites
            .FirstOrDefaultAsync(f => f.ModelEntryId == modelId && f.UserId == userId, ct);

        if (existing is not null)
        {
            db.Favorites.Remove(existing);
            await db.SaveChangesAsync(ct);
            return false;
        }

        db.Favorites.Add(new ModelFavorite
        {
            ModelEntryId = modelId,
            UserId = userId,
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    // Free text -------------------------------------------------------------

    public async Task SetNotesAsync(int modelId, string? notes, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Models.Where(m => m.Id == modelId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Notes, notes), ct);
    }

    public async Task SetDescriptionAsync(int modelId, string? description, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Models.Where(m => m.Id == modelId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Description, description), ct);
    }

    /// <summary>
    /// Renames a model and marks the name as user-chosen, so imports and
    /// rescans will not overwrite it.
    /// </summary>
    public async Task RenameAsync(int modelId, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        var trimmed = name.Trim();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Models.Where(m => m.Id == modelId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Name, trimmed)
                .SetProperty(m => m.NameSetByUser, true), ct);
    }

    /// <summary>
    /// Drops a hand-chosen name, returning the model to its folder name and
    /// letting imports set it again.
    /// </summary>
    public async Task ResetNameAsync(int modelId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var model = await db.Models.FirstOrDefaultAsync(m => m.Id == modelId, ct);
        if (model is null) return;

        var folder = model.RelativePath.Split('/').LastOrDefault();
        model.Name = string.IsNullOrWhiteSpace(folder) ? model.Name : folder;
        model.NameSetByUser = false;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetLicenseAsync(int modelId, string? license, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var value = string.IsNullOrWhiteSpace(license) ? null : license.Trim();
        await db.Models.Where(m => m.Id == modelId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.License, value), ct);
    }

    // Source ----------------------------------------------------------------

    /// <summary>
    /// Stores a source link and the site it came from. Returns false when the
    /// text is not a usable http(s) URL, so the UI can say so rather than
    /// silently keeping junk.
    /// </summary>
    public async Task<bool> SetSourceUrlAsync(int modelId, string? url, CancellationToken ct = default)
    {
        string? normalized = null;
        string? site = null;

        if (!string.IsNullOrWhiteSpace(url))
        {
            normalized = SourceSites.Normalize(url);
            if (normalized is null) return false;
            site = SourceSites.Detect(normalized);
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Models.Where(m => m.Id == modelId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.SourceUrl, normalized)
                .SetProperty(m => m.SourceSite, site), ct);
        return true;
    }

    // Tags ------------------------------------------------------------------

    /// <summary>Adds a tag, reusing an existing one when the name matches case-insensitively.</summary>
    public async Task<Tag?> AddTagAsync(int modelId, string tagName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return null;

        var name = tagName.Trim();
        var normalized = name.ToLowerInvariant();

        await using var db = await factory.CreateDbContextAsync(ct);
        var model = await db.Models.Include(m => m.Tags).FirstOrDefaultAsync(m => m.Id == modelId, ct);
        if (model is null) return null;

        var already = model.Tags.FirstOrDefault(t => t.NormalizedName == normalized);
        if (already is not null) return already;

        var tag = await db.Tags.FirstOrDefaultAsync(t => t.NormalizedName == normalized, ct)
            ?? new Tag { Name = name, NormalizedName = normalized };

        model.Tags.Add(tag);
        await db.SaveChangesAsync(ct);
        return tag;
    }

    public async Task RemoveTagAsync(int modelId, int tagId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var model = await db.Models.Include(m => m.Tags).FirstOrDefaultAsync(m => m.Id == modelId, ct);
        var tag = model?.Tags.FirstOrDefault(t => t.Id == tagId);
        if (model is null || tag is null) return;

        model.Tags.Remove(tag);
        await db.SaveChangesAsync(ct);

        // Drop tags that no longer label anything, so the filter list stays clean.
        if (!await db.Models.AnyAsync(m => m.Tags.Any(t => t.Id == tagId), ct))
            await db.Tags.Where(t => t.Id == tagId).ExecuteDeleteAsync(ct);
    }

    public async Task<List<Tag>> SuggestTagsAsync(string prefix, int limit = 10, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var term = (prefix ?? "").Trim().ToLowerInvariant();
        return await db.Tags.AsNoTracking()
            .Where(t => term == "" || EF.Functions.Like(t.NormalizedName, $"{term}%"))
            .OrderByDescending(t => t.Models.Count).ThenBy(t => t.Name)
            .Take(limit)
            .ToListAsync(ct);
    }

    // Designers -------------------------------------------------------------

    /// <summary>Assigns a designer by name, creating them if new. Blank clears it.</summary>
    public async Task<Designer?> SetDesignerAsync(int modelId, string? designerName, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        if (string.IsNullOrWhiteSpace(designerName))
        {
            await db.Models.Where(m => m.Id == modelId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DesignerId, (int?)null), ct);
            return null;
        }

        var name = designerName.Trim();
        var normalized = name.ToLowerInvariant();

        var designer = await db.Designers.FirstOrDefaultAsync(d => d.NormalizedName == normalized, ct);
        if (designer is null)
        {
            designer = new Designer
            {
                Name = name,
                NormalizedName = normalized,
                CreatedUtc = DateTimeOffset.UtcNow,
            };
            db.Designers.Add(designer);
            await db.SaveChangesAsync(ct);
        }

        await db.Models.Where(m => m.Id == modelId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.DesignerId, designer.Id), ct);
        return designer;
    }

    /// <summary>
    /// Creates a designer directly, rather than as a side effect of assigning
    /// one to a model, so a profile can be set up before any models arrive.
    /// </summary>
    public async Task<Designer> CreateDesignerAsync(string name, string? profileUrl = null,
        string? notes = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A designer needs a name.", nameof(name));

        var trimmed = name.Trim();
        var normalized = trimmed.ToLowerInvariant();

        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Designers.AnyAsync(d => d.NormalizedName == normalized, ct))
            throw new InvalidOperationException($"There is already a designer called \"{trimmed}\".");

        var designer = new Designer
        {
            Name = trimmed,
            NormalizedName = normalized,
            ProfileUrl = SourceSites.Normalize(profileUrl),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        db.Designers.Add(designer);
        await db.SaveChangesAsync(ct);
        return designer;
    }

    public async Task UpdateDesignerAsync(int designerId, string name, string? profileUrl, string? notes,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        await using var db = await factory.CreateDbContextAsync(ct);
        var designer = await db.Designers.FirstOrDefaultAsync(d => d.Id == designerId, ct);
        if (designer is null) return;

        designer.Name = name.Trim();
        designer.NormalizedName = designer.Name.ToLowerInvariant();
        designer.ProfileUrl = SourceSites.Normalize(profileUrl);
        designer.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes a designer; their models keep everything else and simply lose the link.</summary>
    public async Task DeleteDesignerAsync(int designerId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Designers.Where(d => d.Id == designerId).ExecuteDeleteAsync(ct);
    }

    public async Task<List<Designer>> SuggestDesignersAsync(string prefix, int limit = 10,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var term = (prefix ?? "").Trim().ToLowerInvariant();
        return await db.Designers.AsNoTracking()
            .Where(d => term == "" || EF.Functions.Like(d.NormalizedName, $"%{term}%"))
            .OrderByDescending(d => d.Models.Count).ThenBy(d => d.Name)
            .Take(limit)
            .ToListAsync(ct);
    }

    // Collections -----------------------------------------------------------

    public async Task<Collection> CreateCollectionAsync(string name, string? description = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A collection needs a name.", nameof(name));

        await using var db = await factory.CreateDbContextAsync(ct);
        var userId = user.UserId;
        var trimmed = name.Trim();
        var normalized = trimmed.ToLowerInvariant();

        if (await db.Collections.AnyAsync(c => c.OwnerId == userId && c.NormalizedName == normalized, ct))
            throw new InvalidOperationException($"You already have a collection called \"{trimmed}\".");

        var collection = new Collection
        {
            Name = trimmed,
            NormalizedName = normalized,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            OwnerId = userId,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        db.Collections.Add(collection);
        await db.SaveChangesAsync(ct);
        return collection;
    }

    public async Task RenameCollectionAsync(int collectionId, string name, string? description,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        await using var db = await factory.CreateDbContextAsync(ct);
        var userId = user.UserId;
        var collection = await db.Collections
            .FirstOrDefaultAsync(c => c.Id == collectionId && c.OwnerId == userId, ct);
        if (collection is null) return;

        var trimmed = name.Trim();
        var normalized = trimmed.ToLowerInvariant();

        if (normalized != collection.NormalizedName
            && await db.Collections.AnyAsync(
                c => c.OwnerId == userId && c.NormalizedName == normalized && c.Id != collectionId, ct))
            throw new InvalidOperationException($"You already have a collection called \"{trimmed}\".");

        collection.Name = trimmed;
        collection.NormalizedName = normalized;
        collection.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteCollectionAsync(int collectionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var userId = user.UserId;
        await db.Collections
            .Where(c => c.Id == collectionId && c.OwnerId == userId)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>Adds or removes a model from one of the current user's collections.</summary>
    public async Task SetCollectionMembershipAsync(int modelId, int collectionId, bool member,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var userId = user.UserId;

        var collection = await db.Collections
            .Include(c => c.Models)
            .FirstOrDefaultAsync(c => c.Id == collectionId && c.OwnerId == userId, ct);
        if (collection is null) return;

        var already = collection.Models.FirstOrDefault(m => m.Id == modelId);

        if (member && already is null)
        {
            var model = await db.Models.FirstOrDefaultAsync(m => m.Id == modelId, ct);
            if (model is null) return;
            collection.Models.Add(model);
        }
        else if (!member && already is not null)
        {
            collection.Models.Remove(already);
        }
        else
        {
            return;
        }

        await db.SaveChangesAsync(ct);
    }

    // Libraries -------------------------------------------------------------

    public async Task<Library> AddLibraryAsync(string name, string path, bool allowOrganize,
        CancellationToken ct = default)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"Folder not found: {full}");

        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Libraries.AnyAsync(l => l.Path == full, ct))
            throw new InvalidOperationException("That folder is already a library.");

        var library = new Library
        {
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(full) : name.Trim(),
            Path = full,
            AllowOrganize = allowOrganize,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync(ct);
        return library;
    }

    public async Task RemoveLibraryAsync(int libraryId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Libraries.Where(l => l.Id == libraryId).ExecuteDeleteAsync(ct);
    }
}
