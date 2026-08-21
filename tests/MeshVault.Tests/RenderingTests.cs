using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using MeshVault.Core.Imaging;
using MeshVault.Core.Meshes;

namespace MeshVault.Tests;

public class RenderingTests
{
    /// <summary>An in-memory mesh, so rendering tests need no files.</summary>
    private sealed class InMemoryMesh(params Triangle[] triangles) : IMeshSource
    {
        public int? TriangleCount => triangles.Length;
        public IEnumerable<Triangle> ReadTriangles(CancellationToken ct = default) => triangles;
    }

    /// <summary>A solid axis-aligned box, which fills a good part of any view.</summary>
    private static IMeshSource Cube(float size = 10f)
    {
        var t = new List<Triangle>();
        var p = new Vector3[]
        {
            new(0, 0, 0), new(size, 0, 0), new(size, size, 0), new(0, size, 0),
            new(0, 0, size), new(size, 0, size), new(size, size, size), new(0, size, size),
        };
        void Quad(int a, int b, int c, int d)
        {
            t.Add(new Triangle(p[a], p[b], p[c]));
            t.Add(new Triangle(p[a], p[c], p[d]));
        }
        Quad(0, 3, 2, 1); Quad(4, 5, 6, 7); Quad(0, 1, 5, 4);
        Quad(2, 3, 7, 6); Quad(1, 2, 6, 5); Quad(0, 4, 7, 3);
        return new InMemoryMesh([.. t]);
    }

    // PNG encoding ----------------------------------------------------------

    /// <summary>Decodes our own PNG back to pixels, so the tests check real output.</summary>
    private static (int Width, int Height, byte[] Pixels) DecodePng(byte[] png)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);

        var offset = 8;
        int width = 0, height = 0;
        using var idat = new MemoryStream();

        while (offset < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);

            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                Assert.Equal(8, data[8]);   // bit depth
                Assert.Equal(6, data[9]);   // RGBA
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }

            offset += 12 + length;
        }

        idat.Position = 0;
        using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);

        var stride = width * 4;
        var bytes = raw.ToArray();
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, bytes[y * (stride + 1)]); // filter type None
            Array.Copy(bytes, y * (stride + 1) + 1, pixels, y * stride, stride);
        }

        return (width, height, pixels);
    }

    [Fact]
    public void Png_round_trips_through_a_decoder()
    {
        var pixels = new byte[4 * 3 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200; pixels[i + 1] = 100; pixels[i + 2] = 50; pixels[i + 3] = 255;
        }

        var (width, height, decoded) = DecodePng(PngEncoder.Encode(pixels, 4, 3));

        Assert.Equal(4, width);
        Assert.Equal(3, height);
        Assert.Equal(pixels, decoded);
    }

    [Fact]
    public void Png_rejects_a_buffer_that_is_too_small()
    {
        Assert.Throws<ArgumentException>(() => PngEncoder.Encode(new byte[10], 100, 100));
    }

    [Fact]
    public void Png_rejects_a_zero_size_image()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PngEncoder.Encode(new byte[4], 0, 1));
    }

    // Rasterising -----------------------------------------------------------

    [Fact]
    public void Rendering_a_cube_produces_an_image_of_the_requested_size()
    {
        var png = MeshRasterizer.RenderPng(Cube(), new RenderOptions { Width = 120, Height = 90 });

        var (width, height, _) = DecodePng(png);
        Assert.Equal(120, width);
        Assert.Equal(90, height);
    }

    [Fact]
    public void A_rendered_cube_actually_covers_much_of_the_frame()
    {
        var options = new RenderOptions { Width = 100, Height = 100 };
        var (_, _, pixels) = DecodePng(MeshRasterizer.RenderPng(Cube(), options));

        var background = options.Background;
        var drawn = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] != background[0] || pixels[i + 1] != background[1] || pixels[i + 2] != background[2])
                drawn++;
        }

        // A fitted cube seen from an angle should fill a large share of a square frame.
        var coverage = drawn / 10000.0;
        Assert.True(coverage > 0.35, $"cube covered only {coverage:P0} of the frame");
        Assert.True(coverage < 0.95, $"cube covered {coverage:P0}; the fit is not leaving a margin");
    }

    [Fact]
    public void An_empty_mesh_renders_as_background_rather_than_failing()
    {
        var options = new RenderOptions { Width = 20, Height = 20 };
        var (_, _, pixels) = DecodePng(MeshRasterizer.RenderPng(new InMemoryMesh(), options));

        for (var i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal(options.Background[0], pixels[i]);
            Assert.Equal(options.Background[1], pixels[i + 1]);
            Assert.Equal(options.Background[2], pixels[i + 2]);
        }
    }

    [Fact]
    public void Scale_does_not_change_framing()
    {
        // A large and a small cube should fill the frame identically, because the
        // camera fits to the model rather than assuming a size.
        var options = new RenderOptions { Width = 80, Height = 80 };
        var small = DecodePng(MeshRasterizer.RenderPng(Cube(1f), options)).Pixels;
        var large = DecodePng(MeshRasterizer.RenderPng(Cube(500f), options)).Pixels;

        Assert.Equal(small, large);
    }

    [Fact]
    public void Rotating_the_camera_changes_the_image()
    {
        var front = MeshRasterizer.RenderPng(Cube(), new RenderOptions { Yaw = 0, Pitch = 0 });
        var angled = MeshRasterizer.RenderPng(Cube(), new RenderOptions { Yaw = 45, Pitch = 30 });

        Assert.NotEqual(front, angled);
    }

    [Fact]
    public void Rendering_is_deterministic()
    {
        var options = new RenderOptions { Width = 60, Height = 60 };

        Assert.Equal(
            MeshRasterizer.RenderPng(Cube(), options),
            MeshRasterizer.RenderPng(Cube(), options));
    }

    /// <summary>
    /// Printable meshes are often inconsistently wound. Dropping back faces
    /// would punch holes, so both windings must be drawn.
    /// </summary>
    [Fact]
    public void Reversed_winding_still_draws()
    {
        var forward = new InMemoryMesh(new Triangle(
            new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 10, 0)));
        var reversed = new InMemoryMesh(new Triangle(
            new Vector3(0, 0, 0), new Vector3(0, 10, 0), new Vector3(10, 0, 0)));

        var options = new RenderOptions { Width = 40, Height = 40 };

        static int Drawn(byte[] png, RenderOptions o)
        {
            var (_, _, pixels) = DecodePng(png);
            var n = 0;
            for (var i = 0; i < pixels.Length; i += 4)
                if (pixels[i] != o.Background[0] || pixels[i + 1] != o.Background[1]) n++;
            return n;
        }

        Assert.True(Drawn(MeshRasterizer.RenderPng(forward, options), options) > 50);
        Assert.True(Drawn(MeshRasterizer.RenderPng(reversed, options), options) > 50);
    }

    [Fact]
    public void Rendering_can_be_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => MeshRasterizer.RenderPng(Cube(), new RenderOptions(), cts.Token));
    }
}
