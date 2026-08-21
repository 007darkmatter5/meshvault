using MeshVault.Core.Models;
using MeshVault.Core.Services;

namespace MeshVault.Tests;

public class FolderScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mv-" + Guid.NewGuid().ToString("N"));

    private void File_(string relative, string content = "x")
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
    }

    [Fact]
    public void Groups_each_mesh_folder_into_one_model()
    {
        File_("Dragon/dragon.stl");
        File_("Dragon/preview.png");
        File_("Dragon/photos/printed.jpg");
        File_("Boat/benchy.3mf");

        var models = new FolderScanner().Scan(_root).OrderBy(m => m.Name).ToList();

        Assert.Equal(2, models.Count);
        Assert.Equal("Boat", models[0].Name);

        var dragon = models[1];
        Assert.Equal("Dragon", dragon.Name);
        Assert.Equal("Dragon", dragon.RelativePath);
        // The photos/ subfolder holds no meshes, so it is absorbed into Dragon.
        Assert.Equal(3, dragon.Files.Count);
        Assert.Contains(dragon.Files, f => f.RelativePath == "Dragon/photos/printed.jpg");
    }

    [Fact]
    public void Nested_mesh_folder_becomes_its_own_model()
    {
        File_("Castle/castle.stl");
        File_("Castle/Tower/tower.stl");

        var models = new FolderScanner().Scan(_root).OrderBy(m => m.Name).ToList();

        Assert.Equal(2, models.Count);
        Assert.Equal("Castle", models[0].Name);
        Assert.Equal("Tower", models[1].Name);
        // Castle must not swallow Tower's file.
        Assert.Single(models[0].Files);
    }

    [Fact]
    public void Folders_without_meshes_are_not_models()
    {
        File_("JustPhotos/a.png");
        File_("JustPhotos/b.png");

        Assert.Empty(new FolderScanner().Scan(_root));
    }

    [Fact]
    public void Ignores_dot_folders_and_nas_metadata()
    {
        File_(".git/objects/thing.stl");
        File_("@eaDir/thumb.stl");
        File_("Real/real.stl");

        var models = new FolderScanner().Scan(_root).ToList();

        Assert.Single(models);
        Assert.Equal("Real", models[0].Name);
    }

    [Fact]
    public void Classifies_extensions_and_totals_size()
    {
        File_("Kit/part.stl", new string('a', 100));
        File_("Kit/notes.md", new string('b', 10));
        File_("Kit/print.gcode", new string('c', 5));

        var model = Assert.Single(new FolderScanner().Scan(_root));

        Assert.Equal(115, model.TotalBytes);
        Assert.Equal(FileKind.Mesh, model.Files.Single(f => f.Extension == ".stl").Kind);
        Assert.Equal(FileKind.Document, model.Files.Single(f => f.Extension == ".md").Kind);
        Assert.Equal(FileKind.Sliced, model.Files.Single(f => f.Extension == ".gcode").Kind);
    }

    [Fact]
    public void Loose_meshes_at_the_root_form_a_model()
    {
        File_("orphan.stl");

        var model = Assert.Single(new FolderScanner().Scan(_root));

        Assert.Equal("", model.RelativePath);
        Assert.Equal("orphan.stl", model.Files[0].RelativePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
