using MeshVault.Core.Meshes;

namespace MeshVault.Tests;

/// <summary>
/// Staged copies are transient. A process killed mid-scan leaves one behind for
/// every model it was working on, and on a long-lived server those accumulate
/// until they fill the disk.
/// </summary>
public class StagedMeshFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mv-stg-" + Guid.NewGuid().ToString("N"));

    public StagedMeshFileTests() => Directory.CreateDirectory(_dir);

    private string Stranded(string name, int bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void Clears_files_left_by_a_previous_run_and_reports_the_space()
    {
        Stranded("mv-stage-aaa.stl", 2048);
        Stranded("mv-stage-bbb.3mf", 1024);

        var reclaimed = StagedMeshFile.CleanUp(_dir);

        Assert.Equal(3072, reclaimed);
        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    /// <summary>Only our own staged copies, in case the folder is shared.</summary>
    [Fact]
    public void Leaves_files_it_did_not_create()
    {
        Stranded("mv-stage-aaa.stl", 512);
        var other = Stranded("something-else.stl", 512);

        StagedMeshFile.CleanUp(_dir);

        Assert.True(File.Exists(other));
        Assert.False(File.Exists(Path.Combine(_dir, "mv-stage-aaa.stl")));
    }

    [Fact]
    public void A_missing_directory_is_not_an_error()
    {
        Assert.Equal(0, StagedMeshFile.CleanUp(Path.Combine(_dir, "not-there")));
    }

    [Fact]
    public async Task Small_local_files_are_used_in_place_rather_than_copied()
    {
        var path = Stranded("model.stl", 1024);

        using var staged = await StagedMeshFile.CreateAsync(path);

        Assert.False(staged.WasStaged);
        Assert.Equal(path, staged.Path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
