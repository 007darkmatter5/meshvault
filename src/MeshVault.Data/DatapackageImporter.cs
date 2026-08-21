using MeshVault.Core.Models;
using MeshVault.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeshVault.Data;

public record ImportResult(
    int Scanned, int Renamed, int Tagged, int SourcesSet, int DesignersSet,
    int LicensesSet, int Collected, int Described, int Skipped)
{
    public int Changed =>
        Renamed + Tagged + SourcesSet + DesignersSet + LicensesSet + Collected + Described;
}

public record ImportProgress(int Done, int Total, string? Current);

/// <summary>
/// Fills in model metadata from Manyfold's datapackage.json sidecars.
/// </summary>
/// <remarks>
/// Additive by design: it never overwrites a name someone has typed, never
/// removes tags, and only sets a designer, licence or source that is currently
/// blank. Running it twice therefore changes nothing the second time.
/// </remarks>
public class DatapackageImporter(
    IDbContextFactory<MeshVaultDbContext> factory,
    ICurrentUser user,
    ILogger<DatapackageImporter> log)
{
    public async Task<ImportResult> ImportAsync(
        int libraryId,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var library = await db.Libraries.FirstOrDefaultAsync(l => l.Id == libraryId, ct)
            ?? throw new InvalidOperationException($"Library {libraryId} not found.");

        var models = await db.Models
            .Include(m => m.Tags)
            .Include(m => m.Collections)
            .Where(m => m.LibraryId == libraryId)
            .ToListAsync(ct);

        // Loaded once so tags, designers and collections are reused rather than
        // duplicated per model.
        var userId = user.UserId;
        var tagsByName = await db.Tags.ToDictionaryAsync(t => t.NormalizedName, ct);
        var designersByName = await db.Designers.ToDictionaryAsync(d => d.NormalizedName, ct);
        var collectionsByName = await db.Collections
            .Where(c => c.OwnerId == userId)
            .ToDictionaryAsync(c => c.NormalizedName, ct);

        int renamed = 0, tagged = 0, sources = 0, designers = 0, licenses = 0;
        int collected = 0, described = 0, skipped = 0, done = 0;

        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();

            done++;
            if (done % 10 == 0)
                progress?.Report(new ImportProgress(done, models.Count, model.Name));

            var path = Path.Combine(
                library.Path,
                model.RelativePath.Replace('/', Path.DirectorySeparatorChar),
                DatapackageReader.FileName);

            if (!File.Exists(path)) { skipped++; continue; }

            var package = DatapackageReader.Read(path);

            if (ApplyName(model, package)) renamed++;
            if (ApplyTags(model, package, tagsByName, db)) tagged++;
            if (ApplySource(model, package)) sources++;
            if (ApplyDesigner(model, package, designersByName, db)) designers++;
            if (ApplyLicense(model, package)) licenses++;
            if (ApplyCollections(model, package, collectionsByName, userId, db)) collected++;
            if (ApplyDescription(model, package)) described++;
        }

        progress?.Report(new ImportProgress(models.Count, models.Count, "Saving..."));
        await db.SaveChangesAsync(ct);

        var result = new ImportResult(
            models.Count, renamed, tagged, sources, designers, licenses, collected, described, skipped);

        log.LogInformation(
            "Imported datapackages for {Library}: {Renamed} renamed, {Tagged} tagged, " +
            "{Sources} sources, {Designers} designers, {Licenses} licences, " +
            "{Collected} collected, {Described} described, {Skipped} without a sidecar",
            library.Name, renamed, tagged, sources, designers, licenses, collected, described, skipped);

        return result;
    }

    /// <summary>Sets the title unless the user has chosen a name themselves.</summary>
    private static bool ApplyName(ModelEntry model, Datapackage package)
    {
        if (model.NameSetByUser) return false;
        if (package.Title is not { } title) return false;
        if (string.Equals(model.Name, title, StringComparison.Ordinal)) return false;

        model.Name = title;
        return true;
    }

    private static bool ApplyTags(
        ModelEntry model, Datapackage package, Dictionary<string, Tag> tagsByName, MeshVaultDbContext db)
    {
        var added = false;

        foreach (var keyword in package.Keywords)
        {
            var normalized = keyword.ToLowerInvariant();
            if (model.Tags.Any(t => t.NormalizedName == normalized)) continue;

            if (!tagsByName.TryGetValue(normalized, out var tag))
            {
                tag = new Tag { Name = keyword, NormalizedName = normalized };
                db.Tags.Add(tag);
                tagsByName[normalized] = tag;
            }

            model.Tags.Add(tag);
            added = true;
        }

        return added;
    }

    /// <summary>
    /// Only fills an empty source, and only from a genuinely external URL. A
    /// Manyfold export records its own instance as the homepage, which is
    /// usually a localhost address and useless as provenance.
    /// </summary>
    private static bool ApplySource(ModelEntry model, Datapackage package)
    {
        if (model.SourceUrl is not null) return false;
        if (package.Homepage is not { } homepage) return false;
        if (!SourceSites.TryParse(homepage, out var uri)) return false;
        if (IsLocal(uri)) return false;

        model.SourceUrl = uri.ToString();
        model.SourceSite = SourceSites.Detect(model.SourceUrl);
        return true;
    }

    private static bool IsLocal(Uri uri) =>
        uri.IsLoopback
        || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || uri.Host.StartsWith("192.168.", StringComparison.Ordinal)
        || uri.Host.StartsWith("10.", StringComparison.Ordinal)
        || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
        // A bare hostname with no dot is a LAN machine, not a public site.
        || !uri.Host.Contains('.');

    private static bool ApplyDesigner(
        ModelEntry model, Datapackage package,
        Dictionary<string, Designer> designersByName, MeshVaultDbContext db)
    {
        if (model.DesignerId is not null || model.Designer is not null) return false;
        if (package.Author is not { } author) return false;

        var normalized = author.ToLowerInvariant();
        if (!designersByName.TryGetValue(normalized, out var designer))
        {
            designer = new Designer
            {
                Name = author,
                NormalizedName = normalized,
                CreatedUtc = DateTimeOffset.UtcNow,
            };
            db.Designers.Add(designer);
            designersByName[normalized] = designer;
        }

        model.Designer = designer;
        return true;
    }

    /// <summary>
    /// Adds the model to the collections named in the sidecar, creating any that
    /// do not exist yet. Never removes an existing membership.
    /// </summary>
    private static bool ApplyCollections(
        ModelEntry model, Datapackage package,
        Dictionary<string, Collection> collectionsByName, string userId, MeshVaultDbContext db)
    {
        var added = false;

        foreach (var name in package.Collections)
        {
            var normalized = name.ToLowerInvariant();
            if (model.Collections.Any(c => c.NormalizedName == normalized && c.OwnerId == userId))
                continue;

            if (!collectionsByName.TryGetValue(normalized, out var collection))
            {
                collection = new Collection
                {
                    Name = name,
                    NormalizedName = normalized,
                    OwnerId = userId,
                    CreatedUtc = DateTimeOffset.UtcNow,
                };
                db.Collections.Add(collection);
                collectionsByName[normalized] = collection;
            }

            model.Collections.Add(collection);
            added = true;
        }

        return added;
    }

    private static bool ApplyDescription(ModelEntry model, Datapackage package)
    {
        if (model.Description is not null) return false;
        if (package.Description is not { } description) return false;

        model.Description = description;
        return true;
    }

    private static bool ApplyLicense(ModelEntry model, Datapackage package)
    {
        if (model.License is not null) return false;
        if (package.License is not { } license) return false;

        model.License = license;
        return true;
    }
}
