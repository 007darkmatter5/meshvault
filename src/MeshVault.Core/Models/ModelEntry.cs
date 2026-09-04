namespace MeshVault.Core.Models;

/// <summary>
/// A logical model: usually one folder holding one or more mesh files plus
/// images, READMEs and licence text. This is the unit the user browses and tags.
/// </summary>
public class ModelEntry
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public Library? Library { get; set; }

    /// <summary>Path of the model's folder, relative to the library root.</summary>
    public string RelativePath { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>
    /// True once someone has renamed this model by hand. Importers and rescans
    /// leave such names alone, so a deliberate title is never overwritten by a
    /// folder name or a sidecar file.
    /// </summary>
    public bool NameSetByUser { get; set; }

    public string? Description { get; set; }
    public string? Notes { get; set; }

    // Variant groups ---------------------------------------------------------
    //
    // A creator who ships supported, unsupported, hollowed and no-logo copies
    // of a set often puts each in its own folder, and a folder is a model. The
    // same mini then appears four times, unrelated. Grouping says the four rows
    // are one thing without moving a file or destroying a row: the folders stay
    // exactly as they are, and each keeps its own metadata underneath.
    //
    // Written only by an approved regroup, never by a scan, so a rescan cannot
    // undo a grouping the user agreed to.

    /// <summary>
    /// Sculpt this model is one export of, shared with the other members of its
    /// group. Null for a model that stands on its own, which is most of them.
    /// </summary>
    public string? GroupKey { get; set; }

    /// <summary>What the group is called. Seeded from the best-ranked member's name.</summary>
    public string? GroupName { get; set; }

    /// <summary>
    /// Whether this member represents the group where one entry is wanted.
    /// </summary>
    /// <remarks>
    /// Browse lists a model when it is ungrouped or primary, which is what turns
    /// four cards into one. Exactly one member of a group carries it; the
    /// regroup picks the best-ranked export, so the card shows the sculpt rather
    /// than a thicket of supports.
    /// </remarks>
    public bool GroupPrimary { get; set; }

    // Provenance. Shared metadata: it describes the model, not the viewer.
    public string? SourceUrl { get; set; }
    /// <summary>Site name derived from <see cref="SourceUrl"/>, for badging and filtering.</summary>
    public string? SourceSite { get; set; }
    public int? DesignerId { get; set; }
    public Designer? Designer { get; set; }
    public string? License { get; set; }

    public long TotalBytes { get; set; }
    public DateTimeOffset AddedUtc { get; set; }
    public DateTimeOffset FileModifiedUtc { get; set; }

    /// <summary>The file whose auto-rendered thumbnail represents this model on a card.</summary>
    public int? ThumbnailFileId { get; set; }

    /// <summary>
    /// When the user last saved a snapshot from the 3D viewer. Non-null means a
    /// snapshot exists and takes precedence over the auto render; the timestamp
    /// also busts the browser cache when it is replaced.
    /// </summary>
    public DateTimeOffset? SnapshotUpdatedUtc { get; set; }

    /// <summary>
    /// Where the camera stood when the snapshot was taken, so the viewer can
    /// open on the angle the card image shows instead of its default
    /// three-quarter framing.
    /// </summary>
    /// <remarks>
    /// Held as a multiple of the model's bounding radius rather than in scene
    /// units. The viewer frames every model to fit, so a saved distance in raw
    /// units would put the camera inside a smaller mesh, or leave a larger one
    /// as a speck — and the same model re-exported at a different scale is
    /// ordinary. Null when no snapshot has been taken, or when one was taken
    /// before this was recorded.
    /// </remarks>
    public double? SnapshotViewX { get; set; }
    public double? SnapshotViewY { get; set; }
    public double? SnapshotViewZ { get; set; }

    public List<ModelFile> Files { get; set; } = [];
    public List<Tag> Tags { get; set; } = [];
    public List<Collection> Collections { get; set; } = [];

    /// <summary>
    /// The collection that names this model's folder, starred by hand when the
    /// model is in more than one.
    /// </summary>
    /// <remarks>
    /// A model can be in any number of collections and lives in exactly one
    /// folder, so something has to break the tie. It used to be whichever
    /// sorted first alphabetically -- so a collection called "Archive" quietly
    /// outranked the one somebody actually organises by, and adding a model to
    /// a new collection could move it on disk for a reason nobody could see.
    ///
    /// Null means "work it out", which <see cref="PrimaryCollection"/> does:
    /// the only collection when there is exactly one, and otherwise nothing.
    /// Nothing collapses the <c>{collection}</c> level rather than guessing, so
    /// unstarring is also how a model opts out of being filed by collection at
    /// all.
    /// </remarks>
    public int? PrimaryCollectionId { get; set; }

    /// <summary>
    /// The collection that names this model's folder, or null when none does.
    /// Needs <see cref="Collections"/> loaded.
    /// </summary>
    /// <remarks>
    /// Resolved against the memberships rather than trusted outright: a star
    /// left behind on a collection the model has since left would otherwise
    /// keep naming its folder from outside it.
    /// </remarks>
    public Collection? PrimaryCollection =>
        Collections.FirstOrDefault(c => c.Id == PrimaryCollectionId)
        ?? (Collections.Count == 1 ? Collections[0] : null);
    public List<ModelFavorite> Favorites { get; set; } = [];
}
