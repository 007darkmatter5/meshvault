using MeshVault.Core.Models;
using MeshVault.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Data;

/// <summary>A scheme, with whether the reader could paint it from their own rack.</summary>
public record SchemeView(PaintScheme Scheme, bool IsMine, IReadOnlyList<string> Missing)
{
    /// <summary>Nothing in the recipe is absent from the reader's shelf.</summary>
    public bool CanPaint => Missing.Count == 0;
}

/// <summary>
/// Paint racks and painting schemes.
/// </summary>
/// <remarks>
/// A rack is private: it is a shelf somebody owns. A scheme is owned but
/// readable by everyone, so one model can carry several recipes and you can see
/// how someone else painted it. That asymmetry is the whole point - reading
/// another person's scheme against your own rack is what answers "what would I
/// have to buy".
/// </remarks>
public class PaintStore(IDbContextFactory<MeshVaultDbContext> factory, ICurrentUser user)
{
    // Racks -------------------------------------------------------------------

    public async Task<List<Paint>> GetRackAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var owner = user.UserId;

        return await db.Paints.AsNoTracking()
            .Where(p => p.OwnerId == owner)
            .OrderBy(p => p.Brand).ThenBy(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task<Paint?> AddPaintAsync(Paint paint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(paint.Name)) return null;

        await using var db = await factory.CreateDbContextAsync(ct);
        var owner = user.UserId;
        var normalized = paint.Name.Trim().ToLowerInvariant();

        var existing = await db.Paints
            .FirstOrDefaultAsync(p => p.OwnerId == owner && p.NormalizedName == normalized, ct);
        if (existing is not null) return existing;

        var added = new Paint
        {
            OwnerId = owner,
            Name = paint.Name.Trim(),
            NormalizedName = normalized,
            Brand = Blank(paint.Brand),
            Range = Blank(paint.Range),
            Hex = Blank(paint.Hex),
            Finish = paint.Finish,
            Stock = paint.Stock,
            Quantity = Math.Max(0, paint.Quantity),
            Notes = Blank(paint.Notes),
            AddedUtc = DateTimeOffset.UtcNow,
        };

        db.Paints.Add(added);
        await db.SaveChangesAsync(ct);
        return added;
    }

    public async Task UpdatePaintAsync(int paintId, Paint changes, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var owner = user.UserId;

        var paint = await db.Paints.FirstOrDefaultAsync(p => p.Id == paintId && p.OwnerId == owner, ct);
        if (paint is null || string.IsNullOrWhiteSpace(changes.Name)) return;

        paint.Name = changes.Name.Trim();
        paint.NormalizedName = paint.Name.ToLowerInvariant();
        paint.Brand = Blank(changes.Brand);
        paint.Range = Blank(changes.Range);
        paint.Hex = Blank(changes.Hex);
        paint.Finish = changes.Finish;
        paint.Stock = changes.Stock;
        paint.Quantity = Math.Max(0, changes.Quantity);
        paint.Notes = Blank(changes.Notes);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Sets how much is left, which is the edit made most often.</summary>
    public async Task SetStockAsync(int paintId, PaintStock stock, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var owner = user.UserId;

        await db.Paints
            .Where(p => p.Id == paintId && p.OwnerId == owner)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, stock), ct);
    }

    /// <summary>
    /// Sets how many bottles there are. Buying a second one is a one-click edit
    /// rather than a trip through the whole form.
    /// </summary>
    public async Task SetQuantityAsync(int paintId, int quantity, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var owner = user.UserId;
        var clamped = Math.Clamp(quantity, 0, 999);

        await db.Paints
            .Where(p => p.Id == paintId && p.OwnerId == owner)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Quantity, clamped), ct);
    }

    /// <summary>
    /// Removes a bottle from the rack. Schemes that used it keep the step, because
    /// running out of a paint does not un-paint the model.
    /// </summary>
    public async Task DeletePaintAsync(int paintId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var owner = user.UserId;

        await db.Paints.Where(p => p.Id == paintId && p.OwnerId == owner).ExecuteDeleteAsync(ct);
    }

    /// <summary>Paints on the reader's own rack whose name starts with the term.</summary>
    public async Task<List<Paint>> SuggestAsync(string prefix, int limit = 10, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var owner = user.UserId;
        var term = (prefix ?? "").Trim().ToLowerInvariant();

        return await db.Paints.AsNoTracking()
            .Where(p => p.OwnerId == owner && (term == "" || EF.Functions.Like(p.NormalizedName, $"%{term}%")))
            .OrderBy(p => p.Name)
            .Take(limit)
            .ToListAsync(ct);
    }

    // Schemes -----------------------------------------------------------------

    /// <summary>
    /// Every scheme on a model, whoever wrote it, each marked with what the
    /// reader would be missing to paint it themselves.
    /// </summary>
    public async Task<List<SchemeView>> GetSchemesAsync(int modelId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var owner = user.UserId;

        // Sorted after loading, not in SQL: SQLite will not ORDER BY a
        // DateTimeOffset. A model carries a handful of schemes, so the cost of
        // doing it here is nothing.
        var schemes = (await db.PaintSchemes.AsNoTracking()
                .Where(s => s.ModelEntryId == modelId)
                .Include(s => s.Steps.OrderBy(x => x.Order))
                .Include(s => s.Photos)
                .ToListAsync(ct))
            .OrderByDescending(s => s.UpdatedUtc)
            .ToList();

        // Matched by name, not by id: the step may point at somebody else's bottle,
        // or at one that has since been thrown away.
        //
        // Only what is genuinely on the shelf counts. A paint marked "want" is
        // on the shopping list, so a scheme needing it must still say so - that
        // is the whole reason for recording the intention.
        var mine = await db.Paints.AsNoTracking()
            .Where(p => p.OwnerId == owner
                && (p.Stock == PaintStock.Have || p.Stock == PaintStock.Low))
            .Select(p => p.NormalizedName)
            .ToListAsync(ct);

        var onMyShelf = mine.ToHashSet();

        return schemes.Select(s => new SchemeView(
            s,
            s.OwnerId == owner,
            s.Steps
                .Select(step => step.PaintName)
                .Where(name => !string.IsNullOrWhiteSpace(name)
                    && !onMyShelf.Contains(name.ToLowerInvariant()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()))
            .ToList();
    }

    public async Task<PaintScheme?> CreateSchemeAsync(
        int modelId, string name, string? notes, string? ownerName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Models.AnyAsync(m => m.Id == modelId, ct)) return null;

        var scheme = new PaintScheme
        {
            ModelEntryId = modelId,
            OwnerId = user.UserId,
            OwnerName = Blank(ownerName),
            Name = name.Trim(),
            Notes = Blank(notes),
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };

        db.PaintSchemes.Add(scheme);
        await db.SaveChangesAsync(ct);
        return scheme;
    }

    public async Task UpdateSchemeAsync(
        int schemeId, string name, string? notes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        await using var db = await factory.CreateDbContextAsync(ct);
        var scheme = await MineAsync(db, schemeId, ct);
        if (scheme is null) return;

        scheme.Name = name.Trim();
        scheme.Notes = Blank(notes);
        scheme.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteSchemeAsync(int schemeId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var owner = user.UserId;

        await db.PaintSchemes
            .Where(s => s.Id == schemeId && s.OwnerId == owner)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Adds a step. The paint's name and swatch are copied onto it, so the
    /// recipe still reads correctly to someone who does not own that bottle.
    /// </summary>
    public async Task<PaintStep?> AddStepAsync(
        int schemeId, int? paintId, string paintName, string? technique, string? area,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var scheme = await MineAsync(db, schemeId, ct);
        if (scheme is null) return null;

        var paint = paintId is { } id
            ? await db.Paints.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            : null;

        var name = paint?.Name ?? paintName?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)) return null;

        var step = new PaintStep
        {
            PaintSchemeId = schemeId,
            PaintId = paint?.Id,
            PaintName = name,
            Hex = paint?.Hex,
            Technique = Blank(technique),
            Area = Blank(area),
            Order = await db.PaintSteps.CountAsync(s => s.PaintSchemeId == schemeId, ct),
        };

        db.PaintSteps.Add(step);
        scheme.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return step;
    }

    public async Task RemoveStepAsync(int stepId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var owner = user.UserId;

        var step = await db.PaintSteps
            .Include(s => s.PaintScheme)
            .FirstOrDefaultAsync(s => s.Id == stepId, ct);

        if (step?.PaintScheme is null || step.PaintScheme.OwnerId != owner) return;

        db.PaintSteps.Remove(step);
        step.PaintScheme.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // Close the gap so the order stays 0..n-1 and a later insert cannot
        // collide with a number that is still in use.
        var remaining = await db.PaintSteps
            .Where(s => s.PaintSchemeId == step.PaintSchemeId)
            .OrderBy(s => s.Order)
            .ToListAsync(ct);

        for (var i = 0; i < remaining.Count; i++) remaining[i].Order = i;
        await db.SaveChangesAsync(ct);
    }


    // Photos ------------------------------------------------------------------

    /// <summary>
    /// Records a photo against a scheme. The bytes are already on disk; this is
    /// only the row that points at them.
    /// </summary>
    public async Task<SchemePhoto?> AddPhotoAsync(
        int schemeId, string fileName, string contentType, long sizeBytes,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var scheme = await MineAsync(db, schemeId, ct);
        if (scheme is null) return null;

        var photo = new SchemePhoto
        {
            PaintSchemeId = schemeId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            AddedUtc = DateTimeOffset.UtcNow,
        };

        db.SchemePhotos.Add(photo);
        scheme.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return photo;
    }

    /// <summary>Returns the file name to delete from disk, or null if not allowed.</summary>
    public async Task<string?> RemovePhotoAsync(int photoId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var owner = user.UserId;

        var photo = await db.SchemePhotos
            .Include(p => p.PaintScheme)
            .FirstOrDefaultAsync(p => p.Id == photoId, ct);

        if (photo?.PaintScheme is null || photo.PaintScheme.OwnerId != owner) return null;

        var fileName = photo.FileName;
        db.SchemePhotos.Remove(photo);
        photo.PaintScheme.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return fileName;
    }

    /// <summary>A photo's file, for serving it. Readable by anyone: schemes are public.</summary>
    public async Task<SchemePhoto?> GetPhotoAsync(int photoId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.SchemePhotos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == photoId, ct);
    }
    /// <summary>The scheme, only if the caller wrote it.</summary>
    private async Task<PaintScheme?> MineAsync(MeshVaultDbContext db, int schemeId, CancellationToken ct)
    {
        var owner = user.UserId;
        return await db.PaintSchemes.FirstOrDefaultAsync(s => s.Id == schemeId && s.OwnerId == owner, ct);
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
