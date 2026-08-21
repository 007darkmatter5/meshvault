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
