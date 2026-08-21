using System.Buffers.Binary;
using System.Numerics;
using MeshVault.Core.Meshes;

namespace MeshVault.Tests;

public class MeshPayloadTests
{
    private sealed class InMemoryMesh(params Triangle[] triangles) : IMeshSource
    {
        public int? TriangleCount => triangles.Length;
        public IEnumerable<Triangle> ReadTriangles(CancellationToken ct = default) => triangles;
    }

    /// <summary>
    /// A tessellated square surface. A long thin strip would be pathological for
    /// a clustering reducer and is not what a real model looks like.
    /// </summary>
    private static IMeshSource Grid(int count)
    {
        var side = (int)Math.Ceiling(Math.Sqrt(count / 2.0));
        var triangles = new List<Triangle>(count);

        for (var y = 0; y < side && triangles.Count < count; y++)
        {
            for (var x = 0; x < side && triangles.Count < count; x++)
            {
                var a = new Vector3(x, y, 0);
                var b = new Vector3(x + 1, y, 0);
                var c = new Vector3(x + 1, y + 1, 0);
                var d = new Vector3(x, y + 1, 0);

                triangles.Add(new Triangle(a, b, c));
                if (triangles.Count < count) triangles.Add(new Triangle(a, c, d));
            }
        }

        return new InMemoryMesh([.. triangles]);
    }

    /// <summary>Mirrors the decoding the browser module performs.</summary>
    private static (int Count, Vector3 Min, float Scale, Vector3[] Positions) Decode(byte[] payload)
    {
        Assert.Equal("MVM1"u8.ToArray(), payload[..4]);

        var count = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4));
        var min = new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(8)),
            BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(12)),
            BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(16)));
        var scale = BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(20));

        var positions = new Vector3[count * 3];
        var offset = MeshPayload.HeaderBytes;
        for (var i = 0; i < positions.Length; i++)
        {
            positions[i] = new Vector3(
                (BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(offset)) + 32768) * scale + min.X,
                (BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(offset + 2)) + 32768) * scale + min.Y,
                (BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(offset + 4)) + 32768) * scale + min.Z);
            offset += 6;
        }

        return (count, min, scale, positions);
    }

    [Fact]
    public void Payload_round_trips_positions_within_quantisation_error()
    {
        var mesh = new InMemoryMesh(new Triangle(
            new Vector3(0, 0, 0), new Vector3(100, 0, 0), new Vector3(0, 50, 25)));

        var (count, _, scale, positions) = Decode(MeshPayload.Build(mesh, 10_000));

        Assert.Equal(1, count);
        // 16 bits across a 100-unit model is far finer than a pixel at any sane size.
        var tolerance = scale * 2;
        Assert.True(Vector3.Distance(positions[0], new Vector3(0, 0, 0)) < tolerance);
        Assert.True(Vector3.Distance(positions[1], new Vector3(100, 0, 0)) < tolerance);
        Assert.True(Vector3.Distance(positions[2], new Vector3(0, 50, 25)) < tolerance);
    }

    [Fact]
    public void Payload_is_much_smaller_than_the_equivalent_binary_stl()
    {
        var payload = MeshPayload.Build(Grid(1000), 100_000);

        // Binary STL is 50 bytes per triangle; this must be 18 plus a header.
        var stlBytes = 84 + 1000 * 50;
        Assert.Equal(MeshPayload.HeaderBytes + 1000 * 18, payload.Length);
        Assert.True(payload.Length < stlBytes / 2);
    }

    [Fact]
    public void Large_meshes_are_decimated_to_the_budget()
    {
        var payload = MeshPayload.Build(Grid(10_000), triangleBudget: 1_000);

        var (count, _, _, _) = Decode(payload);
        Assert.True(count <= 1_000, $"kept {count} triangles for a budget of 1000");
        Assert.True(count >= 900, $"decimation threw away too much: {count}");
    }

    [Fact]
    public void Meshes_within_budget_are_sent_whole()
    {
        var (count, _, _, _) = Decode(MeshPayload.Build(Grid(500), triangleBudget: 1_000));

        Assert.Equal(500, count);
    }

    [Fact]
    public void An_empty_mesh_produces_a_valid_header_with_no_triangles()
    {
        var payload = MeshPayload.Build(new InMemoryMesh(), 1_000);

        var (count, _, _, _) = Decode(payload);
        Assert.Equal(0, count);
        Assert.Equal(MeshPayload.HeaderBytes, payload.Length);
    }

    [Fact]
    public void A_degenerate_mesh_does_not_divide_by_zero()
    {
        // Every vertex identical: the bounding box has no extent.
        var point = new Vector3(5, 5, 5);
        var payload = MeshPayload.Build(new InMemoryMesh(new Triangle(point, point, point)), 100);

        var (count, _, scale, positions) = Decode(payload);
        Assert.Equal(1, count);
        Assert.True(scale > 0);
        Assert.All(positions, p => Assert.False(float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z)));
    }

    [Fact]
    public void Building_can_be_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => MeshPayload.Build(Grid(1000), 1000, cts.Token));
    }
}
