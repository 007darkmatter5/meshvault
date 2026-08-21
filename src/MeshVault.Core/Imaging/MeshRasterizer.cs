using System.Numerics;
using MeshVault.Core.Meshes;

namespace MeshVault.Core.Imaging;

public record RenderOptions
{
    public int Width { get; init; } = 400;
    public int Height { get; init; } = 300;
    /// <summary>Rendered at this multiple then box-filtered down, which is what removes the jaggies.</summary>
    public int Supersample { get; init; } = 2;
    /// <summary>Turntable angle in degrees, so a snapshot angle can be reproduced server-side.</summary>
    public float Yaw { get; init; } = 35f;
    /// <summary>Elevation in degrees above the horizon.</summary>
    public float Pitch { get; init; } = 25f;
    /// <summary>Z is up for 3D printing, which is also what 3MF and most STLs assume.</summary>
    public bool ZUp { get; init; } = true;

    public byte[] Background { get; init; } = [0x1a, 0x1e, 0x25, 0xff];
    public Vector3 BaseColor { get; init; } = new(0.42f, 0.62f, 0.85f);
}

/// <summary>
/// Renders a mesh to an RGBA image with a z-buffer and flat shading. Pure
/// software: no GPU, no native library, so it runs the same in a slim container
/// as it does on a workstation.
/// </summary>
public static class MeshRasterizer
{
    public static byte[] RenderPng(IMeshSource mesh, RenderOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new RenderOptions();
        var (pixels, width, height) = Render(mesh, options, ct);
        return PngEncoder.Encode(pixels, width, height);
    }

    public static (byte[] Pixels, int Width, int Height) Render(
        IMeshSource mesh, RenderOptions options, CancellationToken ct = default)
    {
        var scale = Math.Clamp(options.Supersample, 1, 4);
        var width = options.Width * scale;
        var height = options.Height * scale;

        var color = new Vector3[width * height];
        var depth = new float[width * height];
        // Larger Z is nearer after the view rotation, so the buffer starts at
        // negative infinity and keeps the maximum.
        Array.Fill(depth, float.NegativeInfinity);

        var background = new Vector3(
            options.Background[0] / 255f, options.Background[1] / 255f, options.Background[2] / 255f);
        Array.Fill(color, background);

        // Two passes, not three: the framing pass already establishes whether
        // there is any geometry, so a separate bounds pass would just be another
        // full read of the file.
        var view = BuildView(options);
        var (fit, offset, hasGeometry) = FitToScreen(mesh, view, width, height, ct);

        if (hasGeometry)
        {
            foreach (var triangle in mesh.ReadTriangles(ct))
            {
                ct.ThrowIfCancellationRequested();
                DrawTriangle(triangle, view, fit, offset, width, height, color, depth, options);
            }
        }

        return (Downsample(color, width, height, scale, options.Background[3]), options.Width, options.Height);
    }

    /// <summary>Rotation that takes model space into view space, with +Y up on screen.</summary>
    private static Matrix4x4 BuildView(RenderOptions options)
    {
        // Z-up models need a -90 degree tilt about X before the turntable, so
        // the printed "up" direction ends up pointing up on screen.
        var toYUp = options.ZUp
            ? Matrix4x4.CreateRotationX(-MathF.PI / 2f)
            : Matrix4x4.Identity;

        var yaw = Matrix4x4.CreateRotationY(options.Yaw * MathF.PI / 180f);
        var pitch = Matrix4x4.CreateRotationX(-options.Pitch * MathF.PI / 180f);

        return toYUp * yaw * pitch;
    }

    /// <summary>
    /// Orthographic fit. The projected extent is measured rather than derived
    /// from the bounding box, because a rotated box's corners overstate how much
    /// room the model actually needs.
    /// </summary>
    private static (float Scale, Vector2 Offset, bool HasGeometry) FitToScreen(
        IMeshSource mesh, Matrix4x4 view, int width, int height, CancellationToken ct)
    {
        var min = new Vector2(float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity);
        var any = false;

        foreach (var triangle in mesh.ReadTriangles(ct))
        {
            ct.ThrowIfCancellationRequested();
            any = true;

            foreach (var vertex in new[] { triangle.A, triangle.B, triangle.C })
            {
                var v = Vector3.Transform(vertex, view);
                min = Vector2.Min(min, new Vector2(v.X, v.Y));
                max = Vector2.Max(max, new Vector2(v.X, v.Y));
            }
        }

        var size = max - min;
        // A single point or a degenerate sliver has no extent to fit to.
        if (!any || size.X <= 0 || size.Y <= 0)
            return (1f, new Vector2(width / 2f, height / 2f), false);

        const float margin = 0.88f;
        var scale = MathF.Min(width / size.X, height / size.Y) * margin;

        var center = (min + max) * 0.5f;
        var offset = new Vector2(width / 2f, height / 2f) - center * new Vector2(scale, -scale);
        return (scale, offset, true);
    }

    private static void DrawTriangle(
        Triangle triangle, Matrix4x4 view, float scale, Vector2 offset,
        int width, int height, Vector3[] color, float[] depth, RenderOptions options)
    {
        var a = Vector3.Transform(triangle.A, view);
        var b = Vector3.Transform(triangle.B, view);
        var c = Vector3.Transform(triangle.C, view);

        var pa = Project(a, scale, offset);
        var pb = Project(b, scale, offset);
        var pc = Project(c, scale, offset);

        var area = Edge(pa, pb, pc);
        if (MathF.Abs(area) < 1e-6f) return;

        // Back faces are kept: printable meshes are frequently inconsistently
        // wound, and dropping them punches holes in the render.
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        if (area < 0) normal = -normal;

        var shade = Shade(normal, options);

        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.X, MathF.Min(pb.X, pc.X))));
        var maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(pa.X, MathF.Max(pb.X, pc.X))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y))));
        var maxY = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y))));

        var inverseArea = 1f / area;

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);

                var w0 = Edge(pb, pc, p) * inverseArea;
                var w1 = Edge(pc, pa, p) * inverseArea;
                var w2 = Edge(pa, pb, p) * inverseArea;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;

                // Larger Z is nearer after the view rotation, so keep the max.
                var z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                var index = y * width + x;
                if (z <= depth[index]) continue;

                depth[index] = z;
                color[index] = shade;
            }
        }
    }

    private static Vector3 Shade(Vector3 normal, RenderOptions options)
    {
        // Key light over the viewer's shoulder plus a dim fill from below, which
        // keeps downward faces from going pure black.
        var key = Vector3.Normalize(new Vector3(-0.4f, 0.6f, 1.0f));
        var fill = Vector3.Normalize(new Vector3(0.5f, -0.4f, 0.3f));

        var lambert = MathF.Max(0, Vector3.Dot(normal, key));
        var fillTerm = MathF.Max(0, Vector3.Dot(normal, fill)) * 0.25f;
        var intensity = 0.18f + 0.82f * lambert + fillTerm;

        var lit = options.BaseColor * intensity;
        return new Vector3(
            MathF.Min(1f, lit.X), MathF.Min(1f, lit.Y), MathF.Min(1f, lit.Z));
    }

    private static Vector2 Project(Vector3 v, float scale, Vector2 offset) =>
        new(v.X * scale + offset.X, -v.Y * scale + offset.Y);

    private static float Edge(Vector2 a, Vector2 b, Vector2 c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static byte[] Downsample(Vector3[] color, int width, int height, int scale, byte alpha)
    {
        var outWidth = width / scale;
        var outHeight = height / scale;
        var pixels = new byte[outWidth * outHeight * 4];
        var samples = scale * scale;

        for (var y = 0; y < outHeight; y++)
        {
            for (var x = 0; x < outWidth; x++)
            {
                var sum = Vector3.Zero;
                for (var sy = 0; sy < scale; sy++)
                    for (var sx = 0; sx < scale; sx++)
                        sum += color[(y * scale + sy) * width + (x * scale + sx)];

                var average = sum / samples;
                var index = (y * outWidth + x) * 4;
                pixels[index] = ToByte(average.X);
                pixels[index + 1] = ToByte(average.Y);
                pixels[index + 2] = ToByte(average.Z);
                pixels[index + 3] = alpha;
            }
        }

        return pixels;
    }

    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}
