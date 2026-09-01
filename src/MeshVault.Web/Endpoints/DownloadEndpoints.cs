using System.Net.Mime;
using MeshVault.Core.Services;
using MeshVault.Data;
using MeshVault.Web.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Net.Http.Headers;

namespace MeshVault.Web.Endpoints;

/// <summary>
/// Hands the original files back: one file, one sculpt, a model, or a whole
/// collection zipped on the way out.
/// </summary>
/// <remarks>
/// Plain HTTP rather than Blazor, and for a harder reason than the thumbnails
/// next door. A download is bytes the browser saves, and the only way to start
/// one from a circuit is to marshal the whole file through SignalR first — which
/// for a 2 GB model means holding it in memory to hand it over. These are
/// ordinary GET links instead, and the browser does what browsers do.
/// </remarks>
public static class DownloadEndpoints
{
    public static void MapDownloadEndpoints(this WebApplication app)
    {
        // An account, not the view policy the thumbnails use. Public browsing
        // hands a visitor a decimated, quantised preview; this hands over the
        // creator's file exactly as it was bought. Those are different things to
        // leave open, so this door stays shut whichever way that setting is set.
        var group = app.MapGroup("/download").RequireAuthorization();

        group.MapGet("/file/{fileId:int}", DownloadFile);
        group.MapGet("/model/{modelId:int}", DownloadModel);
        group.MapGet("/sculpt/{modelId:int}", DownloadSculpt);
        group.MapGet("/collection/{collectionId:int}", DownloadCollection);
    }

    /// <summary>
    /// One file, as it sits on disk. The only download that supports ranges: it
    /// is a real file with a known length, so a dropped connection can be picked
    /// up where it left off rather than started again.
    /// </summary>
    private static async Task<IResult> DownloadFile(
        int fileId,
        HttpContext context,
        DownloadCatalog catalog,
        ForegroundActivity foreground,
        CancellationToken ct)
    {
        var item = await catalog.GetFileAsync(fileId, ct);
        if (item is null || !File.Exists(item.FullPath)) return Results.NotFound();

        // Results.File writes the body after this method has returned, so the
        // claim has to outlive the handler. Released when the response is done,
        // which is what the thumbnail worker is waiting on.
        var busy = foreground.Begin();
        context.Response.OnCompleted(() => { busy.Dispose(); return Task.CompletedTask; });

        // Octet-stream whatever it really is. The library holds files nobody
        // here wrote, and naming an HTML or SVG file for what it is invites the
        // browser to render it on our own origin.
        return Results.File(
            item.FullPath,
            MediaTypeNames.Application.Octet,
            item.EntryPath,
            enableRangeProcessing: true);
    }

    private static Task<IResult> DownloadModel(
        int modelId, HttpContext context, DownloadCatalog catalog, CancellationToken ct) =>
        Archive(context, catalog.GetModelAsync(modelId, ct));

    private static Task<IResult> DownloadSculpt(
        int modelId, string? key, HttpContext context, DownloadCatalog catalog, CancellationToken ct) =>
        Archive(context, catalog.GetSculptAsync(modelId, key ?? "", ct));

    private static Task<IResult> DownloadCollection(
        int collectionId, HttpContext context, DownloadCatalog catalog, CancellationToken ct) =>
        Archive(context, catalog.GetCollectionAsync(collectionId, ct));

    /// <summary>
    /// Resolves a download and streams it as a zip, or answers 404 while that is
    /// still something the response can say.
    /// </summary>
    private static async Task<IResult> Archive(HttpContext context, Task<DownloadSet?> pending)
    {
        var set = await pending;

        // No rows, or every row pointing outside its library. Either way there
        // is nothing to send, and an empty zip looks like a bug at the far end.
        if (set is null || set.Items.Count == 0) return Results.NotFound();

        await StreamAsync(context, set);

        // The body is already written; this only stops the framework writing
        // over the top of it.
        return Results.Empty;
    }

    private static async Task StreamAsync(HttpContext context, DownloadSet set)
    {
        var ct = context.RequestAborted;
        var services = context.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DownloadEndpoints));

        using var slot = await services.GetRequiredService<ArchiveThrottle>().EnterAsync(ct);

        // Somebody is sitting in front of a progress bar. The thumbnail worker
        // can wait; without this the download queues behind its backlog.
        using var busy = services.GetRequiredService<ForegroundActivity>().Begin();

        // ZipArchive writes its headers and central directory synchronously, and
        // Kestrel forbids blocking writes to a response body by default. Nothing
        // else in this request does blocking IO, and the alternative is
        // buffering an archive that can run to tens of gigabytes.
        if (context.Features.Get<IHttpBodyControlFeature>() is { } bodyControl)
            bodyControl.AllowSynchronousIO = true;

        // Kestrel hangs up on a response that falls below a minimum rate. A
        // library share this app measures at about 1.4 MB/s, feeding an archive
        // that can run for hours, will dip under that line sooner or later, and
        // losing an hour-old download to a momentary pause is not a defence
        // against anything.
        if (context.Features.Get<IHttpMinResponseDataRateFeature>() is { } rate)
            rate.MinDataRate = null;

        context.Response.ContentType = MediaTypeNames.Application.Zip;
        context.Response.Headers.ContentDisposition = Attachment(set.Name);

        // No Content-Length: the compressed size is not known until it has been
        // written. The browser shows an unbounded progress bar, and the download
        // cannot be resumed. Worth it — the alternative is building the whole
        // archive on disk first, paying for every byte twice on a box whose
        // /data is usually the small fast volume.

        try
        {
            await ArchiveWriter.WriteAsync(context.Response.Body, set, logger, ct);
        }
        catch (OperationCanceledException)
        {
            // Somebody closed the tab or hit cancel. Nothing to report, and
            // nothing left to report it to.
        }
        catch (Exception ex)
        {
            // The headers went out long ago, so the status code cannot be
            // changed to say this. Aborting is the only way left to tell the
            // browser that what it has is not the whole file.
            logger.LogError(ex, "Download {Name} failed part-way through", set.Name);
            context.Abort();
        }
    }

    /// <summary>
    /// The Content-Disposition header naming the archive. Sanitised the same way
    /// a folder name is, so a model called "Con." or one carrying a slash does
    /// not produce a header the browser has to guess at.
    /// </summary>
    private static string Attachment(string name)
    {
        var safe = PathTemplate.Sanitize(name);
        var fileName = (safe.Length > 0 ? safe : "meshvault") + ".zip";

        // SetHttpFileName writes both forms: an ASCII-folded filename for
        // anything that reads only that, and the RFC 5987 filename* that carries
        // a name with accents in it intact.
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(fileName);
        return disposition.ToString();
    }
}
