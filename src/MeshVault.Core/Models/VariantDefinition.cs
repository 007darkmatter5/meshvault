namespace MeshVault.Core.Models;

/// <summary>
/// One flavour a sculpt can be exported as, and the words that give it away.
/// </summary>
/// <remarks>
/// Curated, like <see cref="PaintBrand"/> — every creator invents their own
/// shorthand, and a list frozen into the build is wrong the first time somebody
/// buys from a new one.
///
/// Unlike paint brands these do start populated. An empty vocabulary does not
/// read as "nothing configured yet", it reads as broken: a pack of two hundred
/// files stays two hundred unrelated files until at least one definition
/// exists. The seeded set is a starting point and is meant to be edited.
///
/// Nothing references these by id. A file keeps the label it was given as plain
/// text, so renaming a definition changes what future passes produce rather
/// than rewriting what is already indexed — the next pass brings the library
/// into line.
/// </remarks>
public class VariantDefinition
{
    public int Id { get; set; }

    /// <summary>What this flavour is called: "Supported", "No logo", "Hollowed".</summary>
    public string Name { get; set; } = "";

    /// <summary>Lower-cased, so one instance cannot hold both Supported and supported.</summary>
    public string NormalizedName { get; set; } = "";

    /// <summary>
    /// Comma-separated words that mean this flavour. Each matches whole words
    /// only, and one written with spaces matches those words in sequence, so
    /// "no logo" also catches "no-logo" and "no_logo".
    /// </summary>
    public string MatchTerms { get; set; } = "";

    /// <summary>
    /// How good this flavour is to look at, lowest first. Decides which export
    /// a preview opens on and which one a model's card image is taken from.
    /// </summary>
    /// <remarks>
    /// Supports are a scaffold rather than the model, so they rank last by
    /// default — but that is a preference, not a fact, which is why it is a
    /// number on a row the user owns instead of a rule in the code.
    /// </remarks>
    public int PreviewRank { get; set; }

    /// <summary>
    /// Filler: the terms are stripped from the sculpt's name but the file is
    /// not marked as a variant of anything. "STL", "files", "printable".
    /// </summary>
    public bool IsFiller { get; set; }

    /// <summary>The terms, split and trimmed.</summary>
    public IEnumerable<string> Terms() =>
        MatchTerms.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The set new instances begin with. Verified against real packs: creators
    /// abbreviate mid-name (UD-001-SUP-Wall, UD-001-HOL-Wall), so the short
    /// forms matter as much as the words.
    /// </summary>
    public static List<VariantDefinition> Starter() =>
    [
        new() { Name = "Unsupported", PreviewRank = 1,
                MatchTerms = "unsupported, unsup, unsupport, no supports, no support, nosupports, nosupport" },
        new() { Name = "Solid", PreviewRank = 2, MatchTerms = "solid, unhollowed" },
        new() { Name = "Hollowed", PreviewRank = 3, MatchTerms = "hollowed, hollow, hol" },
        new() { Name = "No logo", PreviewRank = 4, MatchTerms = "no logo, nologo, logoless, nl" },
        new() { Name = "Logo", PreviewRank = 5, MatchTerms = "logo" },
        new() { Name = "One piece", PreviewRank = 6, MatchTerms = "one piece, onepiece, merged, whole" },
        new() { Name = "Supported", PreviewRank = 30,
                MatchTerms = "supported, presupported, pre supported, presup, sup, sups, supports, with supports, w supports" },
        new() { Name = "Filler", IsFiller = true, PreviewRank = 0,
                MatchTerms = "files, file, stl, 3mf, obj, lys, chitubox, print, prints, printable, version" },
    ];
}
