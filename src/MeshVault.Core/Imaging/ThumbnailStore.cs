namespace MeshVault.Core.Imaging;

/// <summary>
/// Holds rendered thumbnails on disk, outside the database, so the SQLite file
/// stays small and images can be served straight from the filesystem.
/// </summary>
public class ThumbnailStore(string rootDirectory)
{
    /// <summary>Bumped when rendering changes enough that cached images should be redone.</summary>
    public const int RenderVersion = 1;

    private string FileDirectory => Path.Combine(rootDirectory, "files");
    private string ModelDirectory => Path.Combine(rootDirectory, "models");

    /// <summary>Auto-rendered thumbnail for one mesh file.</summary>
    public string PathForFile(int fileId) =>
        Path.Combine(FileDirectory, Shard(fileId), $"{fileId}.png");

    /// <summary>Snapshot the user chose for a model, which wins over the auto render.</summary>
    public string PathForModelSnapshot(int modelId) =>
        Path.Combine(ModelDirectory, Shard(modelId), $"{modelId}.png");

    // Thousands of files in one directory is slow to enumerate on some
    // filesystems, so they are spread over 256 buckets.
    private static string Shard(int id) => (id & 0xFF).ToString("x2");

    public bool HasFileThumbnail(int fileId) => File.Exists(PathForFile(fileId));

    public bool HasModelSnapshot(int modelId) => File.Exists(PathForModelSnapshot(modelId));

    public async Task SaveFileThumbnailAsync(int fileId, byte[] png, CancellationToken ct = default) =>
        await WriteAsync(PathForFile(fileId), png, ct);

    public async Task SaveModelSnapshotAsync(int modelId, byte[] png, CancellationToken ct = default) =>
        await WriteAsync(PathForModelSnapshot(modelId), png, ct);

    public void DeleteModelSnapshot(int modelId) => TryDelete(PathForModelSnapshot(modelId));

    public void DeleteFileThumbnail(int fileId) => TryDelete(PathForFile(fileId));

    /// <summary>
    /// Writes to a temporary file and moves it into place, so a reader never
    /// sees a partially written PNG.
    /// </summary>
    private static async Task WriteAsync(string path, byte[] png, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, png, ct);
        File.Move(temporary, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
