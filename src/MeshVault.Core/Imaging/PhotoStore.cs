namespace MeshVault.Core.Imaging;

/// <summary>
/// Photos of finished models, kept beside the thumbnails under the data
/// directory.
/// </summary>
/// <remarks>
/// Never in the library. The library holds files the user put there and is
/// usually mounted read-only; a photo taken on a phone is something this app
/// made and belongs with the app's own data, where the backup advice already
/// points.
/// </remarks>
public class PhotoStore(string rootDirectory)
{
    /// <summary>Big enough for a phone photo, small enough to refuse a video.</summary>
    public const int MaxBytes = 12 * 1024 * 1024;

    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
    };

    public string Root { get; } = rootDirectory;

    public string PathFor(string fileName) => Path.Combine(Root, fileName);

    public static bool IsAllowedType(string? contentType) =>
        contentType is not null && Allowed.ContainsKey(contentType);

    public static string ExtensionFor(string contentType) => Allowed[contentType];

    /// <summary>
    /// Checks the bytes rather than trusting the declared type.
    /// </summary>
    /// <remarks>
    /// The file is written to disk and served back out, so a browser deciding
    /// for itself what it received is the whole risk. The snapshot endpoint
    /// already sniffs PNG for the same reason.
    /// </remarks>
    public static bool LooksLikeImage(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12) return false;

        // JPEG
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return true;

        // PNG
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return true;

        // RIFF....WEBP
        if (bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
            && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P') return true;

        return false;
    }

    public async Task<string> SaveAsync(byte[] bytes, string contentType, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Root);

        var fileName = Guid.NewGuid().ToString("N") + ExtensionFor(contentType);
        await File.WriteAllBytesAsync(PathFor(fileName), bytes, ct);
        return fileName;
    }

    public void Delete(string fileName)
    {
        try
        {
            var path = PathFor(fileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A photo left on disk is untidy; failing the delete of its row
            // would be worse.
        }
    }
}
