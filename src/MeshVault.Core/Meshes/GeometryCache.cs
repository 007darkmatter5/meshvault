namespace MeshVault.Core.Meshes;

/// <summary>
/// Stores viewer-ready mesh payloads on disk.
/// </summary>
/// <remarks>
/// Building a payload requires reading the original file, which on a slow
/// library share costs many seconds even though the result is only tens of
/// kilobytes. Caching turns every view after the first into a local read, and
/// the thumbnail worker fills the cache from the copy it already staged, so
/// most models are ready before anyone opens them.
/// </remarks>
public class GeometryCache(string rootDirectory)
{
    /// <summary>
    /// Bump when the payload format or the way it is built changes, so stale
    /// files are ignored. Version 2 replaced stride sampling with vertex
    /// clustering; version 1 payloads render as scattered facets.
    /// </summary>
    public const int FormatVersion = 2;

    public string PathFor(int fileId) =>
        Path.Combine(rootDirectory, (fileId & 0xFF).ToString("x2"), $"{fileId}.v{FormatVersion}.mvm");

    public bool Has(int fileId) => File.Exists(PathFor(fileId));

    public async Task<byte[]?> TryReadAsync(int fileId, CancellationToken ct = default)
    {
        var path = PathFor(fileId);
        try
        {
            return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public async Task WriteAsync(int fileId, byte[] payload, CancellationToken ct = default)
    {
        var path = PathFor(fileId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written aside and moved, so a reader never sees a half-written payload.
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, payload, ct);
            File.Move(temporary, path, overwrite: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            TryDelete(temporary);
            throw;
        }
    }

    public void Delete(int fileId) => TryDelete(PathFor(fileId));

    /// <summary>
    /// Removes payloads left behind by earlier format versions. Without this a
    /// version bump orphans the whole cache on disk, invisible and never read.
    /// </summary>
    public int PruneOldVersions()
    {
        if (!Directory.Exists(rootDirectory)) return 0;

        var current = $".v{FormatVersion}.mvm";
        var removed = 0;

        try
        {
            foreach (var path in Directory.EnumerateFiles(rootDirectory, "*.mvm", SearchOption.AllDirectories))
            {
                if (path.EndsWith(current, StringComparison.OrdinalIgnoreCase)) continue;
                TryDelete(path);
                removed++;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return removed;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
