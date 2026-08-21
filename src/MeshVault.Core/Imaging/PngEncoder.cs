using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MeshVault.Core.Imaging;

/// <summary>
/// Writes 8-bit RGBA PNGs using only the BCL. Avoids a native imaging
/// dependency, which keeps the Linux container image slim and side-steps
/// ImageSharp's commercial licence.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <param name="pixels">RGBA, 4 bytes per pixel, top row first.</param>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Image must have a positive size.");
        if (pixels.Length < width * height * 4)
            throw new ArgumentException("Pixel buffer is smaller than the stated size.", nameof(pixels));

        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // colour type: truecolour with alpha
        ihdr[10] = 0; // deflate
        ihdr[11] = 0; // adaptive filtering
        ihdr[12] = 0; // no interlace
        WriteChunk(output, "IHDR", ihdr);

        WriteChunk(output, "IDAT", Compress(pixels, width, height));
        WriteChunk(output, "IEND", []);

        return output.ToArray();
    }

    private static byte[] Compress(ReadOnlySpan<byte> pixels, int width, int height)
    {
        var stride = width * 4;
        var raw = new byte[(stride + 1) * height];

        // Filter type 0 (None) per scanline. Up-filtering would compress a
        // little better but costs a pass; thumbnails are small either way.
        for (var y = 0; y < height; y++)
        {
            var source = y * stride;
            var destination = y * (stride + 1);
            raw[destination] = 0;
            pixels.Slice(source, stride).CopyTo(raw.AsSpan(destination + 1));
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw);
        }
        return compressed.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crc = Crc32.Compute(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in first) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            foreach (var b in second) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
