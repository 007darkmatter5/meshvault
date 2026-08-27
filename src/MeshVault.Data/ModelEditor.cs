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

        // Favouriting a sculpt favourites every export of it: the starred card
        // in Browse is the group, and a half-favourited group would show as
        // starred or not depending on which member happened to be primary.
        var ids = await GroupStore.MemberIdsAsync(db, modelId, ct);

        var existing = await db.Favorites
            .Where(f => ids.Contains(f.ModelEntryId) && f.UserId == userId)
            .ToListAsync(ct);

        if (existing.Count > 0)
        {
            db.Favorites.RemoveRange(existing);
            await db.SaveChangesAsync(ct);
            return false;
        }

        foreach (var id in ids)
        {
            db.Favorites.Add(new ModelFavorite
            {
                ModelEntryId = id,
                UserId = userId,
                CreatedUtc = DateTimeOffset.UtcNow,
            });
        }

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

    // Variants --------------------------------------------------------------

    /// <summary>
    /// Says by hand which sculpt a file is an export of, and which flavour.
    /// </summary>
    /// <remarks>
    /// Marks the file as set by the user, so neither a rescan nor a change to
    /// the variant vocabulary undoes it. This is the escape hatch for the cases
    /// no vocabulary can reach: a creator's typo that splits one sculpt in two,
    /// or a mini genuinely called "Hollow Knight" whose name got eaten.
    ///
    /// The sculpt key is normalised the same way the classifier does it, so a
    /// file moved here lands in the same group as one that was detected.
    /// </remarks>
    public async Task SetVariantAsync(
        int fileId, string sculptName, IEnumerable<VariantDefinition> labels,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sculptName)) return;

        var chosen = labels.OrderBy(d => d.PreviewRank).ToList();
        var name = sculptName.Trim();
        var key = VariantClassifier.NormalizeKey(name);
        var label = chosen.Count == 0 ? null : string.Join(", ", chosen.Select(d => d.Name));
        var rank = chosen.Sum(d => d.PreviewRank);

        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Files.Where(f => f.Id == fileId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.SculptKey, key)
                .SetProperty(f => f.SculptName, name)
                .SetProperty(f => f.VariantLabel, label)
                .SetProperty(f => f.VariantRank, rank)
                .SetProperty(f => f.VariantSetByUser, true), ct);
    }

    /// <summary>
    /// Drops a hand-set sculpt and variant, handing the file back to detection.
    /// Takes effect on the next pass, which the caller runs.
    /// </summary>
    public async Task ResetVariantAsync(int fileId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Files.Where(f => f.Id == fileId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.VariantSetByUser, false), ct);
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

        // A tag describes the sculpt, not the export, so it lands on every
        // folder the group covers rather than on whichever one was open.
        var ids = await GroupStore.MemberIdsAsync(db, modelId, ct);
        var models = await db.Models.Include(m => m.Tags)
            .Where(m => ids.Contains(m.Id)).ToListAsync(ct);
        if (models.Count == 0) return null;

        var tag = await db.Tags.FirstOrDefaultAsync(t => t.NormalizedName == normalized, ct)
            ?? new Tag { Name = name, NormalizedName = normalized };

        foreach (var model in models)
            if (model.Tags.All(t => t.NormalizedName != normalized))
                model.Tags.Add(tag);

        await db.SaveChangesAsync(ct);
        return tag;
    }

    public async Task RemoveTagAsync(int modelId, int tagId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var ids = await GroupStore.MemberIdsAsync(db, modelId, ct);
        var models = await db.Models.Include(m => m.Tags)
            .Where(m => ids.Contains(m.Id)).ToListAsync(ct);

        var removed = false;
        foreach (var model in models)
        {
            if (model.Tags.FirstOrDefault(t => t.Id == tagId) is not { } tag) continue;
            model.Tags.Remove(tag);
            removed = true;
        }

        if (!removed) return;
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

        // Whole group in or whole group out, for the same reason as favourites:
        // the collection lists one card per group, so partial membership would
        // show or hide it depending on which member is primary.
        var ids = await GroupStore.MemberIdsAsync(db, modelId, ct);
        var changed = false;

        if (member)
        {
            var missing = ids.Where(id => collection.Models.All(m => m.Id != id)).ToList();
            if (missing.Count == 0) return;

            foreach (var model in await db.Models.Where(m => missing.Contains(m.Id)).ToListAsync(ct))
            {
                collection.Models.Add(model);
                changed = true;
            }
        }
        else
        {
            foreach (var model in collection.Models.Where(m => ids.Contains(m.Id)).ToList())
            {
                collection.Models.Remove(model);
                changed = true;
            }
        }

        if (!changed) return;
        await db.SaveChangesAsync(ct);
    }

    // Bulk editing ----------------------------------------------------------

    /// <summary>
    /// Applies one edit to many models at once.
    /// </summary>
    /// <remarks>
    /// Deliberately not a loop over the single-model methods. Each of those
    /// opens its own context and round-trips the database; over a few hundred
    /// models that is thousands of queries against SQLite on a home server.
    /// This resolves each tag and designer once and works in sets.
    ///
    /// An id that no longer exists is skipped rather than failing the edit. A
    /// selection made before a rescan is expected to go slightly stale, and
    /// quietly doing the rest is what was asked for.
    /// </remarks>
    public async Task<BulkEditResult> ApplyBulkEditAsync(
        IReadOnlyCollection<int> modelIds, BulkEdit edit, CancellationToken ct = default)
    {
        if (modelIds.Count == 0 || edit.IsEmpty) return new BulkEditResult(0, 0, 0, 0, 0, 0);

        var ids = modelIds.Distinct().ToList();
        await using var db = await factory.CreateDbContextAsync(ct);
        var userId = user.UserId;

        var designerChanged = 0;
        var tagsAdded = 0;
        var tagsRemoved = 0;
        var collectionChanged = 0;
        var favoritesChanged = 0;

        // Designer ----------------------------------------------------------
        if (edit.ClearDesigner)
        {
            designerChanged = await db.Models
                .Where(m => ids.Contains(m.Id) && m.DesignerId != null)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DesignerId, (int?)null), ct);
        }
        else if (!string.IsNullOrWhiteSpace(edit.DesignerName))
        {
            var designer = await GetOrCreateDesignerAsync(db, edit.DesignerName, ct);
            designerChanged = await db.Models
                .Where(m => ids.Contains(m.Id) && m.DesignerId != designer.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DesignerId, designer.Id), ct);
        }

        // Tags --------------------------------------------------------------
        if (edit.TagsToAdd.Count > 0 || edit.TagsToRemove.Count > 0)
        {
            var models = await db.Models
                .Include(m => m.Tags)
                .Where(m => ids.Contains(m.Id))
                .ToListAsync(ct);

            foreach (var name in Normalize(edit.TagsToAdd))
            {
                var tag = await GetOrCreateTagAsync(db, name, ct);
                foreach (var model in models.Where(m => m.Tags.All(t => t.Id != tag.Id)))
                {
                    model.Tags.Add(tag);
                    tagsAdded++;
                }
            }

            var removing = Normalize(edit.TagsToRemove)
                .Select(n => n.ToLowerInvariant())
                .ToHashSet();

            foreach (var model in models)
            {
                foreach (var tag in model.Tags.Where(t => removing.Contains(t.NormalizedName)).ToList())
                {
                    model.Tags.Remove(tag);
                    tagsRemoved++;
                }
            }

            await db.SaveChangesAsync(ct);

            // A tag that no longer labels anything would otherwise linger in
            // the filter sidebar, which removing one at a time already avoids.
            if (tagsRemoved > 0)
            {
                await db.Tags
                    .Where(t => removing.Contains(t.NormalizedName) && !t.Models.Any())
                    .ExecuteDeleteAsync(ct);
            }
        }

        // Collection --------------------------------------------------------
        if (edit.CollectionId is { } collectionId)
        {
            var collection = await db.Collections
                .Include(c => c.Models)
                .FirstOrDefaultAsync(c => c.Id == collectionId && c.OwnerId == userId, ct);

            if (collection is not null)
            {
                if (edit.RemoveFromCollection)
                {
                    foreach (var model in collection.Models.Where(m => ids.Contains(m.Id)).ToList())
                    {
                        collection.Models.Remove(model);
                        collectionChanged++;
                    }
                }
                else
                {
                    var present = collection.Models.Select(m => m.Id).ToHashSet();
                    var missing = await db.Models
                        .Where(m => ids.Contains(m.Id) && !present.Contains(m.Id))
                        .ToListAsync(ct);

                    foreach (var model in missing)
                    {
                        collection.Models.Add(model);
                        collectionChanged++;
                    }
                }

                await db.SaveChangesAsync(ct);
            }
        }

        // Favorites ---------------------------------------------------------
        if (edit.Favorite is { } favorite)
        {
            if (favorite)
            {
                var already = await db.Favorites
                    .Where(f => f.UserId == userId && ids.Contains(f.ModelEntryId))
                    .Select(f => f.ModelEntryId)
                    .ToListAsync(ct);

                // Only for models that still exist. A favorite pointing at a
                // model that has gone is unreachable and never cleaned up.
                var existing = await db.Models
                    .Where(m => ids.Contains(m.Id) && !already.Contains(m.Id))
                    .Select(m => m.Id)
                    .ToListAsync(ct);

                foreach (var modelId in existing)
                {
                    db.Favorites.Add(new ModelFavorite
                    {
                        ModelEntryId = modelId,
                        UserId = userId,
                        CreatedUtc = DateTimeOffset.UtcNow,
                    });
                    favoritesChanged++;
                }

                await db.SaveChangesAsync(ct);
            }
            else
            {
                favoritesChanged = await db.Favorites
                    .Where(f => f.UserId == userId && ids.Contains(f.ModelEntryId))
                    .ExecuteDeleteAsync(ct);
            }
        }

        return new BulkEditResult(
            ids.Count, designerChanged, tagsAdded, tagsRemoved, collectionChanged, favoritesChanged);
    }

    /// <summary>Trimmed, de-duplicated case-insensitively, blanks dropped.</summary>
    private static List<string> Normalize(IReadOnlyList<string> names) =>
        names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .DistinctBy(n => n.ToLowerInvariant())
            .ToList();

    private static async Task<Designer> GetOrCreateDesignerAsync(
        MeshVaultDbContext db, string name, CancellationToken ct)
    {
        var trimmed = name.Trim();
        var normalized = trimmed.ToLowerInvariant();

        var designer = await db.Designers.FirstOrDefaultAsync(d => d.NormalizedName == normalized, ct);
        if (designer is not null) return designer;

        designer = new Designer
        {
            Name = trimmed,
            NormalizedName = normalized,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        db.Designers.Add(designer);
        await db.SaveChangesAsync(ct);
        return designer;
    }

    private static async Task<Tag> GetOrCreateTagAsync(
        MeshVaultDbContext db, string name, CancellationToken ct)
    {
        var normalized = name.ToLowerInvariant();

        var tag = await db.Tags.FirstOrDefaultAsync(t => t.NormalizedName == normalized, ct);
        if (tag is not null) return tag;

        tag = new Tag { Name = name, NormalizedName = normalized };
        db.Tags.Add(tag);
        await db.SaveChangesAsync(ct);
        return tag;
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

    /// <summary>
    /// Renames a library and changes whether MeshVault may organise it. The path
    /// stays fixed: every model is recorded relative to it, so moving the root
    /// would orphan the whole library rather than follow it.
    /// </summary>
    public async Task UpdateLibraryAsync(int libraryId, string name, bool allowOrganize,
        string? inboxPath = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var library = await db.Libraries.FirstOrDefaultAsync(l => l.Id == libraryId, ct);
        if (library is null) return;

        // An empty name would leave an unidentifiable row, so fall back to the
        // folder name exactly as adding one does.
        library.Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(library.Path) : name.Trim();
        library.AllowOrganize = allowOrganize;

        // Stored in the form paths are compared in, so "/Inbox/" and "inbox"
        // are the same folder rather than one of them silently matching nothing.
        var inbox = Inbox.Normalize(inboxPath);
        library.InboxPath = inbox.Length == 0 ? null : inbox;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Remembers how this library should be laid out, so the templates are
    /// there next time rather than starting from the defaults again.
    /// </summary>
    /// <remarks>
    /// Saving a preference is not applying it. Nothing moves here — the rules
    /// only decide what the next plan proposes.
    /// </remarks>
    public async Task SetOrganizeRulesAsync(
        int libraryId, OrganizeRules rules, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        await db.Libraries.Where(l => l.Id == libraryId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.FolderTemplate, rules.FolderTemplate)
                .SetProperty(l => l.FileTemplate, rules.FileTemplate)
                .SetProperty(l => l.RenameFiles, rules.RenameFiles)
                .SetProperty(l => l.FolderCase, rules.FolderCase)
                .SetProperty(l => l.FileCase, rules.FileCase), ct);
    }

    public async Task RemoveLibraryAsync(int libraryId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Libraries.Where(l => l.Id == libraryId).ExecuteDeleteAsync(ct);
    }
}
