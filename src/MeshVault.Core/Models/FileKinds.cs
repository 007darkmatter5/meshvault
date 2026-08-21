namespace MeshVault.Core.Models;

public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".stl"] = FileKind.Mesh,   [".3mf"] = FileKind.Mesh,  [".obj"] = FileKind.Mesh,
        [".ply"] = FileKind.Mesh,
        [".step"] = FileKind.Cad,   [".stp"] = FileKind.Cad,   [".f3d"] = FileKind.Cad,
        [".scad"] = FileKind.Cad,   [".ipt"] = FileKind.Cad,   [".blend"] = FileKind.Cad,
        [".gcode"] = FileKind.Sliced, [".bgcode"] = FileKind.Sliced, [".ctb"] = FileKind.Sliced,
        [".form"] = FileKind.Sliced, [".3mf.gcode"] = FileKind.Sliced,
        [".png"] = FileKind.Image,  [".jpg"] = FileKind.Image,  [".jpeg"] = FileKind.Image,
        [".webp"] = FileKind.Image, [".gif"] = FileKind.Image,  [".bmp"] = FileKind.Image,
        [".txt"] = FileKind.Document, [".md"] = FileKind.Document, [".pdf"] = FileKind.Document,
        [".zip"] = FileKind.Archive, [".7z"] = FileKind.Archive, [".rar"] = FileKind.Archive,
    };

    public static FileKind FromExtension(string extension) =>
        Map.TryGetValue(extension, out var kind) ? kind : FileKind.Other;

    /// <summary>Extensions we can currently rasterise a thumbnail for.</summary>
    public static bool CanThumbnail(string extension) =>
        extension.Equals(".stl", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".3mf", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".obj", StringComparison.OrdinalIgnoreCase);
}
