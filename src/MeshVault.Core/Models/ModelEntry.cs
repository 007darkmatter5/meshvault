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

    public List<ModelFile> Files { get; set; } = [];
    public List<Tag> Tags { get; set; } = [];
    public List<Collection> Collections { get; set; } = [];
    public List<ModelFavorite> Favorites { get; set; } = [];
}
