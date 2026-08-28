using MeshVault.Core.Models;
using MeshVault.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeshVault.Data;

/// <summary>What a restore pass put back.</summary>
public record SculptNameRestoreResult(int Restored, int Considered)
{
    public bool Any => Restored > 0;
}

/// <summary>
/// Puts back the spelling of a sculpt name that the app's own renaming took
/// away.
///
/// A sculpt's heading is read out of its file's name, and organizing under a
/// case convention rewrites that name. Before this was noticed, the sequence
/// "organize into kebab-case, then rescan" quietly relabelled a library:
/// "UD 067 Hole Trap" became "ud 067 hole trap", because by then the only
/// record of the capitals was a filename the app had itself replaced.
///
/// The record survives regardless. <see cref="OrganizeStep.From"/> holds every
/// path a run moved a file away from, kept so the run can be taken back, and
/// the oldest one for a file is the name it arrived with.
///
/// <see cref="VariantClassifier.Apply"/> no longer does this, so a library
/// organized from here on needs nothing from this class. It exists for the ones
/// already relabelled, where the damage is done and the evidence is still on
/// hand.
/// </summary>
public class SculptNameRestorer(
    IDbContextFactory<MeshVaultDbContext> factory,
    VariantRules rules,
    ILogger<SculptNameRestorer> log)
{
    /// <summary>Models held in memory at once, as in <see cref="VariantReindexer"/>.</summary>
    private const int PageSize = 200;

    /// <summary>
    /// Re-reads each file's original name and restores the spelling wherever it
    /// says the same thing as the name on record.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: a name is put back only when it differs from the
    /// stored one by case alone. That is the whole of the damage, and the guard
    /// is what makes this safe to run on a library it has nothing to fix. A
    /// file whose words changed for any other reason -- a definition curated
    /// since, a rename done by hand on the share -- reads as a genuine
    /// difference and is left exactly as it is, because this cannot tell which
    /// of the two names is the one the user wants.
    ///
    /// A sculpt set by hand is skipped as everywhere else. It already carries a
    /// spelling nothing overwrote.
    /// </remarks>
    public async Task<SculptNameRestoreResult> RestoreAsync(CancellationToken ct = default)
    {
        var classifier = rules.Current;
        int restored = 0, considered = 0, lastId = 0;

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

            var originals = await OriginalNamesAsync(
                db, page.SelectMany(m => m.Files).Select(f => f.Id).ToList(), ct);

            foreach (var model in page)
            {
                foreach (var file in model.Files)
                {
                    if (!originals.TryGetValue(file.Id, out var original)) continue;
                    considered++;
                    if (Restore(classifier, model, file, original)) restored++;
                }
            }

            await db.SaveChangesAsync(ct);
        }

        if (restored > 0)
            log.LogInformation("Restored the spelling of {Count} sculpt name(s)", restored);

        return new SculptNameRestoreResult(restored, considered);
    }

    /// <summary>
    /// The name each file arrived with, from the earliest run that moved it.
    /// </summary>
    /// <remarks>
    /// Read flat and grouped in memory rather than asked for as "the first step
    /// of each file": that is a correlated sub-select in a projection, which
    /// SQLite cannot do and EF will not fall back on -- it throws at runtime.
    /// Ordering by step id is enough, since the executor appends them as it
    /// works and runs happen one after another.
    /// </remarks>
    private static async Task<Dictionary<int, string>> OriginalNamesAsync(
        MeshVaultDbContext db, List<int> fileIds, CancellationToken ct)
    {
        var steps = await db.OrganizeSteps
            .AsNoTracking()
            .Where(s => s.FileId != null && fileIds.Contains(s.FileId.Value))
            .OrderBy(s => s.Id)
            .Select(s => new { FileId = s.FileId!.Value, s.From })
            .ToListAsync(ct);

        var earliest = new Dictionary<int, string>();
        foreach (var step in steps) earliest.TryAdd(step.FileId, step.From);
        return earliest;
    }

    /// <summary>Returns true when the file's stored spelling was put back.</summary>
    private static bool Restore(
        VariantClassifier classifier, ModelEntry model, ModelFile file, string originalPath)
    {
        if (file.VariantSetByUser || string.IsNullOrEmpty(file.SculptName)) return false;

        // The file name alone, not the path it sat at. The name on record was
        // itself read from a bare file name -- organizing lands every file
        // directly in its model's folder -- so reading the original the same
        // way compares like with like. Any folder that once contributed a word
        // is absent from both sides rather than one.
        var original = Path.GetFileName(originalPath);
        if (string.IsNullOrEmpty(original)) return false;

        var fallback = string.IsNullOrWhiteSpace(model.Name)
            ? Path.GetFileNameWithoutExtension(original)
            : model.Name;

        var was = classifier.Classify(fallback, original).DisplayName;

        if (string.Equals(was, file.SculptName, StringComparison.Ordinal)) return false;
        if (!string.Equals(was, file.SculptName, StringComparison.OrdinalIgnoreCase)) return false;

        file.SculptName = was;
        return true;
    }
}
