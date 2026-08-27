using MeshVault.Core.Models;

namespace MeshVault.Core.Services;

/// <summary>One export of a sculpt, and what makes it different from the others.</summary>
public record SculptVariant(ModelFile File, string? Label)
{
    /// <summary>What to call this variant in a picker. The plain export has no label of its own.</summary>
    public string Display => Label ?? "Plain";
}

/// <summary>
/// A single sculpt, with every export of it that the model folder holds.
/// </summary>
public record SculptGroup(string Key, string Name, IReadOnlyList<SculptVariant> Variants)
{
    /// <summary>
    /// The export to show first — the best-ranked, so the cleanest copy rather
    /// than whichever happened to sort first.
    /// </summary>
    public ModelFile Preferred => Variants[0].File;

    public bool HasVariants => Variants.Count > 1;
}

/// <summary>Gathers a model's files into the sculpts they are exports of.</summary>
public static class VariantGrouper
{
    /// <summary>
    /// Groups <paramref name="files"/> by sculpt, best export first within each
    /// group and groups in name order.
    /// </summary>
    /// <remarks>
    /// Files indexed before variants existed have no sculpt key yet. Rather than
    /// dropping them into one nameless heap they fall back to standing alone,
    /// which is exactly how they behaved before.
    /// </remarks>
    public static List<SculptGroup> Group(IEnumerable<ModelFile> files)
    {
        return files
            .GroupBy(f => string.IsNullOrEmpty(f.SculptKey) ? f.RelativePath : f.SculptKey,
                     StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var variants = group
                    .Select(f => new SculptVariant(f, f.VariantLabel))
                    .OrderBy(v => v.File.VariantRank)
                    .ThenBy(v => v.File.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var name = variants[0].File.SculptName;
                if (string.IsNullOrWhiteSpace(name))
                    name = Path.GetFileNameWithoutExtension(variants[0].File.FileName);

                return new SculptGroup(group.Key, name, variants);
            })
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
