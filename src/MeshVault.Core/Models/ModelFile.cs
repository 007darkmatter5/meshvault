namespace MeshVault.Core.Models;

public enum FileKind
{
    Other = 0,
    Mesh,      // stl, 3mf, obj, ply
    Cad,       // step, stp, f3d, scad
    Sliced,    // gcode, bgcode, ctb, form
    Image,     // png, jpg, webp, gif
    Document,  // txt, md, pdf
    Archive,   // zip, 7z, rar
}

public class ModelFile
{
    public int Id { get; set; }
    public int ModelEntryId { get; set; }
    public ModelEntry? ModelEntry { get; set; }

    /// <summary>Path relative to the library root.</summary>
    public string RelativePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public FileKind Kind { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset ModifiedUtc { get; set; }

    /// <summary>Content hash, used for duplicate detection and thumbnail cache keys.</summary>
    public string? Sha256 { get; set; }

    /// <summary>
    /// Which sculpt this file is an export of, normalised for grouping. Files
    /// sharing a key are one model exported several ways — supported and
    /// unsupported, hollowed and solid — rather than several models. Null until
    /// <see cref="Services.VariantClassifier"/> has read the name, and only
    /// meshes and CAD files ever get one.
    /// </summary>
    public string? SculptKey { get; set; }

    /// <summary>The sculpt's name with its original casing, for headings.</summary>
    public string? SculptName { get; set; }

    /// <summary>
    /// What sets this export apart from its siblings — "Supported",
    /// "Hollowed, 32mm" — or null for the plain version.
    /// </summary>
    public string? VariantLabel { get; set; }

    /// <summary>
    /// How good this export is to look at, lowest first, summed from the
    /// <see cref="VariantDefinition.PreviewRank"/> of each of its labels.
    /// </summary>
    /// <remarks>
    /// Stored rather than worked out on demand so that "show the cleanest copy"
    /// is a sort — the thumbnail worker can order by it in SQL, and grouping
    /// does not need the vocabulary in hand.
    /// </remarks>
    public int VariantRank { get; set; }

    /// <summary>
    /// True once someone has set this file's sculpt or variant by hand.
    /// Rescans and vocabulary changes leave such a file alone, so a correction
    /// is not undone by the next pass.
    /// </summary>
    /// <remarks>
    /// The same bargain as <see cref="ModelEntry.NameSetByUser"/>: the app
    /// proposes, the person decides, and the decision outlives the proposal.
    /// </remarks>
    public bool VariantSetByUser { get; set; }

    // Populated for meshes once geometry has been parsed.
    public int? TriangleCount { get; set; }
    public float? SizeX { get; set; }
    public float? SizeY { get; set; }
    public float? SizeZ { get; set; }

    public ThumbnailState ThumbnailState { get; set; }
}

public enum ThumbnailState
{
    Pending = 0,
    Ready,
    Failed,
    NotApplicable,
}
