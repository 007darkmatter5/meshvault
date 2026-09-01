using MeshVault.Web.Endpoints;
using Microsoft.AspNetCore.Http;

namespace MeshVault.Tests;

/// <summary>
/// Media requests must bypass the not-found page. Re-executing it answered an
/// img tag with a full HTML error page, and replaying a POST body through the
/// Blazor endpoint turned a 404 into a content-type 400.
/// </summary>
public class MediaPathTests
{
    [Theory]
    [InlineData("/thumb/model/7")]
    [InlineData("/thumb/file/15")]
    [InlineData("/mesh/299")]
    [InlineData("/snapshot/8")]
    [InlineData("/THUMB/model/7")]
    [InlineData("/download/file/15")]
    [InlineData("/download/model/7")]
    [InlineData("/download/collection/3")]
    public void Media_paths_are_recognised(string path)
    {
        Assert.True(MediaEndpoints.IsMediaPath(new PathString(path)));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/browse")]
    [InlineData("/model/7")]
    [InlineData("/libraries")]
    [InlineData("/designers")]
    [InlineData("/not-found")]
    public void Page_paths_are_not_treated_as_media(string path)
    {
        Assert.False(MediaEndpoints.IsMediaPath(new PathString(path)));
    }

    /// <summary>
    /// Segment matching, not a prefix string match: a page called /thumbnails
    /// must still get the friendly not-found page.
    /// </summary>
    [Theory]
    [InlineData("/thumbnails")]
    [InlineData("/meshes/1")]
    [InlineData("/snapshots")]
    public void Similarly_named_pages_are_not_media(string path)
    {
        Assert.False(MediaEndpoints.IsMediaPath(new PathString(path)));
    }
}
