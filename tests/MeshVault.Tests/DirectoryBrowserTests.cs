using System.Diagnostics;
using MeshVault.Core.Services;

namespace MeshVault.Tests;

public class DirectoryBrowserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mv-br-" + Guid.NewGuid().ToString("N"));
    private readonly DirectoryBrowser _browser = new();

    public DirectoryBrowserTests() => Directory.CreateDirectory(_root);

    private string Dir(string relative)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(full);
        return full;
    }

    private void File_(string relative)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, "x");
    }

    private DirectoryEntry Entry(string path) => new(Path.GetFileName(path), path);

    [Fact]
    public void Lists_subdirectories_sorted_by_name()
    {
        Dir("Zebra");
        Dir("apple");
        Dir("Mango");

        var names = _browser.GetChildren(_root).Select(d => d.Name).ToList();

        Assert.Equal(["apple", "Mango", "Zebra"], names);
    }

    [Fact]
    public void Listing_defers_counting_so_it_does_no_per_child_work()
    {
        Dir("A");
        Dir("B");

        Assert.All(_browser.GetChildren(_root), e =>
        {
            Assert.Null(e.ModelFileCount);
            Assert.False(e.Probed);
        });
    }

    [Fact]
    public async Task Probe_counts_only_mesh_and_cad_files_directly_inside()
    {
        File_("Models/a.stl");
        File_("Models/b.3mf");
        File_("Models/c.step");
        File_("Models/readme.md");
        File_("Models/photo.png");
        File_("Models/Nested/deep.stl");

        var probed = await _browser.ProbeAsync(Entry(Path.Combine(_root, "Models")));

        // readme/photo are excluded, and the nested .stl does not count here.
        Assert.Equal(3, probed.ModelFileCount);
        Assert.True(probed.HasSubdirectories);
        Assert.True(probed.Probed);
    }

    [Fact]
    public async Task Probe_reports_folders_without_subdirectories()
    {
        var leaf = Dir("Leaf");

        var probed = await _browser.ProbeAsync(Entry(leaf));

        Assert.False(probed.HasSubdirectories);
        Assert.Equal(0, probed.ModelFileCount);
        Assert.True(probed.Accessible);
    }

    [Fact]
    public async Task Probe_caps_counting_so_one_huge_folder_cannot_stall_it()
    {
        for (var i = 0; i < DirectoryBrowser.MaxCountedFiles + 25; i++)
            File_($"Huge/part{i}.stl");

        var probed = await _browser.ProbeAsync(Entry(Path.Combine(_root, "Huge")));

        Assert.Equal(DirectoryBrowser.MaxCountedFiles, probed.ModelFileCount);
    }

    [Fact]
    public async Task Probe_marks_unreadable_folders_inaccessible_rather_than_throwing()
    {
        var probed = await _browser.ProbeAsync(Entry(Path.Combine(_root, "does-not-exist")));

        Assert.False(probed.Accessible);
        Assert.Null(probed.ModelFileCount);
    }

    [Fact]
    public async Task Probe_is_cancellable()
    {
        for (var i = 0; i < 200; i++) File_($"Many/part{i}.stl");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _browser.ProbeAsync(Entry(Path.Combine(_root, "Many")), cts.Token));
    }

    [Fact]
    public void Hides_dot_folders_and_system_noise()
    {
        Dir(".hidden");
        Dir("$Recycle.Bin");
        Dir("System Volume Information");
        Dir("@eaDir");
        Dir("Visible");

        var names = _browser.GetChildren(_root).Select(d => d.Name).ToList();

        Assert.Equal(["Visible"], names);
    }

    [Fact]
    public void Missing_or_unreadable_paths_return_empty_rather_than_throwing()
    {
        Assert.Empty(_browser.GetChildren(Path.Combine(_root, "does-not-exist")));
        Assert.False(_browser.Exists(Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void Parent_walks_up_and_stops_at_a_root()
    {
        var child = Dir("a/b");

        Assert.Equal(Path.Combine(_root, "a"), _browser.GetParent(child));

        var top = Path.GetPathRoot(_root)!;
        Assert.Null(_browser.GetParent(top));
    }

    [Fact]
    public void Breadcrumbs_are_cumulative_and_end_at_the_full_path()
    {
        var deep = Dir("a/b/c");

        var crumbs = DirectoryBrowser.GetBreadcrumbs(deep);

        Assert.Equal(deep, crumbs[^1].Path);
        Assert.Equal("c", crumbs[^1].Name);
        Assert.Equal(Path.Combine(_root, "a", "b"), crumbs[^2].Path);
        for (var i = 1; i < crumbs.Count; i++)
            Assert.StartsWith(crumbs[i - 1].Path, crumbs[i].Path);
    }

    /// <summary>
    /// Regression guard for the 4.8s dialog open: GetRoots used to probe every
    /// drive, which cost over a second each for mapped network drives.
    /// </summary>
    [Fact]
    public void Roots_are_returned_without_touching_any_drive()
    {
        // Warm up: the first call pays JIT and the first drive enumeration,
        // which is unrelated to the per-drive probing this guards against.
        _browser.GetRoots();

        var elapsed = Stopwatch.StartNew();
        var roots = _browser.GetRoots();
        elapsed.Stop();

        Assert.NotEmpty(roots);
        // The real assertion: nothing has been probed.
        Assert.All(roots, r => Assert.False(r.Probed));

        // Generous, because it runs on a shared machine. The regression it
        // catches was 4.9 seconds for six drives, so a second is ample and
        // does not turn ordinary scheduling noise into a failure.
        Assert.True(elapsed.ElapsedMilliseconds < 1000,
            $"GetRoots took {elapsed.ElapsedMilliseconds}ms; it must not probe each drive.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
