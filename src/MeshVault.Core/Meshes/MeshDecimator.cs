using System.Numerics;

namespace MeshVault.Core.Meshes;

/// <summary>A mesh reduced to a shared vertex list plus index triples.</summary>
public record IndexedMesh(List<Vector3> Vertices, List<(int A, int B, int C)> Triangles)
{
    public static IndexedMesh Empty => new([], []);
}

/// <summary>
/// Reduces a mesh by vertex clustering: the bounding box is divided into a grid,
/// every vertex collapses onto the first one seen in its cell, and triangles
/// that collapse to a line or point are dropped.
/// </summary>
/// <remarks>
/// This replaced keeping every Nth triangle. Stride sampling looks fine on a
/// coarse model and catastrophic on a dense one — a 2.7M triangle model reduced
/// to a 250k budget kept one triangle in eleven, leaving scattered facets with
/// the interior showing through. Clustering keeps the surface closed because
/// neighbouring triangles collapse together rather than being deleted at random.
/// </remarks>
public static class MeshDecimator
{
    private const int MinResolution = 8;
    private const int MaxResolution = 1024;

    /// <summary>Resolution guesses to reach the budget, before giving up and taking the closest.</summary>
    private const int MaxAttempts = 4;

    /// <summary>
    /// Clusters until the triangle count is close to <paramref name="triangleBudget"/>.
    /// Each attempt re-reads the mesh, so callers should pass a locally staged file.
    /// </summary>
    public static IndexedMesh Reduce(
        IMeshSource mesh, Bounds bounds, int triangleBudget, CancellationToken ct = default)
    {
        if (triangleBudget <= 0 || bounds.IsEmpty) return IndexedMesh.Empty;

        // A surface occupies roughly the square of the grid resolution, so this
        // lands near the budget on the first try for most models.
        var resolution = Math.Clamp((int)MathF.Sqrt(triangleBudget / 2f), MinResolution, MaxResolution);

        // Tracked separately: the best result that fits, and the least bad one
        // that does not. Collapsing these into a single "best" let an early
        // overshoot win and shipped a mesh three times over budget.
        IndexedMesh? bestUnder = null;
        IndexedMesh? bestOver = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var candidate = ClusterOnce(mesh, bounds, resolution, ct);
            var count = candidate.Triangles.Count;

            if (count == 0) return candidate;

            if (count <= triangleBudget)
            {
                if (bestUnder is null || count > bestUnder.Triangles.Count) bestUnder = candidate;

                // Close enough: within 25% of the budget is a good use of the bytes.
                if (count >= triangleBudget * 0.75) return candidate;
            }
            else if (bestOver is null || count < bestOver.Triangles.Count)
            {
                bestOver = candidate;
            }

            // Triangle count grows roughly with the square of the resolution.
            var scaled = (int)MathF.Round(resolution * MathF.Sqrt(triangleBudget / (float)count));
            var next = Math.Clamp(scaled, MinResolution, MaxResolution);

            // Guarantee progress: rounding can land on the same resolution while
            // still over budget, which would spin without improving.
            if (next == resolution) next = count > triangleBudget ? resolution - 1 : resolution + 1;
            if (next < MinResolution || next > MaxResolution) break;

            resolution = next;
        }

        return bestUnder ?? bestOver ?? IndexedMesh.Empty;
    }

    private static IndexedMesh ClusterOnce(
        IMeshSource mesh, Bounds bounds, int resolution, CancellationToken ct)
    {
        var size = bounds.Size;
        var scale = new Vector3(
            resolution / MathF.Max(size.X, 1e-6f),
            resolution / MathF.Max(size.Y, 1e-6f),
            resolution / MathF.Max(size.Z, 1e-6f));

        var cellToVertex = new Dictionary<long, int>();
        var vertices = new List<Vector3>();
        var seen = new HashSet<(int, int, int)>();
        var triangles = new List<(int, int, int)>();

        foreach (var triangle in mesh.ReadTriangles(ct))
        {
            ct.ThrowIfCancellationRequested();

            var a = Representative(triangle.A);
            var b = Representative(triangle.B);
            var c = Representative(triangle.C);

            // Collapsed to a line or a point: it contributes no surface.
            if (a == b || b == c || a == c) continue;

            // Deduplicate regardless of winding or starting vertex, but emit the
            // original order so face normals still point outwards.
            if (!seen.Add(Ordered(a, b, c))) continue;

            triangles.Add((a, b, c));
        }

        return new IndexedMesh(vertices, triangles);

        int Representative(Vector3 v)
        {
            var key = CellKey(v, bounds.Min, scale, resolution);
            if (cellToVertex.TryGetValue(key, out var index)) return index;

            // Keep the first real vertex rather than the cell centre; snapping to
            // centres visibly blocks off edges and corners.
            index = vertices.Count;
            vertices.Add(v);
            cellToVertex[key] = index;
            return index;
        }
    }

    private static long CellKey(Vector3 v, Vector3 min, Vector3 scale, int resolution)
    {
        var x = Math.Clamp((int)((v.X - min.X) * scale.X), 0, resolution);
        var y = Math.Clamp((int)((v.Y - min.Y) * scale.Y), 0, resolution);
        var z = Math.Clamp((int)((v.Z - min.Z) * scale.Z), 0, resolution);

        // Resolution is capped at 1024, so 11 bits per axis is enough.
        return ((long)x << 22) | ((long)y << 11) | (uint)z;
    }

    private static (int, int, int) Ordered(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }
}
