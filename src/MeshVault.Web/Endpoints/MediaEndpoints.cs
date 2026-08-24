using System.Globalization;
using MeshVault.Core.Imaging;
using MeshVault.Core.Meshes;
using MeshVault.Core.Models;
using MeshVault.Data;
using MeshVault.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeshVault.Web.Endpoints;

/// <summary>
/// Serves thumbnails and viewer geometry, and accepts snapshots. These are
/// plain HTTP endpoints rather than Blazor components because the browser
/// requests them directly from img tags and fetch.
/// </summary>
public static class MediaEndpoints
{
    /// <summary>Triangles above this are decimated before being sent to the browser.</summary>
    private static int ViewerTriangleBudget => ThumbnailService.ViewerTriangleBudget;

    /// <summary>Refuse absurd uploads; a 400x300 PNG is a few tens of KB.</summary>
    private const int MaxSnapshotBytes = 4 * 1024 * 1024;

    private static readonly string[] MediaPrefixes = ["/thumb", "/mesh", "/snapshot"];

    /// <summary>
    /// Whether a request is for binary media rather than a page. Used to keep
    /// the not-found page out of image and geometry responses.
    /// </summary>
    public static bool IsMediaPath(PathString path) =>
        MediaPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    public static void MapMediaEndpoints(this WebApplication app)
    {
        // Behind the same login as the pages. These serve the library's actual
        // contents, so leaving them open would make the sign-in decorative:
        // anyone reachable could enumerate thumbnails, pull geometry and post
        // snapshots. Browsers attach the auth cookie to same-origin img and
        // fetch requests, so the viewer and grid are unaffected.
        var media = app.MapGroup("").RequireAuthorization();

        media.MapGet("/thumb/model/{modelId:int}", GetModelThumbnail);
        media.MapGet("/thumb/file/{fileId:int}", GetFileThumbnail);
        media.MapGet("/mesh/{fileId:int}", GetMeshGeometry);
        media.MapPost("/snapshot/{modelId:int}", SaveSnapshot).DisableAntiforgery();
    }

    private static async Task<IResult> GetModelThumbnail(
        int modelId,
        IDbContextFactory<MeshVaultDbContext> factory,
        ThumbnailStore store,
        CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var model = await db.Models.AsNoTracking()
            .Where(m => m.Id == modelId)
            .Select(m => new { m.ThumbnailFileId, m.SnapshotUpdatedUtc })
            .FirstOrDefaultAsync(ct);

        if (model is null) return Results.NotFound();

        // A snapshot the user chose always beats the automatic render.
        if (model.SnapshotUpdatedUtc is not null)
        {
            var snapshot = store.PathForModelSnapshot(modelId);
            if (File.Exists(snapshot)) return ServePng(snapshot);
        }

        if (model.ThumbnailFileId is { } fileId)
        {
            var thumbnail = store.PathForFile(fileId);
            if (File.Exists(thumbnail)) return ServePng(thumbnail);
        }

        return Results.NotFound();
    }

    private static IResult GetFileThumbnail(int fileId, ThumbnailStore store)
    {
        var path = store.PathForFile(fileId);
        return File.Exists(path) ? ServePng(path) : Results.NotFound();
    }

    private static IResult ServePng(string path) =>
        Results.File(path, "image/png", lastModified: File.GetLastWriteTimeUtc(path), enableRangeProcessing: false);

    /// <summary>
    /// Streams a mesh to the viewer as compact binary rather than the original
    /// file. A 22 MB STL carries a normal per triangle and no shared vertices;
    /// sending quantised positions and letting the GPU derive normals cuts that
    /// dramatically, which matters on a slow library share.
    /// </summary>
    private static async Task<IResult> GetMeshGeometry(
        int fileId,
        IDbContextFactory<MeshVaultDbContext> factory,
        GeometryCache cache,
        ForegroundActivity foreground,
        IOptions<MeshVaultOptions> options,
        CancellationToken ct)
    {
        // The thumbnail worker usually filled this already, turning what would be
        // a multi-second read off the library share into a local one.
        if (await cache.TryReadAsync(fileId, ct) is { } cached)
            return Results.Bytes(cached, "application/octet-stream");

        // Not cached, so this will read the original file. Claim the share:
        // someone is sitting in front of a spinner waiting for it.
        using var _ = foreground.Begin();

        await using var db = await factory.CreateDbContextAsync(ct);

        var file = await db.Files.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new
            {
                f.Extension,
                f.RelativePath,
                LibraryPath = f.ModelEntry!.Library!.Path,
            })
            .FirstOrDefaultAsync(ct);

        if (file is null) return Results.NotFound();
        if (!MeshLoader.CanRead(file.Extension)) return Results.NotFound();

        var fullPath = Path.Combine(
            file.LibraryPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath)) return Results.NotFound();

        using var staged = await StagedMeshFile.CreateAsync(
            fullPath, Path.Combine(options.Value.DataPath, "staging"), ct);

        var payload = await Task.Run(
            () => MeshPayload.Build(MeshLoader.Open(staged.Path), ViewerTriangleBudget, ct), ct);

        // Cache it so the next viewer opens instantly.
        try { await cache.WriteAsync(fileId, payload, ct); }
        catch (Exception) { /* serving the model matters more than caching it */ }

        return Results.Bytes(payload, "application/octet-stream");
    }

    private static async Task<IResult> SaveSnapshot(
        int modelId,
        HttpRequest request,
        IDbContextFactory<MeshVaultDbContext> factory,
        ThumbnailStore store,
        CancellationToken ct)
    {
        if (request.ContentLength > MaxSnapshotBytes)
            return Results.BadRequest("Snapshot is too large.");

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        if (bytes.Length == 0) return Results.BadRequest("Snapshot was empty.");
        if (bytes.Length > MaxSnapshotBytes) return Results.BadRequest("Snapshot is too large.");
        if (!IsPng(bytes)) return Results.BadRequest("Snapshot must be a PNG.");

        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Models.AnyAsync(m => m.Id == modelId, ct)) return Results.NotFound();

        await store.SaveModelSnapshotAsync(modelId, bytes, ct);

        // The camera rides along as query values rather than in the body,
        // which is the PNG itself. Absent or unparseable means an older viewer
        // took the picture, and the model simply keeps its default framing.
        var view = ParseView(request.Query["vx"], request.Query["vy"], request.Query["vz"]);

        await db.Models.Where(m => m.Id == modelId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.SnapshotUpdatedUtc, DateTimeOffset.UtcNow)
                .SetProperty(m => m.SnapshotViewX, view?.X)
                .SetProperty(m => m.SnapshotViewY, view?.Y)
                .SetProperty(m => m.SnapshotViewZ, view?.Z), ct);

        return Results.Ok();
    }

    /// <summary>
    /// Reads the camera saved with a snapshot, rejecting anything that would
    /// not frame a model. Null means keep the default framing.
    /// </summary>
    /// <remarks>
    /// Values are multiples of the bounding radius, written by the viewer.
    /// Missing ones are ordinary: a snapshot taken before the viewer recorded
    /// its camera sends none, and that model simply opens as it always did.
    /// </remarks>
    public static (double X, double Y, double Z)? ParseView(string? x, string? y, string? z)
    {
        if (!TryReadDouble(x, out var vx) || !TryReadDouble(y, out var vy) || !TryReadDouble(z, out var vz))
            return null;

        // A camera at the origin has no direction to look from, and the viewer
        // divides by that length. A distance of a thousand radii is not a view
        // anyone chose, so treat it as a bug rather than restoring it.
        var length = Math.Sqrt(vx * vx + vy * vy + vz * vz);
        return length is > 0.001 and < 1000 ? (vx, vy, vz) : null;
    }

    private static bool TryReadDouble(string? raw, out double value)
    {
        value = 0;
        // Invariant culture only: the viewer writes these with toFixed, and a
        // server in a comma-decimal locale would otherwise read 0.55 as 55.
        return !string.IsNullOrWhiteSpace(raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    /// <summary>The upload is written to disk and served back, so verify it really is a PNG.</summary>
    private static bool IsPng(ReadOnlySpan<byte> bytes) =>
        bytes.Length > 8
        && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
        && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;
}
