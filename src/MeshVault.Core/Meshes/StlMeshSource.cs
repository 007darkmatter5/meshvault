using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace MeshVault.Core.Meshes;

/// <summary>
/// Reads binary and ASCII STL. Both are streamed, so a 129 MB file costs a
/// buffer rather than its full triangle list.
/// </summary>
public sealed class StlMeshSource : IMeshSource
{
    private const int BinaryHeaderBytes = 80;
    private const int BinaryTriangleBytes = 50;

    private readonly string _path;
    private readonly bool _isBinary;

    public int? TriangleCount { get; }

    public StlMeshSource(string path)
    {
        _path = path;
        var length = new FileInfo(path).Length;

        if (length < BinaryHeaderBytes + 4)
            throw new MeshFormatException("File is too small to be an STL.");

        using var stream = File.OpenRead(path);
        _isBinary = LooksBinary(stream, length, out var declared);
        if (_isBinary) TriangleCount = declared;
    }

    /// <summary>
    /// Some binary STLs begin with the word "solid", so the length check decides,
    /// not the leading bytes: a binary file's size is exactly the header plus
    /// its declared triangle count.
    /// </summary>
    private static bool LooksBinary(Stream stream, long length, out int declaredTriangles)
    {
        Span<byte> header = stackalloc byte[BinaryHeaderBytes + 4];
        stream.ReadExactly(header);
        declaredTriangles = BinaryPrimitives.ReadInt32LittleEndian(header[BinaryHeaderBytes..]);

        if (declaredTriangles < 0) return false;

        var expected = (long)BinaryHeaderBytes + 4 + (long)declaredTriangles * BinaryTriangleBytes;
        return expected == length;
    }

    public IEnumerable<Triangle> ReadTriangles(CancellationToken ct = default) =>
        _isBinary ? ReadBinary(ct) : ReadAscii(ct);

    private IEnumerable<Triangle> ReadBinary(CancellationToken ct)
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 16, FileOptions.SequentialScan);

        stream.Seek(BinaryHeaderBytes + 4, SeekOrigin.Begin);

        var buffer = new byte[BinaryTriangleBytes];
        for (var i = 0; i < TriangleCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            var read = stream.ReadAtLeast(buffer, BinaryTriangleBytes, throwOnEndOfStream: false);
            if (read < BinaryTriangleBytes) yield break; // truncated file: keep what we have

            // Bytes 0-11 hold the file's normal, which is often wrong or zero;
            // it is recomputed from the winding instead.
            yield return new Triangle(
                ReadVector(buffer, 12), ReadVector(buffer, 24), ReadVector(buffer, 36));
        }
    }

    private static Vector3 ReadVector(ReadOnlySpan<byte> buffer, int offset) => new(
        BinaryPrimitives.ReadSingleLittleEndian(buffer[offset..]),
        BinaryPrimitives.ReadSingleLittleEndian(buffer[(offset + 4)..]),
        BinaryPrimitives.ReadSingleLittleEndian(buffer[(offset + 8)..]));

    private IEnumerable<Triangle> ReadAscii(CancellationToken ct)
    {
        using var reader = new StreamReader(_path, Encoding.UTF8, true, 1 << 16);

        var vertices = new Vector3[3];
        var count = 0;

        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();

            var span = line.AsSpan().Trim();
            if (!span.StartsWith("vertex", StringComparison.OrdinalIgnoreCase)) continue;

            if (!TryParseVertex(span[6..], out var vertex)) continue;

            vertices[count++] = vertex;
            if (count < 3) continue;

            count = 0;
            yield return new Triangle(vertices[0], vertices[1], vertices[2]);
        }
    }

    private static bool TryParseVertex(ReadOnlySpan<char> span, out Vector3 vertex)
    {
        vertex = default;
        Span<float> values = stackalloc float[3];
        var found = 0;

        foreach (var range in span.Split(' '))
        {
            var token = span[range].Trim();
            if (token.IsEmpty) continue;
            if (found == 3) return false;

            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out values[found]))
                return false;
            found++;
        }

        if (found != 3) return false;
        vertex = new Vector3(values[0], values[1], values[2]);
        return true;
    }
}
