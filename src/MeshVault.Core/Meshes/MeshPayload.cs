using System.Buffers.Binary;
using System.Numerics;

namespace MeshVault.Core.Meshes;

/// <summary>
/// Packs a mesh into a compact binary form for the browser viewer.
/// </summary>
/// <remarks>
/// Layout, little-endian:
///   magic "MVM1" (4 bytes)
///   triangleCount : int32
///   min x,y,z     : 3 x float32
///   scale         : float32   (world units per quantisation step)
///   positions     : triangleCount * 9 * int16
///
/// Positions are quantised to 16 bits across the bounding box, which is well
/// under a pixel of error at any sane viewing size, and normals are omitted
/// because the shader derives them per fragment. That is 18 bytes per triangle
/// against 50 in a binary STL, before decimation.
/// </remarks>
public static class MeshPayload
{
    public const int HeaderBytes = 4 + 4 + 12 + 4;
    private const int BytesPerTriangle = 9 * sizeof(short);
    private static readonly byte[] Magic = "MVM1"u8.ToArray();

    /// <summary>
    /// A valid payload carrying no triangles. Used to mark a file the builder
    /// could not read, so it is not retried on every idle pass.
    /// </summary>
    public static byte[] EmptyPayload()
    {
        var buffer = new byte[HeaderBytes];
        WriteHeader(buffer, 0, Vector3.Zero, 1f);
        return buffer;
    }

    public static byte[] Build(IMeshSource mesh, int triangleBudget, CancellationToken ct = default)
    {
        var bounds = mesh.ComputeBounds(ct);
        var total = mesh.CountTriangles(ct);

        // Over budget: cluster rather than drop triangles. Stride sampling used
        // to live here and shredded dense models, leaving the interior visible
        // through the gaps.
        if (triangleBudget > 0 && total > triangleBudget)
            return BuildFromIndexed(MeshDecimator.Reduce(mesh, bounds, triangleBudget, ct), bounds);

        var buffer = new byte[HeaderBytes + total * BytesPerTriangle];

        var (min, scale) = Frame(bounds);
        WriteHeader(buffer, total, min, scale);

        var offset = HeaderBytes;
        var written = 0;

        foreach (var triangle in mesh.ReadTriangles(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (written >= total) break;

            offset = Write(buffer, offset, triangle.A, min, scale);
            offset = Write(buffer, offset, triangle.B, min, scale);
            offset = Write(buffer, offset, triangle.C, min, scale);
            written++;
        }

        // A file can report more triangles than it holds; trim to what was real.
        if (written != total)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), written);
            Array.Resize(ref buffer, HeaderBytes + written * BytesPerTriangle);
        }

        return buffer;
    }

    private static byte[] BuildFromIndexed(IndexedMesh mesh, Bounds bounds)
    {
        var buffer = new byte[HeaderBytes + mesh.Triangles.Count * BytesPerTriangle];
        var (min, scale) = Frame(bounds);
        WriteHeader(buffer, mesh.Triangles.Count, min, scale);

        var offset = HeaderBytes;
        foreach (var (a, b, c) in mesh.Triangles)
        {
            offset = Write(buffer, offset, mesh.Vertices[a], min, scale);
            offset = Write(buffer, offset, mesh.Vertices[b], min, scale);
            offset = Write(buffer, offset, mesh.Vertices[c], min, scale);
        }

        return buffer;
    }

    /// <summary>
    /// Quantisation frame: 65535 steps across the largest axis, the same scale
    /// on all three so the model's proportions survive.
    /// </summary>
    private static (Vector3 Min, float Scale) Frame(Bounds bounds)
    {
        var min = bounds.IsEmpty ? Vector3.Zero : bounds.Min;
        var size = bounds.IsEmpty ? Vector3.One : bounds.Size;

        var extent = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        if (extent <= 0) extent = 1f;

        return (min, extent / 65535f);
    }

    private static void WriteHeader(byte[] buffer, int triangleCount, Vector3 min, float scale)
    {
        Magic.CopyTo(buffer, 0);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), triangleCount);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(8), min.X);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(12), min.Y);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(16), min.Z);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(20), scale);
    }

    private static int Write(byte[] buffer, int offset, Vector3 v, Vector3 min, float scale)
    {
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset), Quantize(v.X, min.X, scale));
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset + 2), Quantize(v.Y, min.Y, scale));
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset + 4), Quantize(v.Z, min.Z, scale));
        return offset + 6;
    }

    /// <summary>Maps a coordinate onto the signed 16-bit range used by the payload.</summary>
    private static short Quantize(float value, float min, float scale)
    {
        var steps = (value - min) / scale;
        return (short)(Math.Clamp(steps, 0, 65535) - 32768);
    }
}
