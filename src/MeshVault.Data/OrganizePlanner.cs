using MeshVault.Core.Models;
using MeshVault.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Data;

/// <summary>How a library should be laid out.</summary>
public record OrganizeRules
{
    /// <summary>Folder each model lands in, relative to the library root.</summary>
    public string FolderTemplate { get; init; } = "{designer}/{model}";

    /// <summary>
    /// Name for each file inside that folder. Ignored unless
    /// <see cref="RenameFiles"/> is set, and extensions are always kept.
    /// </summary>
    public string FileTemplate { get; init; } = "{model} - {file}";

    /// <summary>
    /// Off by default. A folder move is reversible and loses nothing; a rename
    /// throws away whatever the original name encoded, which is often the only
    /// record that a mesh was pre-supported or was version two.
    /// </summary>
    public bool RenameFiles { get; init; }
}

public enum MoveOutcome
{
    /// <summary>Already where the rules want it.</summary>
    AlreadyThere,
    Move,

    /// <summary>Something else is already using that path.</summary>
    Collides,

    /// <summary>The rules produced nothing usable, so the model is left alone.</summary>
    Unusable,
}

public record PlannedMove(
    int ModelId,
    string ModelName,
    string From,
    string To,
    MoveOutcome Outcome,
    string? Problem = null,
    IReadOnlyList<PlannedRename>? Renames = null)
{
    public IReadOnlyList<PlannedRename> Renames { get; init; } = Renames ?? [];
}

public record PlannedRename(int FileId, string From, string To);

public record OrganizePlan(IReadOnlyList<PlannedMove> Moves)
{
    public int Moving => Moves.Count(m => m.Outcome == MoveOutcome.Move);
    public int AlreadyThere => Moves.Count(m => m.Outcome == MoveOutcome.AlreadyThere);
    public int Colliding => Moves.Count(m => m.Outcome == MoveOutcome.Collides);
    public int Unusable => Moves.Count(m => m.Outcome == MoveOutcome.Unusable);
    public int Renames => Moves.Sum(m => m.Renames.Count);

    /// <summary>Nothing to do, so applying would be a no-op.</summary>
    public bool IsEmpty => Moving == 0 && Renames == 0;
}

/// <summary>
/// Works out what organising a library would do, without touching a single
/// file.
/// </summary>
/// <remarks>
/// Separate from applying it on purpose. This is the first thing in MeshVault
/// that would write to somebody's library, and a plan that can be read in full
/// before anything happens is the difference between a tool and a gamble.
/// </remarks>
public class OrganizePlanner(IDbContextFactory<MeshVaultDbContext> factory, ICurrentUser user)
{
    public async Task<OrganizePlan> PlanAsync(
        int libraryId, OrganizeRules rules, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var userId = user.UserId;

        var models = await db.Models.AsNoTracking()
            .Where(m => m.LibraryId == libraryId)
            .Include(m => m.Designer)
            .Include(m => m.Tags)
            .Include(m => m.Files)
            .Include(m => m.Collections.Where(c => c.OwnerId == userId))
            .OrderBy(m => m.RelativePath)
            .ToListAsync(ct);

        var moves = new List<PlannedMove>();

        // Folders the plan will occupy, so two models sent to the same place are
        // reported rather than one silently landing inside the other.
        var claimed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            var staying = Destination(model, rules) == model.RelativePath;
            if (staying) claimed[model.RelativePath] = model.Id;
        }

        foreach (var model in models)
        {
            var destination = Destination(model, rules);

            if (string.IsNullOrEmpty(destination))
            {
                moves.Add(new PlannedMove(model.Id, model.Name, model.RelativePath, "",
                    MoveOutcome.Unusable, "The template produced an empty path for this model."));
                continue;
            }

            if (destination == model.RelativePath)
            {
                moves.Add(new PlannedMove(model.Id, model.Name, model.RelativePath, destination,
                    MoveOutcome.AlreadyThere, Renames: PlanRenames(model, rules)));
                continue;
            }

            if (claimed.TryGetValue(destination, out var other) && other != model.Id)
            {
                moves.Add(new PlannedMove(model.Id, model.Name, model.RelativePath, destination,
                    MoveOutcome.Collides,
                    "Another model is already going there. Give them different names first."));
                continue;
            }

            claimed[destination] = model.Id;
            moves.Add(new PlannedMove(model.Id, model.Name, model.RelativePath, destination,
                MoveOutcome.Move, Renames: PlanRenames(model, rules)));
        }

        return new OrganizePlan(moves);
    }

    private static string Destination(ModelEntry model, OrganizeRules rules) =>
        PathTemplate.Render(rules.FolderTemplate, TokensFor(model), forFile: false);

    private static List<PlannedRename> PlanRenames(ModelEntry model, OrganizeRules rules)
    {
        if (!rules.RenameFiles) return [];

        var renames = new List<PlannedRename>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var file in model.Files.OrderBy(f => f.RelativePath))
        {
            index++;
            var tokens = TokensFor(model);
            tokens["file"] = Path.GetFileNameWithoutExtension(file.FileName);
            tokens["index"] = index.ToString();
            tokens["kind"] = file.Kind.ToString();

            var stem = PathTemplate.Render(rules.FileTemplate, tokens, forFile: true);
            if (string.IsNullOrEmpty(stem)) continue;

            // The extension is never templated. It is what tells every other
            // program on the machine what the file is.
            var name = stem + file.Extension;

            // Two files rendering to the same name would have one overwrite the
            // other, so the later ones are numbered instead.
            var candidate = name;
            var suffix = 2;
            while (!used.Add(candidate))
            {
                candidate = $"{stem} ({suffix}){file.Extension}";
                suffix++;
            }

            if (!string.Equals(candidate, file.FileName, StringComparison.Ordinal))
                renames.Add(new PlannedRename(file.Id, file.FileName, candidate));
        }

        return renames;
    }

    private static Dictionary<string, string?> TokensFor(ModelEntry model) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["model"] = model.Name,
        ["designer"] = model.Designer?.Name,
        ["source"] = model.SourceSite,
        ["collection"] = model.Collections.OrderBy(c => c.Name).FirstOrDefault()?.Name,
        ["tag"] = model.Tags.OrderBy(t => t.Name).FirstOrDefault()?.Name,
        ["year"] = model.AddedUtc.Year.ToString(),
        ["license"] = model.License,
    };
}
