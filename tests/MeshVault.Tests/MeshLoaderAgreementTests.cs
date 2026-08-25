using MeshVault.Core.Meshes;
using MeshVault.Core.Models;

namespace MeshVault.Tests;

/// <summary>
/// The indexer queues a preview for anything <see cref="FileKinds.CanThumbnail"/>
/// accepts, and the worker then hands the file to <see cref="MeshLoader"/>. If
/// those two disagree, every file in the gap is read off the library share only
/// to be refused, and shows up as a failed preview that could never have worked.
/// </summary>
public class MeshLoaderAgreementTests
{
    [Theory]
    [InlineData(".stl")]
    [InlineData(".3mf")]
    [InlineData(".obj")]
    [InlineData(".ply")]
    [InlineData(".step")]
    [InlineData(".gcode")]
    [InlineData(".png")]
    [InlineData(".zip")]
    public void Nothing_is_queued_for_a_preview_that_cannot_be_read(string extension)
    {
        Assert.Equal(MeshLoader.CanRead(extension), FileKinds.CanThumbnail(extension));
    }

    [Theory]
    [InlineData(".STL")]
    [InlineData(".3MF")]
    public void The_check_ignores_casing(string extension)
    {
        Assert.True(FileKinds.CanThumbnail(extension));
    }
}
