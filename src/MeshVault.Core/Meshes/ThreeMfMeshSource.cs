using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Xml;

namespace MeshVault.Core.Meshes;

/// <summary>
/// Reads 3MF, which is a zip holding an XML model part. Only geometry is read:
/// objects are resolved through the build section so that components placed
/// with a transform land where the slicer would put them.
/// </summary>
public sealed class ThreeMfMeshSource(string path) : IMeshSource
{
    private const string ModelPart = "3D/3dmodel.model";

    public int? TriangleCount => null;

    public IEnumerable<Triangle> ReadTriangles(CancellationToken ct = default)
    {
        var document = LoadModel();

        // Objects may reference each other via <components>, so resolve meshes
        // first and then walk the build items that place them.
        var objects = ReadObjects(document, ct);
        var items = ReadBuildItems(document);

        // A 3MF with no build section still has drawable objects.
        if (items.Count == 0)
        {
            foreach (var mesh in objects.Values)
                foreach (var triangle in mesh.Triangles) yield return triangle;
            yield break;
        }

        foreach (var (objectId, transform) in items)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var triangle in Emit(objectId, transform, objects, depth: 0))
                yield return triangle;
        }
    }

    private static IEnumerable<Triangle> Emit(
        string objectId, Matrix4x4 transform, Dictionary<string, ObjectNode> objects, int depth)
    {
        // Guards against a malformed file whose components reference each other.
        if (depth > 8 || !objects.TryGetValue(objectId, out var node)) yield break;

        foreach (var triangle in node.Triangles)
        {
            yield return new Triangle(
                Vector3.Transform(triangle.A, transform),
                Vector3.Transform(triangle.B, transform),
                Vector3.Transform(triangle.C, transform));
        }

        foreach (var (childId, childTransform) in node.Components)
        {
            foreach (var triangle in Emit(childId, childTransform * transform, objects, depth + 1))
                yield return triangle;
        }
    }

    private sealed record ObjectNode(
        List<Triangle> Triangles, List<(string Id, Matrix4x4 Transform)> Components);

    private XmlDocument LoadModel()
    {
        using var archive = ZipFile.OpenRead(path);

        var entry = archive.GetEntry(ModelPart)
            ?? archive.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase))
            ?? throw new MeshFormatException("No 3D model part found inside the 3MF.");

        using var stream = entry.Open();
        var document = new XmlDocument();
        // 3MF is not a place for external entities.
        document.XmlResolver = null;
        document.Load(stream);
        return document;
    }

    private static Dictionary<string, ObjectNode> ReadObjects(XmlDocument document, CancellationToken ct)
    {
        var objects = new Dictionary<string, ObjectNode>(StringComparer.Ordinal);

        foreach (XmlElement element in document.GetElementsByTagName("object"))
        {
            ct.ThrowIfCancellationRequested();

            var id = element.GetAttribute("id");
            if (string.IsNullOrEmpty(id)) continue;

            var triangles = new List<Triangle>();
            var components = new List<(string, Matrix4x4)>();

            foreach (XmlElement mesh in element.GetElementsByTagName("mesh"))
                ReadMesh(mesh, triangles, ct);

            foreach (XmlElement component in element.GetElementsByTagName("component"))
            {
                var childId = component.GetAttribute("objectid");
                if (!string.IsNullOrEmpty(childId))
                    components.Add((childId, ParseTransform(component.GetAttribute("transform"))));
            }

            objects[id] = new ObjectNode(triangles, components);
        }

        return objects;
    }

    private static void ReadMesh(XmlElement mesh, List<Triangle> triangles, CancellationToken ct)
    {
        var vertices = new List<Vector3>();

        foreach (XmlElement vertex in mesh.GetElementsByTagName("vertex"))
        {
            vertices.Add(new Vector3(
                ParseFloat(vertex.GetAttribute("x")),
                ParseFloat(vertex.GetAttribute("y")),
                ParseFloat(vertex.GetAttribute("z"))));
        }

        foreach (XmlElement triangle in mesh.GetElementsByTagName("triangle"))
        {
            ct.ThrowIfCancellationRequested();

            if (!int.TryParse(triangle.GetAttribute("v1"), out var v1)
                || !int.TryParse(triangle.GetAttribute("v2"), out var v2)
                || !int.TryParse(triangle.GetAttribute("v3"), out var v3))
                continue;

            // Skip rather than throw: one bad index should not lose the model.
            if (!InRange(v1, vertices.Count) || !InRange(v2, vertices.Count) || !InRange(v3, vertices.Count))
                continue;

            triangles.Add(new Triangle(vertices[v1], vertices[v2], vertices[v3]));
        }
    }

    private static bool InRange(int index, int count) => index >= 0 && index < count;

    private static List<(string ObjectId, Matrix4x4 Transform)> ReadBuildItems(XmlDocument document)
    {
        var items = new List<(string, Matrix4x4)>();

        foreach (XmlElement build in document.GetElementsByTagName("build"))
        {
            foreach (XmlElement item in build.GetElementsByTagName("item"))
            {
                var objectId = item.GetAttribute("objectid");
                if (!string.IsNullOrEmpty(objectId))
                    items.Add((objectId, ParseTransform(item.GetAttribute("transform"))));
            }
        }

        return items;
    }

    /// <summary>3MF transforms are 12 numbers: a 3x3 basis followed by a translation.</summary>
    private static Matrix4x4 ParseTransform(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Matrix4x4.Identity;

        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 12) return Matrix4x4.Identity;

        Span<float> m = stackalloc float[12];
        for (var i = 0; i < 12; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out m[i]))
                return Matrix4x4.Identity;
        }

        return new Matrix4x4(
            m[0], m[1], m[2], 0,
            m[3], m[4], m[5], 0,
            m[6], m[7], m[8], 0,
            m[9], m[10], m[11], 1);
    }

    private static float ParseFloat(string? value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0f;
}
