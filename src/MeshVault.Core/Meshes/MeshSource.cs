using System.Numerics;

namespace MeshVault.Core.Meshes;

public readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C)
{
    /// <summary>Geometric normal, recomputed rather than trusted from the file.</summary>
    public Vector3 Normal()
    {
        var n = Vector3.Cross(B - A, C - A);
        var length = n.Length();
        return length > 1e-12f ? n / length : Vector3.UnitZ;
    }
}

public readonly record struct Bounds(Vector3 Min, Vector3 Max)
{
    public Vector3 Size => Max - Min;
    public Vector3 Center => (Min + Max) * 0.5f;
    public bool IsEmpty => Max.X < Min.X;

    public static Bounds Empty => new(
        new Vector3(float.PositiveInfinity), new Vector3(float.NegativeInfinity));

    public Bounds Add(Vector3 p) => new(Vector3.Min(Min, p), Vector3.Max(Max, p));
}

/// <summary>
/// A mesh that can be walked more than once without being held in memory. A
/// 129 MB STL is roughly 2.7 million triangles, so materialising one costs
/// ~100 MB; streaming lets bounds and rasterising each take a separate pass at
/// constant memory.
/// </summary>
public interface IMeshSource
{
    /// <summary>Triangle count when the format states it up front, otherwise null.</summary>
    int? TriangleCount { get; }

    IEnumerable<Triangle> ReadTriangles(CancellationToken ct = default);
}

public static class MeshSourceExtensions
{
    public static Bounds ComputeBounds(this IMeshSource source, CancellationToken ct = default)
    {
        var bounds = Bounds.Empty;
        foreach (var t in source.ReadTriangles(ct))
        {
            bounds = bounds.Add(t.A).Add(t.B).Add(t.C);
        }
        return bounds;
    }

    public static int CountTriangles(this IMeshSource source, CancellationToken ct = default)
    {
        if (source.TriangleCount is { } known) return known;

        var count = 0;
        foreach (var _ in source.ReadTriangles(ct)) count++;
        return count;
    }
}

public class MeshFormatException(string message) : Exception(message);
