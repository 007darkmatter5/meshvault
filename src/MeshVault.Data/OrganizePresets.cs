using MeshVault.Core.Services;

namespace MeshVault.Data;

/// <summary>A whole way of laying a library out, under one name.</summary>
/// <param name="Example">
/// One path the preset would produce. Written by hand rather than rendered from
/// the library, because the plan on the right is already a real answer built
/// from real models -- and it is one click away. A second, lesser preview
/// system beside it would be the sprawl this page is trying to escape.
/// </param>
public record OrganizePreset(string Name, string Summary, string Example, OrganizeRules Rules);

/// <summary>
/// The handful of layouts worth offering, so choosing one is picking rather
/// than authoring.
/// </summary>
/// <remarks>
/// The templates are only meaningful in combination, and every dangerous
/// interaction between them is invisible in the fields themselves: {sculpt} in
/// the folder template decides whether a pack of ninety-eight stays one folder
/// or becomes ninety-eight, and dropping {file} from the file template throws
/// away the only record that a mesh was hollowed unless {variant} is there to
/// catch it. A preset is the right unit precisely because it fixes all of that
/// at once.
///
/// Renaming appears in exactly one preset. It is the only choice here that
/// destroys something, so it should be picked deliberately rather than met as a
/// switch while exploring.
/// </remarks>
public static class OrganizePresets
{
    // Each adds one thing to the one above it, and the example says which. A
    // list of four unrelated schemes would be four things to compare; a ladder
    // is one decision about how far to go.
    public static IReadOnlyList<OrganizePreset> All =>
    [
        new("By designer",
            "Every model in its designer's folder, named and packed as it is now.",
            "Dungeon Blocks/UD Pack Supported/UD-067-HOL-Hole Trap.stl",
            new OrganizeRules { FolderTemplate = "{designer}/{model}" }),

        new("A folder per mini",
            "Breaks a pack into one folder for each sculpt, and gathers the "
            + "separate supported and hollowed folders of one mini back together.",
            "Dungeon Blocks/UD 067 Hole Trap/UD-067-HOL-Hole Trap.stl",
            new OrganizeRules { FolderTemplate = "{designer}/{sculpt}" }),

        new("By designer and collection",
            "The same, with your collections as a level in between.",
            "Dungeon Blocks/The Ultimate Dungeon/UD 067 Hole Trap/UD-067-HOL-Hole Trap.stl",
            new OrganizeRules { FolderTemplate = "{designer}/{collection}/{sculpt}" }),

        new("Tidy the names too",
            "The same again, with every file put into one consistent case. The "
            + "only preset that renames -- but it keeps each name, so nothing is lost.",
            "Dungeon Blocks/The Ultimate Dungeon/UD 067 Hole Trap/ud-067-hol-hole-trap.stl",
            new OrganizeRules
            {
                FolderTemplate = "{designer}/{collection}/{sculpt}",
                RenameFiles = true,

                // {file}, not {sculpt}-{variant}. Keeping the original name and
                // only changing its case is the one renaming scheme that cannot
                // lose anything: the creator already encoded the variant in it
                // -- HOL, SUP, NL, and nothing at all for the plain one -- in a
                // shorthand that is theirs rather than a guess of ours.
                //
                // A scheme that rebuilds the name has to reconstruct that from
                // MeshVault's own classification, and is wrong wherever the
                // classification is. It also cannot collide, because the names
                // it keeps were already unique on disk, so no file ends up
                // meaninglessly numbered "(2)".
                FileTemplate = "{file}",
                FileCase = NameCase.Kebab,
            }),
    ];

    /// <summary>
    /// The preset these rules are, or null for a template of somebody's own.
    /// </summary>
    /// <remarks>
    /// Compared by value, so editing a template by hand and landing back on a
    /// preset's exact wording selects it again rather than leaving the page
    /// insisting the choice is custom.
    ///
    /// The file half is only compared when renaming is on. A file template left
    /// at whatever it was last set to, with the switch off, changes nothing --
    /// and letting it decide the answer would leave two identical-looking pages
    /// disagreeing about which preset they are on.
    /// </remarks>
    public static OrganizePreset? Matching(OrganizeRules rules) =>
        All.FirstOrDefault(p =>
            p.Rules.FolderTemplate == rules.FolderTemplate
            && p.Rules.FolderCase == rules.FolderCase
            && p.Rules.RenameFiles == rules.RenameFiles
            && (!rules.RenameFiles
                || (p.Rules.FileTemplate == rules.FileTemplate
                    && p.Rules.FileCase == rules.FileCase)));
}
