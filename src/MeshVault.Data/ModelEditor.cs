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
    public Task SetVariantAsync(
        int fileId, string sculptName, IEnumerable<VariantDefinition> labels,
        CancellationToken ct = default) =>
        SetSculptAsync([fileId], sculptName, labels, ct);

    /// <summary>
    /// Says by hand which sculpt some files are exports of, and which flavour.
    /// Returns how many files were changed.
    /// </summary>
    /// <remarks>
    /// Naming several files at once is what moving a stray export into the
    /// right sculpt actually is, and doing it a file at a time meant retyping
    /// the same name identically or quietly creating a second sculpt one letter
    /// different from the first.
    /// </remarks>
    public async Task<int> SetSculptAsync(
        IEnumerable<int> fileIds, string sculptName, IEnumerable<VariantDefinition> labels,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sculptName)) return 0;

        var ids = fileIds.ToList();
        if (ids.Count == 0) return 0;

        // Alphabetical, matching what detection produces, so a file corrected by
        // hand and one read from its name render the same way in a template.
        var chosen = labels.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var name = sculptName.Trim();
        var key = VariantClassifier.NormalizeKey(name);
        var label = chosen.Count == 0 ? null : string.Join(", ", chosen.Select(d => d.Name));
        var rank = chosen.Sum(d => d.PreviewRank);

        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Files.Where(f => ids.Contains(f.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.SculptKey, key)
                .SetProperty(f => f.SculptName, name)
                .SetProperty(f => f.VariantLabel, label)
                .SetProperty(f => f.VariantRank, rank)
                .SetProperty(f => f.VariantSetByUser, true)

                // A hand-set sculpt outranks the pin organizing leaves behind,
                // so clearing it keeps one flag meaning one thing: this row was
                // decided by a person.
                .SetProperty(f => f.VariantSetByOrganize, false), ct);
    }

    /// <summary>
    /// Renames a sculpt everywhere it appears in a model and its group.
    /// Returns how many files were changed.
    /// </summary>
    /// <remarks>
    /// The operation that was missing entirely: a sculpt with six exports could
    /// only be renamed by editing six files and typing the same name into each,
    /// where one slip left two sculpts a letter apart and no way to see why they
    /// no longer grouped.
    ///
    /// Renaming onto a name another sculpt already has **merges** the two -- the
    /// key is what groups, and two files carrying one key are one sculpt. That
    /// is the same operation seen from the other end rather than a special case,
    /// so there is no separate merge to get subtly different.
    /// </remarks>
    public async Task<int> RenameSculptAsync(
        int modelId, string sculptKey, string newName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sculptKey) || string.IsNullOrWhiteSpace(newName)) return 0;

        var name = newName.Trim();
        var key = VariantClassifier.NormalizeKey(name);

        await using var db = await factory.CreateDbContextAsync(ct);

        // The whole group, because the page that offers this shows the group as
        // one thing -- renaming a sculpt on screen and leaving the same sculpt
        // named differently in a sibling folder would split the group in two.
        var models = await GroupStore.MemberIdsAsync(db, modelId, ct);

        return await db.Files
            .Where(f => models.Contains(f.ModelEntryId) && f.SculptKey == sculptKey)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.SculptKey, key)
                .SetProperty(f => f.SculptName, name)
                .SetProperty(f => f.VariantSetByUser, true)
                .SetProperty(f => f.VariantSetByOrganize, false), ct);
    }

    /// <summary>
    /// Sets which variants some files are, leaving the sculpt each belongs to
    /// alone. Returns how many were changed.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="SetSculptAsync"/> because the two are
    /// different questions asked of the same rows: which mini this is, and which
    /// cut of it. Marking four files supported in one go must not quietly move
    /// them all into one sculpt.
    /// </remarks>
    public async Task<int> SetVariantsAsync(
        IEnumerable<int> fileIds, IEnumerable<VariantDefinition> labels,
        CancellationToken ct = default)
    {
        var ids = fileIds.ToList();
        if (ids.Count == 0) return 0;

        var chosen = labels.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var label = chosen.Count == 0 ? null : string.Join(", ", chosen.Select(d => d.Name));
        var rank = chosen.Sum(d => d.PreviewRank);

        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Files.Where(f => ids.Contains(f.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.VariantLabel, label)
                .SetProperty(f => f.VariantRank, rank)
                .SetProperty(f => f.VariantSetByUser, true)
                .SetProperty(f => f.VariantSetByOrganize, false), ct);
    }

    /// <summary>
    /// Drops a hand-set sculpt and variant, handing the file back to detection.
    /// Takes effect on the next pass, which the caller runs.
    /// </summary>
    /// <remarks>
    /// Clears the organize pin as well. Asking for detection back and being
    /// handed the values organizing froze would be the same answer under a
    /// different name -- and where the two disagree, the person asking is the
    /// one who gets to be right.
    /// </remarks>
    public async Task ResetVariantAsync(int fileId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Files.Where(f => f.Id == fileId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.VariantSetByUser, false)
                .SetProperty(f => f.VariantSetByOrganize, false), ct);
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

    /// <summary>
    /// Designers whose name contains <paramref name="prefix"/>, or all of them
    /// when it is blank.
    /// </summary>
    /// <param name="limit">A ceiling on the results, or null for all of them.</param>
    /// <remarks>
    /// Uncapped by default, and alphabetical. It used to hand back the ten with
    /// the most models, which made opening the picker show an arbitrary-looking
    /// ten and hid everyone else until you typed -- and typing is exactly what
    /// creates a designer by accident. A list you can scan is the fix for both:
    /// there is no reason to type at all when the name is already on screen.
    ///
    /// Alphabetical because the list is now complete. Ordering by model count
    /// is a good answer to "who matters most" and a poor one to "where is
    /// Cinderwing3D in this list".
    /// </remarks>
    public async Task<List<Designer>> SuggestDesignersAsync(string prefix, int? limit = null,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var term = (prefix ?? "").Trim().ToLowerInvariant();

        IQueryable<Designer> query = db.Designers.AsNoTracking()
            .Where(d => term == "" || EF.Functions.Like(d.NormalizedName, $"%{term}%"))
            .OrderBy(d => d.Name);

        if (limit is int cap) query = query.Take(cap);

        return await query.ToListAsync(ct);
    }

    /// <summary>Whether a designer of this name already exists.</summary>
    /// <remarks>
    /// Asked before creating one, so that half a name typed while searching can
    /// be questioned rather than quietly turned into a designer called "Ci".
    /// Matched on the normalised name, the same way
    /// <see cref="SetDesignerAsync"/> decides whether to create.
    /// </remarks>
    public async Task<bool> DesignerExistsAsync(string? name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        await using var db = await factory.CreateDbContextAsync(ct);
        var normalized = name.Trim().ToLowerInvariant();
        return await db.Designers.AnyAsync(d => d.NormalizedName == normalized, ct);
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

        if (await db.Collections.AnyAsync(c => c.NormalizedName == normalized, ct))
            throw new InvalidOperationException($"There is already a collection called \"{trimmed}\".");

        var collection = new Collection
        {
            Name = trimmed,
            NormalizedName = normalized,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
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
            .FirstOrDefaultAsync(c => c.Id == collectionId, ct);
        if (collection is null) return;

        var trimmed = name.Trim();
        var normalized = trimmed.ToLowerInvariant();

        if (normalized != collection.NormalizedName
            && await db.Collections.AnyAsync(
                c => c.NormalizedName == normalized && c.Id != collectionId, ct))
            throw new InvalidOperationException($"There is already a collection called \"{trimmed}\".");

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
            .Where(c => c.Id == collectionId)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>Adds or removes a model from a collection.</summary>
    public async Task SetCollectionMembershipAsync(int modelId, int collectionId, bool member,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var collection = await db.Collections.FirstOrDefaultAsync(c => c.Id == collectionId, ct);
        if (collection is null) return;

        // Whole group in or whole group out, for the same reason as favourites:
        // the collection lists one card per group, so partial membership would
        // show or hide it depending on which member is primary.
        var ids = await GroupStore.MemberIdsAsync(db, modelId, ct);

        // Worked from the model's side rather than the collection's, and with
        // its memberships loaded, because the star that decides which
        // collection names its folder has to be settled against them.
        var models = await db.Models
            .Include(m => m.Collections)
            .Where(m => ids.Contains(m.Id))
            .ToListAsync(ct);

        var changed = false;
        foreach (var model in models)
        {
            if (member == model.Collections.Any(c => c.Id == collectionId)) continue;

            // Read before the memberships move underneath it: this is the
            // collection that was naming the folder a moment ago.
            var was = model.PrimaryCollection;

            if (member) model.Collections.Add(collection);
            else model.Collections.Remove(model.Collections.First(c => c.Id == collectionId));

            SettleStar(model, was);
            changed = true;
        }

        if (!changed) return;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Keeps the star that names a model's folder honest across a change of
    /// membership.
    /// </summary>
    /// <remarks>
    /// A model in exactly one collection needs no star: that collection is
    /// implicitly primary, and storing it would be another value to keep in
    /// step for no gain. The moment a second arrives, that implicit choice has
    /// to be written down -- otherwise the model has two collections and no
    /// star, the folder level collapses, and adding a model to a second
    /// collection would quietly un-file it on the next organize.
    ///
    /// Leaving the starred collection clears the star rather than moving it.
    /// Which of the survivors should name the folder is not something to guess
    /// at, and a model with no star files without the level at all -- a shape
    /// somebody can see on the page and correct, rather than a choice made for
    /// them.
    /// </remarks>
    private static void SettleStar(ModelEntry model, Collection? was)
    {
        if (model.Collections.Count <= 1)
        {
            model.PrimaryCollectionId = null;
            return;
        }

        if (model.Collections.Any(c => c.Id == model.PrimaryCollectionId)) return;

        model.PrimaryCollectionId =
            was is not null && model.Collections.Any(c => c.Id == was.Id) ? was.Id : null;
    }

    /// <summary>
    /// Stars the collection that names this model's folder, or clears it.
    /// </summary>
    /// <remarks>
    /// Only a collection the model is actually in. A star pointing outside the
    /// memberships would name a folder the model has no other claim to, and
    /// <see cref="ModelEntry.PrimaryCollection"/> would ignore it anyway --
    /// leaving the page showing a choice that does nothing.
    /// </remarks>
    public async Task SetPrimaryCollectionAsync(int modelId, int? collectionId,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var ids = await GroupStore.MemberIdsAsync(db, modelId, ct);
        var models = await db.Models
            .Include(m => m.Collections)
            .Where(m => ids.Contains(m.Id))
            .ToListAsync(ct);

        foreach (var model in models)
        {
            model.PrimaryCollectionId =
                collectionId is { } id && model.Collections.Any(c => c.Id == id) ? id : null;
        }

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
            var collection = await db.Collections.FirstOrDefaultAsync(c => c.Id == collectionId, ct);

            if (collection is not null)
            {
                // From the model's side, with memberships loaded, so the star
                // that names each folder is settled the same way a single edit
                // settles it. Doing it per collection instead would leave a
                // few hundred models with two collections and no star, and the
                // next organize would un-file every one of them.
                var affected = await db.Models
                    .Include(m => m.Collections)
                    .Where(m => ids.Contains(m.Id))
                    .ToListAsync(ct);

                foreach (var model in affected)
                {
                    var member = !edit.RemoveFromCollection;
                    if (member == model.Collections.Any(c => c.Id == collectionId)) continue;

                    var was = model.PrimaryCollection;

                    if (member) model.Collections.Add(collection);
                    else model.Collections.Remove(model.Collections.First(c => c.Id == collectionId));

                    SettleStar(model, was);
                    collectionChanged++;
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
