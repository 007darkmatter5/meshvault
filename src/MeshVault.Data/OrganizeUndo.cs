using MeshVault.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeshVault.Data;

/// <summary>What taking a run back did.</summary>
public record UndoResult(int FilesRestored, int Skipped, IReadOnlyList<string> Problems)
{
    public bool Clean => Problems.Count == 0;
}

/// <summary>
/// Takes an <see cref="OrganizeRun"/> back.
/// </summary>
/// <remarks>
/// The same rules as applying, read backwards, and for the same reasons.
///
/// Files, never folders: every step is one file returning to the path it was
/// recorded at, so a run that stops half way leaves the rest where the record
/// still says they are. Never overwrite: a file whose old path is occupied by
/// something else is left alone and said out loud. Never guess: a file that is
/// not where the record says it is has been touched since, and this stops
/// rather than deciding on somebody's behalf what they meant by that.
///
/// It cannot give back what a run deleted, and it cannot unpick a merge — the
/// tags of a model folded into another went into it, and nothing here knows
/// which were whose. <see cref="OrganizeRun.FullyReversible"/> says which kind
/// of run this was, and the page says so before the button rather than after.
/// </remarks>
public class OrganizeUndo(
    IDbContextFactory<MeshVaultDbContext> factory,
    ILogger<OrganizeUndo> log)
{
    /// <summary>The last run that still stands, or null when there is none.</summary>
    public async Task<OrganizeRun?> LastAsync(int libraryId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        return await db.OrganizeRuns.AsNoTracking()
            .Where(r => r.LibraryId == libraryId && r.UndoneUtc == null)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<UndoResult> UndoAsync(
        int runId, IProgress<OrganizeProgress>? progress = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var run = await db.OrganizeRuns.Include(r => r.Steps)
            .FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new InvalidOperationException($"Run {runId} is not in the catalog.");

        if (run.UndoneUtc is not null)
            throw new InvalidOperationException("That run has already been taken back.");

        var library = await db.Libraries.FirstOrDefaultAsync(l => l.Id == run.LibraryId, ct)
            ?? throw new InvalidOperationException("That library is no longer here.");

        // The same gate as applying. Permission to move files can be taken away
        // between a run and its undo, and this writes to the share exactly as
        // the run did.
        if (!library.AllowOrganize)
            throw new InvalidOperationException(
                $"{library.Name} does not allow MeshVault to move files. Turn that on first.");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(library.Path));
        if (!Directory.Exists(root))
            throw new InvalidOperationException($"{library.Name} is not reachable at {root}.");

        var problems = new List<string>();
        int restored = 0, skipped = 0;

        // Models this run brought into being, learned from the files it gave
        // them: the step says the destination was new, and the file still
        // points at it until this loop hands it back.
        var invented = new HashSet<int>();

        // Backwards. A file moved twice within one run — filed, then rehomed —
        // has to retrace both, and in the other order.
        var steps = run.Steps.OrderByDescending(s => s.Id).ToList();
        var fileSteps = steps.Where(s => s.FileId is not null).ToList();
        var done = 0;

        foreach (var step in fileSteps)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            progress?.Report(new OrganizeProgress(done, fileSteps.Count, step.From));

            var file = await db.Files.FirstOrDefaultAsync(f => f.Id == step.FileId, ct);
            if (file is null)
            {
                skipped++;
                continue;
            }

            // Moved, renamed or rescanned since. The record describes a library
            // that no longer exists, and acting on it would be guessing.
            if (!string.Equals(file.RelativePath, step.To, StringComparison.Ordinal))
            {
                problems.Add(
                    $"{step.To} has changed since the run, so it was left where it is.");
                skipped++;
                continue;
            }

            var from = Combine(root, step.To);
            var back = Combine(root, step.From);

            if (from is null || back is null)
            {
                problems.Add($"{step.From} is outside the library, so it was skipped.");
                skipped++;
                continue;
            }

            if (!File.Exists(from))
            {
                problems.Add($"{step.To} is no longer on disk, so it was left out.");
                skipped++;
                continue;
            }

            // Never overwrite, on the way back as much as on the way there.
            if (File.Exists(back) && !string.Equals(from, back, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{step.From} is taken by something else, so {file.FileName} stayed.");
                skipped++;
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(back)!);
                if (!string.Equals(from, back, StringComparison.Ordinal)) MoveBack(from, back);

                if (step.ToModelCreated) invented.Add(file.ModelEntryId);

                file.RelativePath = step.From;
                file.FileName = Path.GetFileName(step.From);
                if (step.FromModelId is { } owner) file.ModelEntryId = owner;
                restored++;
            }
            catch (Exception ex)
            {
                problems.Add($"Could not put {step.From} back: {ex.Message}");
                skipped++;
            }
        }

        await db.SaveChangesAsync(ct);

        foreach (var step in steps.Where(s => s.ModelId is not null))
        {
            var model = await db.Models.FirstOrDefaultAsync(m => m.Id == step.ModelId, ct);

            if (model is not null
                && string.Equals(model.RelativePath, step.To, StringComparison.Ordinal))
            {
                model.RelativePath = step.From;

                // The name follows the folder here for the same reason it does
                // going forwards. Putting the folder back and leaving the name
                // organizing gave it would undo half of what happened, and the
                // half left behind is the half people can see.
                if (!model.NameSetByUser) model.Name = step.From.Split('/')[^1];
            }
        }

        // A model the run invented, now holding nothing, is one it should take
        // away again — the same tidying RemoveEmptiedAsync does forwards.
        //
        // Only the ones it invented. An empty model that predates the run is
        // somebody's, and clearing up more than was made is not undoing.
        if (invented.Count > 0)
        {
            var abandoned = await db.Models
                .Where(m => invented.Contains(m.Id) && !m.Files.Any())
                .ToListAsync(ct);

            db.Models.RemoveRange(abandoned);
        }

        run.UndoneUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        PruneEmpty(root, steps.Select(s => s.To).ToList(), problems);

        log.LogInformation(
            "Undid organize run {Run} on {Library}: {Restored} file(s) back, {Skipped} skipped",
            run.Id, library.Name, restored, skipped);

        foreach (var problem in problems)
            log.LogWarning("Undoing run {Run}: {Problem}", run.Id, problem);

        return new UndoResult(restored, skipped, problems);
    }

    /// <summary>Same case-only handling as the executor, for the same reason.</summary>
    private static void MoveBack(string from, string to)
    {
        if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(from, to);
            return;
        }

        File.Move(from, to, overwrite: true);
    }

    /// <summary>Folders the run made that nothing is left in.</summary>
    private static void PruneEmpty(string root, List<string> paths, List<string> problems)
    {
        foreach (var folder in paths
            .Select(p => p.Contains('/') ? p[..p.LastIndexOf('/')] : p)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => p.Length))
        {
            var full = Combine(root, folder);
            if (full is null || !Directory.Exists(full)) continue;

            try
            {
                if (Directory.EnumerateFileSystemEntries(full).Any()) continue;
                Directory.Delete(full);
            }
            catch (Exception ex)
            {
                problems.Add($"Could not remove the empty folder {folder}: {ex.Message}");
            }
        }
    }

    /// <summary>Joins a relative path to the root, refusing anything that climbs out.</summary>
    private static string? Combine(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
