using System.IO.Compression;
using MeshVault.Data;

namespace MeshVault.Web.Services;

/// <summary>
/// Writes a <see cref="DownloadSet"/> out as a zip, one file at a time.
/// </summary>
/// <remarks>
/// Separate from the endpoint so the archive can be written somewhere a test can
/// open it again. What a zip is and what an HTTP response is are different
/// questions, and only the second one needs a browser.
/// </remarks>
public static class ArchiveWriter
{
    /// <summary>
    /// Extensions whose bytes are already compressed. Deflating a JPEG or a 3MF
    /// (a zip in its own right) spends CPU to gain nothing, so they are stored.
    /// Meshes and gcode are the opposite — a binary STL roughly halves — and
    /// deflate at its fastest runs orders of magnitude faster than the share can
    /// feed it, so that compression is free in wall-clock terms.
    /// </summary>
    private static readonly HashSet<string> AlreadyCompressed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".gz", ".xz", ".bz2", ".jpg", ".jpeg", ".png", ".webp",
        ".gif", ".mp4", ".mov", ".webm", ".3mf", ".ctb", ".photon", ".pwmx", ".bgcode",
    };

    /// <summary>
    /// Streams every file of <paramref name="set"/> into <paramref name="destination"/>,
    /// which is left open. Returns how many files actually made it in.
    /// </summary>
    public static async Task<int> WriteAsync(
        Stream destination,
        DownloadSet set,
        ILogger logger,
        CancellationToken ct = default)
    {
        var written = 0;

        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        foreach (var item in set.Items)
        {
            ct.ThrowIfCancellationRequested();

            FileStream source;
            try
            {
                source = File.OpenRead(item.FullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Indexed but since moved, or unreadable by this process.
                // Skipping hands back an archive short of a file; throwing hands
                // back a truncated one, which looks the same and says less.
                logger.LogWarning(ex, "Skipping {Path} in download {Name}", item.FullPath, set.Name);
                continue;
            }

            await using (source)
            {
                var entry = zip.CreateEntry(item.EntryPath, LevelFor(item.EntryPath));
                entry.LastWriteTime = LastWriteOf(item.FullPath);

                await using var target = entry.Open();
                await source.CopyToAsync(target, ct);
            }

            written++;
        }

        return written;
    }

    private static CompressionLevel LevelFor(string entryPath) =>
        AlreadyCompressed.Contains(Path.GetExtension(entryPath))
            ? CompressionLevel.NoCompression
            : CompressionLevel.Fastest;

    /// <summary>
    /// The file's timestamp, or now when the share will not give a usable one.
    /// </summary>
    /// <remarks>
    /// Zip cannot record a date before 1980 and
    /// <see cref="ZipArchiveEntry.LastWriteTime"/> throws rather than clamping.
    /// A share reporting a file as year 1601 — which happens — would otherwise
    /// tear down an archive half way through, after the headers had gone out and
    /// the status code could no longer say why.
    /// </remarks>
    private static DateTimeOffset LastWriteOf(string path)
    {
        try
        {
            var written = File.GetLastWriteTime(path);
            return written.Year >= 1980 ? written : DateTimeOffset.Now;
        }
        catch (IOException)
        {
            return DateTimeOffset.Now;
        }
    }
}
