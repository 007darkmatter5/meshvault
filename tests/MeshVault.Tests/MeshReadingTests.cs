using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using MeshVault.Core.Meshes;

namespace MeshVault.Tests;

public class MeshReadingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mv-mesh-" + Guid.NewGuid().ToString("N"));

    public MeshReadingTests() => Directory.CreateDirectory(_dir);

    private string Path_(string name) => System.IO.Path.Combine(_dir, name);

    /// <summary>Writes a binary STL of a unit tetrahedron (4 triangles).</summary>
    private string WriteBinaryStl(string name, int triangleCount = 4, string header = "binary stl")
    {
        var path = Path_(name);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        var head = new byte[80];
        Encoding.ASCII.GetBytes(header).CopyTo(head, 0);
        writer.Write(head);
        writer.Write(triangleCount);

        for (var i = 0; i < triangleCount; i++)
        {
            // Deliberately wrong stored normal: readers must recompute it.
            foreach (var f in new[] { 9f, 9f, 9f }) writer.Write(f);
            writer.Write(0f); writer.Write(0f); writer.Write(0f);
            writer.Write(1f); writer.Write(0f); writer.Write(0f);
            writer.Write(0f); writer.Write(i + 1f); writer.Write(0f);
            writer.Write((ushort)0);
        }
        return path;
    }

    private string WriteAsciiStl(string name)
    {
        var path = Path_(name);
        File.WriteAllText(path, """
            solid test
              facet normal 0 0 1
                outer loop
                  vertex 0 0 0
                  vertex 1 0 0
                  vertex 0 1 0
                endloop
              endfacet
              facet normal 0 0 1
                outer loop
                  vertex 0 0 2
                  vertex 3 0 2
                  vertex 0 4 2
                endloop
              endfacet
            endsolid test
            """);
        return path;
    }

    // STL -------------------------------------------------------------------

    [Fact]
    public void Binary_stl_reports_its_triangle_count_without_reading_them()
    {
        var source = new StlMeshSource(WriteBinaryStl("cube.stl", 12));

        Assert.Equal(12, source.TriangleCount);
    }

    [Fact]
    public void Binary_stl_reads_vertices_and_recomputes_normals()
    {
        var source = new StlMeshSource(WriteBinaryStl("t.stl", 1));

        var triangle = Assert.Single(source.ReadTriangles());
        Assert.Equal(new Vector3(0, 0, 0), triangle.A);
        Assert.Equal(new Vector3(1, 0, 0), triangle.B);
        Assert.Equal(new Vector3(0, 1, 0), triangle.C);
        // Stored normal was (9,9,9); the geometric one is +Z.
        Assert.Equal(Vector3.UnitZ, triangle.Normal());
    }

    /// <summary>
    /// Binary STLs written by some tools begin with the word "solid", which is
    /// also how ASCII files start. Length, not prefix, decides the format.
    /// </summary>
    [Fact]
    public void Binary_stl_beginning_with_solid_is_not_mistaken_for_ascii()
    {
        var source = new StlMeshSource(WriteBinaryStl("tricky.stl", 3, header: "solid something"));

        Assert.Equal(3, source.TriangleCount);
        Assert.Equal(3, source.ReadTriangles().Count());
    }

    [Fact]
    public void Ascii_stl_is_read()
    {
        var source = new StlMeshSource(WriteAsciiStl("a.stl"));

        Assert.Null(source.TriangleCount);
        var triangles = source.ReadTriangles().ToList();
        Assert.Equal(2, triangles.Count);
        Assert.Equal(new Vector3(3, 0, 2), triangles[1].B);
    }

    [Fact]
    public void A_truncated_binary_stl_yields_what_it_can_instead_of_throwing()
    {
        var path = WriteBinaryStl("cut.stl", 10);
        // Chop the file mid-triangle.
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
            stream.SetLength(84 + 50 * 4 + 20);

        // Declared count no longer matches the length, so it reads as ASCII and
        // finds nothing rather than crashing.
        var source = new StlMeshSource(path);
        Assert.Empty(source.ReadTriangles());
    }

    [Fact]
    public void Bounds_are_computed_across_all_triangles()
    {
        var source = new StlMeshSource(WriteAsciiStl("a.stl"));

        var bounds = source.ComputeBounds();

        Assert.Equal(new Vector3(0, 0, 0), bounds.Min);
        Assert.Equal(new Vector3(3, 4, 2), bounds.Max);
    }

    [Fact]
    public void Streaming_twice_gives_the_same_triangles()
    {
        var source = new StlMeshSource(WriteBinaryStl("t.stl", 5));

        Assert.Equal(source.ReadTriangles().ToList(), source.ReadTriangles().ToList());
    }

    // 3MF -------------------------------------------------------------------

    private string WriteThreeMf(string name, string modelXml)
    {
        var path = Path_(name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("3D/3dmodel.model");
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(modelXml);
        return path;
    }

    private const string OneTriangle = """
        <?xml version="1.0" encoding="UTF-8"?>
        <model unit="millimeter" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">
          <resources>
            <object id="1" type="model">
              <mesh>
                <vertices>
                  <vertex x="0" y="0" z="0" />
                  <vertex x="10" y="0" z="0" />
                  <vertex x="0" y="20" z="0" />
                </vertices>
                <triangles>
                  <triangle v1="0" v2="1" v3="2" />
                </triangles>
              </mesh>
            </object>
          </resources>
          <build><item objectid="1" /></build>
        </model>
        """;

    [Fact]
    public void ThreeMf_reads_mesh_geometry()
    {
        var source = new ThreeMfMeshSource(WriteThreeMf("m.3mf", OneTriangle));

        var triangle = Assert.Single(source.ReadTriangles());
        Assert.Equal(new Vector3(0, 0, 0), triangle.A);
        Assert.Equal(new Vector3(10, 0, 0), triangle.B);
        Assert.Equal(new Vector3(0, 20, 0), triangle.C);
    }

    [Fact]
    public void ThreeMf_applies_the_build_item_transform()
    {
        var withTransform = OneTriangle.Replace(
            """<item objectid="1" />""",
            """<item objectid="1" transform="1 0 0 0 1 0 0 0 1 100 200 300" />""");

        var source = new ThreeMfMeshSource(WriteThreeMf("t.3mf", withTransform));

        var triangle = Assert.Single(source.ReadTriangles());
        Assert.Equal(new Vector3(100, 200, 300), triangle.A);
        Assert.Equal(new Vector3(110, 200, 300), triangle.B);
    }

    [Fact]
    public void ThreeMf_skips_triangles_with_out_of_range_indices()
    {
        var bad = OneTriangle.Replace(
            """<triangle v1="0" v2="1" v3="2" />""",
            """<triangle v1="0" v2="1" v3="2" /><triangle v1="0" v2="1" v3="99" />""");

        var source = new ThreeMfMeshSource(WriteThreeMf("bad.3mf", bad));

        Assert.Single(source.ReadTriangles());
    }

    [Fact]
    public void ThreeMf_without_a_build_section_still_renders_its_objects()
    {
        var noBuild = OneTriangle.Replace("""<build><item objectid="1" /></build>""", "<build />");

        var source = new ThreeMfMeshSource(WriteThreeMf("nb.3mf", noBuild));

        Assert.Single(source.ReadTriangles());
    }

    [Fact]
    public void A_3mf_with_no_model_part_is_reported_clearly()
    {
        var path = Path_("empty.3mf");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            archive.CreateEntry("something.txt");

        var source = new ThreeMfMeshSource(path);
        var ex = Assert.Throws<MeshFormatException>(() => source.ReadTriangles().ToList());
        Assert.Contains("3MF", ex.Message);
    }

    // Loader ----------------------------------------------------------------

    [Theory]
    [InlineData(".stl", true)]
    [InlineData(".STL", true)]
    [InlineData(".3mf", true)]
    [InlineData(".obj", false)]
    [InlineData(".step", false)]
    public void Loader_knows_which_formats_it_can_read(string extension, bool expected)
    {
        Assert.Equal(expected, MeshLoader.CanRead(extension));
    }

    [Fact]
    public void Loader_picks_the_reader_by_extension()
    {
        Assert.IsType<StlMeshSource>(MeshLoader.Open(WriteBinaryStl("x.stl", 1)));
        Assert.IsType<ThreeMfMeshSource>(MeshLoader.Open(WriteThreeMf("x.3mf", OneTriangle)));
        Assert.Throws<MeshFormatException>(() => MeshLoader.Open("x.obj"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
