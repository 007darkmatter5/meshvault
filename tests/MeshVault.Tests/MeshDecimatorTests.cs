using System.Numerics;
using MeshVault.Core.Meshes;

namespace MeshVault.Tests;

/// <summary>
/// Guards against the regression that produced a shredded model in the browser:
/// keeping every Nth triangle deleted neighbours at random, so a dense mesh lost
/// its surface and the interior showed through. Clustering must keep the surface.
/// </summary>
public class MeshDecimatorTests
{
    private sealed class ListMesh(List<Triangle> triangles) : IMeshSource
    {
        public int? TriangleCount => triangles.Count;
        public IEnumerable<Triangle> ReadTriangles(CancellationToken ct = default) => triangles;
    }

    /// <summary>A closed sphere, tessellated finely enough to need reducing.</summary>
    private static IMeshSource Sphere(int segments)
    {
        var triangles = new List<Triangle>();

        Vector3 At(int i, int j)
        {
            var theta = MathF.PI * i / segments;
            var phi = 2 * MathF.PI * j / segments;
            return new Vector3(
                MathF.Sin(theta) * MathF.Cos(phi),
                MathF.Sin(theta) * MathF.Sin(phi),
                MathF.Cos(theta)) * 50f;
        }

        for (var i = 0; i < segments; i++)
        {
            for (var j = 0; j < segments; j++)
            {
                var a = At(i, j);
                var b = At(i + 1, j);
                var c = At(i + 1, j + 1);
                var d = At(i, j + 1);
                triangles.Add(new Triangle(a, b, c));
                triangles.Add(new Triangle(a, c, d));
            }
        }

        return new ListMesh(triangles);
    }

    [Fact]
    public void Reduces_a_dense_mesh_to_near_the_budget()
    {
        var mesh = Sphere(200);           // 80,000 triangles
        var bounds = mesh.ComputeBounds();

        var reduced = MeshDecimator.Reduce(mesh, bounds, 5_000);

        Assert.True(reduced.Triangles.Count <= 5_000,
            $"produced {reduced.Triangles.Count}, over the 5000 budget");
        Assert.True(reduced.Triangles.Count > 500,
            $"produced only {reduced.Triangles.Count}; the reduction is far too aggressive");
    }

    /// <summary>
    /// The shape must survive. Stride sampling passed a naive count check while
    /// destroying the surface, so this asserts the silhouette instead.
    /// </summary>
    [Fact]
    public void Keeps_the_original_shape_and_extent()
    {
        var mesh = Sphere(120);
        var bounds = mesh.ComputeBounds();

        var reduced = MeshDecimator.Reduce(mesh, bounds, 3_000);

        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var v in reduced.Vertices)
        {
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }

        // Within one grid cell of the original bounds on every axis.
        var tolerance = bounds.Size.Length() * 0.05f;
        Assert.True(Vector3.Distance(min, bounds.Min) < tolerance, $"min drifted: {min} vs {bounds.Min}");
        Assert.True(Vector3.Distance(max, bounds.Max) < tolerance, $"max drifted: {max} vs {bounds.Max}");
    }

    /// <summary>
    /// Every vertex must sit on the original surface. Snapping to cell centres
    /// would round corners off; keeping a real vertex per cell does not.
    /// </summary>
    [Fact]
    public void Every_output_vertex_is_an_original_vertex()
    {
        var mesh = Sphere(60);
        var original = mesh.ReadTriangles()
            .SelectMany(t => new[] { t.A, t.B, t.C })
            .ToHashSet();

        var reduced = MeshDecimator.Reduce(mesh, mesh.ComputeBounds(), 1_000);

        Assert.All(reduced.Vertices, v => Assert.Contains(v, original));
    }

    [Fact]
    public void Produces_no_degenerate_triangles()
    {
        var reduced = MeshDecimator.Reduce(Sphere(100), Sphere(100).ComputeBounds(), 2_000);

        Assert.All(reduced.Triangles, t =>
        {
            Assert.NotEqual(t.A, t.B);
            Assert.NotEqual(t.B, t.C);
            Assert.NotEqual(t.A, t.C);
        });
    }

    [Fact]
    public void Does_not_emit_the_same_triangle_twice()
    {
        var reduced = MeshDecimator.Reduce(Sphere(100), Sphere(100).ComputeBounds(), 2_000);

        var keys = reduced.Triangles
            .Select(t => { var v = new[] { t.A, t.B, t.C }; Array.Sort(v); return (v[0], v[1], v[2]); })
            .ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void An_empty_or_degenerate_mesh_is_handled()
    {
        var empty = new ListMesh([]);
        Assert.Empty(MeshDecimator.Reduce(empty, Bounds.Empty, 100).Triangles);

        var point = new Vector3(1, 1, 1);
        var degenerate = new ListMesh([new Triangle(point, point, point)]);
        var reduced = MeshDecimator.Reduce(degenerate, degenerate.ComputeBounds(), 100);
        Assert.Empty(reduced.Triangles);
    }

    [Fact]
    public void Reduction_can_be_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => MeshDecimator.Reduce(Sphere(100), Sphere(100).ComputeBounds(), 1000, cts.Token));
    }

    /// <summary>
    /// End to end through the payload: a mesh well over budget must come back
    /// reduced but still substantial, not a handful of scattered facets.
    /// </summary>
    [Fact]
    public void Payload_of_an_oversized_mesh_is_reduced_without_shredding()
    {
        var mesh = Sphere(200);           // 80,000 triangles
        var payload = MeshPayload.Build(mesh, 5_000);

        var count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4));
        Assert.True(count is > 500 and <= 5_000, $"payload carried {count} triangles");
    }
}
