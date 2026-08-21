namespace MeshVault.Core.Meshes;

/// <summary>Picks a reader by extension.</summary>
public static class MeshLoader
{
    public static bool CanRead(string extension) =>
        extension.Equals(".stl", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".3mf", StringComparison.OrdinalIgnoreCase);

    public static IMeshSource Open(string path)
    {
        var extension = Path.GetExtension(path);

        if (extension.Equals(".stl", StringComparison.OrdinalIgnoreCase))
            return new StlMeshSource(path);

        if (extension.Equals(".3mf", StringComparison.OrdinalIgnoreCase))
            return new ThreeMfMeshSource(path);

        throw new MeshFormatException($"No mesh reader for '{extension}'.");
    }
}
