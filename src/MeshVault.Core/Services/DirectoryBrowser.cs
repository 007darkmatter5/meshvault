using MeshVault.Core.Models;

namespace MeshVault.Core.Services;

/// <summary>
/// A folder in the picker. <see cref="ModelFileCount"/> is null until the entry
/// has been probed — probing touches the filesystem and is deferred so that a
/// slow or disconnected network share cannot stall the listing.
/// </summary>
public record DirectoryEntry(
    string Name,
    string FullPath,
    bool Accessible = true,
    bool HasSubdirectories = false,
    int? ModelFileCount = null)
{
    public bool Probed => ModelFileCount is not null || !Accessible;
}

/// <summary>
/// Lists directories on the machine running MeshVault, so a library root can be
/// picked in the UI. Only ever reads directory names and file names — never file
/// contents — and never writes.
/// </summary>
/// <remarks>
/// Listing is split from probing on purpose. Enumerating a mapped network drive
/// costs well over a second per drive, so the cheap parts (names, paths) are
/// returned immediately and the expensive parts (readability, child counts) are
/// filled in afterwards via <see cref="ProbeAsync"/>.
/// </remarks>
public class DirectoryBrowser
{
    /// <summary>Counting stops here so one enormous folder cannot stall a probe.</summary>
    public const int MaxCountedFiles = 500;

    // Filtered by name rather than by attribute, because reading attributes is
    // another round trip per entry on a network share.
    private static readonly string[] NoiseNames =
    [
        "$Recycle.Bin", "System Volume Information", "$WinREAgent", "Recovery",
        "@eaDir", "#recycle", "lost+found", "__MACOSX",
    ];

    /// <summary>
    /// Top-level starting points: drive letters on Windows, / on Unix. Does no
    /// per-drive I/O, so this returns in well under a millisecond even when
    /// network drives are mapped.
    /// </summary>
    public List<DirectoryEntry> GetRoots()
    {
        if (!OperatingSystem.IsWindows())
            return [new DirectoryEntry("/", "/")];

        var roots = new List<DirectoryEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            // DriveInfo.Name is available without contacting the volume;
            // IsReady and VolumeLabel are not, so they are left to ProbeAsync.
            var path = drive.Name;
            roots.Add(new DirectoryEntry(Name: path.TrimEnd('\\', '/'), FullPath: path));
        }
        return roots;
    }

    /// <summary>Subdirectory names only. One enumeration, no per-child I/O.</summary>
    public List<DirectoryEntry> GetChildren(string path)
    {
        var children = new List<DirectoryEntry>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.')) continue;
                if (NoiseNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                children.Add(new DirectoryEntry(name, dir));
            }
        }
        catch (DirectoryNotFoundException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
        catch (IOException) { return []; }

        children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return children;
    }

    /// <summary>
    /// Fills in readability, whether the folder has subfolders, and how many mesh
    /// or CAD files sit directly inside it. Runs off the caller's thread because
    /// on a network share this can take a second or more.
    /// </summary>
    public Task<DirectoryEntry> ProbeAsync(DirectoryEntry entry, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                var hasSubdirectories = Directory.EnumerateDirectories(entry.FullPath).Any();

                ct.ThrowIfCancellationRequested();

                var count = 0;
                foreach (var file in Directory.EnumerateFiles(entry.FullPath))
                {
                    ct.ThrowIfCancellationRequested();
                    if (FileKinds.FromExtension(Path.GetExtension(file)) is FileKind.Mesh or FileKind.Cad)
                    {
                        if (++count >= MaxCountedFiles) break;
                    }
                }

                return entry with
                {
                    Accessible = true,
                    HasSubdirectories = hasSubdirectories,
                    ModelFileCount = count,
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // Unreadable, disconnected, or permission-denied: show it greyed
                // out rather than dropping it or failing the whole listing.
                return entry with { Accessible = false, ModelFileCount = null };
            }
        }, ct);

    /// <summary>Parent directory, or null when already at a root.</summary>
    public string? GetParent(string path)
    {
        try
        {
            return Directory.GetParent(Path.TrimEndingDirectorySeparator(path))?.FullName;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public bool Exists(string path)
    {
        try { return Directory.Exists(path); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Counts mesh and CAD files directly inside a folder, capped.</summary>
    public Task<int> CountModelFilesAsync(string path, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var count = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(path))
                {
                    ct.ThrowIfCancellationRequested();
                    if (FileKinds.FromExtension(Path.GetExtension(file)) is FileKind.Mesh or FileKind.Cad)
                    {
                        if (++count >= MaxCountedFiles) break;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return 0; }
            return count;
        }, ct);

    /// <summary>Splits a path into cumulative segments for a breadcrumb trail.</summary>
    public static List<(string Name, string Path)> GetBreadcrumbs(string path)
    {
        var crumbs = new List<(string, string)>();
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        while (!string.IsNullOrEmpty(current))
        {
            var name = Path.GetFileName(current);
            if (string.IsNullOrEmpty(name)) name = current; // drive root or "/"
            crumbs.Insert(0, (name, current));

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || parent == current) break;
            current = Path.TrimEndingDirectorySeparator(parent);
        }
        return crumbs;
    }
}
