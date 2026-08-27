using MeshVault.Core.Models;
using MeshVault.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeshVault.Data;

/// <summary>What a pass over the library changed.</summary>
public record ReindexResult(int FilesReclassified, int CardsRepointed)
{
    public bool Any => FilesReclassified > 0 || CardsRepointed > 0;
}

/// <summary>
/// Re-reads every indexed file's name against the variant vocabulary.
///
/// A scan would do the same thing, but a scan walks the library share — minutes
/// of network reads to answer a question that is pure string work on rows
/// already in the database. Curating a definition must not cost that.
///
/// Files the user has set by hand are skipped throughout: the point of the
/// override is that it survives this.
/// </summary>
public class VariantReindexer(
    IDbContextFactory<MeshVaultDbContext> factory,
    VariantRules rules,
    VariantStore definitions,
    SettingsStore settings,
    ILogger<VariantReindexer> log)
{
    /// <summary>Models held in memory at once. Keeps a large library off the heap.</summary>
    private const int PageSize = 200;

    /// <summary>
    /// Puts the stored vocabulary into force, and re-reads the library when that
    /// is a change from what the stored sculpt keys were built with.
    /// </summary>
    /// <remarks>
    /// Recognising "no change" by fingerprint is what keeps this off the startup
    /// path on every restart: the work only happens when the definitions — or
    /// the classifier that reads them — actually moved.
    /// </remarks>
    public async Task<ReindexResult> ApplyAsync(CancellationToken ct = default)
    {
        var stored = await definitions.SeedIfEmptyAsync(ct);
        var fingerprint = rules.Set(stored).Fingerprint();

        if (await settings.GetStringAsync(SettingKeys.VariantRulesVersion, ct) == fingerprint)
            return new ReindexResult(0, 0);

        var result = await ReclassifyAllAsync(ct);
        await settings.SetStringAsync(SettingKeys.VariantRulesVersion, fingerprint, ct);

        if (result.Any)
        {
            log.LogInformation(
                "Variant vocabulary changed; re-read {Files} file(s) into sculpts and moved {Cards} card image(s)",
                result.FilesReclassified, result.CardsRepointed);
        }

        return result;
    }

    /// <summary>
    /// Re-reads one model's files. What a correction on a model page needs:
    /// walking the whole library to answer a question about six files would
    /// make the smallest edit the most expensive one.
    /// </summary>
    public async Task<ReindexResult> ReclassifyModelAsync(int modelId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var model = await db.Models
            .Include(m => m.Files)
            .FirstOrDefaultAsync(m => m.Id == modelId, ct);

        if (model is null) return new ReindexResult(0, 0);

        var classifier = rules.Current;
        var reclassified = model.Files.Count(f => classifier.Apply(model, f));
        var repointed = RepointCardImage(model) ? 1 : 0;

        await db.SaveChangesAsync(ct);
        return new ReindexResult(reclassified, repointed);
    }

    /// <summary>Re-reads every file, and re-picks each model's card image.</summary>
    public async Task<ReindexResult> ReclassifyAllAsync(CancellationToken ct = default)
    {
        var classifier = rules.Current;
        int reclassified = 0, repointed = 0, lastId = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            await using var db = await factory.CreateDbContextAsync(ct);

            var page = await db.Models
                .Include(m => m.Files)
                .Where(m => m.Id > lastId)
                .OrderBy(m => m.Id)
                .Take(PageSize)
                .ToListAsync(ct);

            if (page.Count == 0) break;
            lastId = page[^1].Id;

            foreach (var model in page)
            {
                foreach (var file in model.Files)
                    if (classifier.Apply(model, file))
                        reclassified++;

                if (RepointCardImage(model)) repointed++;
            }

            await db.SaveChangesAsync(ct);
        }

        return new ReindexResult(reclassified, repointed);
    }

    /// <summary>
    /// Points the model's card at the export that shows it best, out of those
    /// already rendered.
    /// </summary>
    /// <remarks>
    /// The card used to be whichever render finished first, which on a pack that
    /// ships supported and unsupported copies of everything was a coin toss
    /// between the sculpt and a thicket of scaffolding. Re-picking is only a
    /// pointer change — every candidate has a thumbnail on disk already, so no
    /// model is read off the library share to do it.
    ///
    /// A snapshot the user took is not touched: that is chosen by hand, and it
    /// wins over the card image wherever both exist.
    /// </remarks>
    private static bool RepointCardImage(ModelEntry model)
    {
        if (model.ThumbnailFileId is null) return false;

        var best = model.Files
            .Where(f => f.ThumbnailState == ThumbnailState.Ready)
            .OrderBy(f => f.VariantRank)
            .ThenBy(f => f.SizeBytes)
            .FirstOrDefault();

        if (best is null || best.Id == model.ThumbnailFileId) return false;

        model.ThumbnailFileId = best.Id;
        return true;
    }
}
