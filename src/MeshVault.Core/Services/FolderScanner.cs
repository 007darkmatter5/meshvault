using MeshVault.Core.Models;

namespace MeshVault.Core.Services;

public record ScannedFile(string RelativePath, string FileName, string Extension,
    FileKind Kind, long SizeBytes, DateTimeOffset ModifiedUtc);

public record ScannedModel(string RelativePath, string Name, IReadOnlyList<ScannedFile> Files)
{
    public long TotalBytes => Files.Sum(f => f.SizeBytes);
    public DateTimeOffset ModifiedUtc => Files.Max(f => f.ModifiedUtc);
}

/// <summary>
/// Walks a library root and groups files into logical models. Pure filesystem
/// reads — no database, no mutation — so it can be tested against a temp folder.
/// </summary>
public class FolderScanner
{
    private static readonly string[] IgnoredDirectories =
        [".git", ".svn", "node_modules", "@eaDir", ".meshvault", "__MACOSX", ".Trash-1000"];

    /// <summary>
    /// A folder becomes a model when it directly contains at least one mesh or CAD
    /// file. Its non-model subfolders (images, docs, variants without meshes) are
    /// absorbed into it, so "Dragon/{files,supports/,photos/}" stays one entry.
    /// </summary>
    public IEnumerable<ScannedModel> Scan(string rootPath, CancellationToken ct = default) =>
        Scan(rootPath, null, ct);

    /// <summary>
    /// The same walk, begun at <paramref name="subPath"/> inside the library
    /// rather than at its root.
    /// </summary>
    /// <remarks>
    /// Every path still comes back relative to the <b>library root</b>, not to
    /// the folder walked. That is the whole point and the only tricky part:
    /// reconciliation is keyed on <see cref="Core.Models.ModelEntry.RelativePath"/>,
    /// so a scan of the inbox that reported inbox-relative paths would read as
    /// a library full of new models beside the ones already recorded.
    ///
    /// A sub-path is resolved before it is checked, so no arrangement of ".."
    /// walks out of the library.
    /// </remarks>
    public IEnumerable<ScannedModel> Scan(string rootPath, string? subPath,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"Library root not found: {rootPath}");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var start = root;

        if (!string.IsNullOrWhiteSpace(subPath))
        {
            start = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Path.Combine(root, subPath.Replace('/', Path.DirectorySeparatorChar))));

            if (!start.Equals(root, StringComparison.OrdinalIgnoreCase)
                && !start.StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"'{subPath}' is not inside the library.", nameof(subPath));
            }

            if (!Directory.Exists(start))
                throw new DirectoryNotFoundException($"Folder not found: {start}");
        }

        foreach (var dir in EnumerateDirectories(start, ct))
        {
            ct.ThrowIfCancellationRequested();

            var ownFiles = ReadFiles(root, dir, recurse: false, ct);
            if (!ownFiles.Any(f => f.Kind is FileKind.Mesh or FileKind.Cad))
                continue;

            // Absorb subfolders that are not models in their own right.
            var files = new List<ScannedFile>(ownFiles);
            foreach (var sub in SafeEnumerateDirectories(dir))
            {
                if (IsIgnored(sub)) continue;
                if (ContainsModelFolder(sub, ct)) continue;
                files.AddRange(ReadFiles(root, sub, recurse: true, ct));
            }

            var relative = Path.GetRelativePath(root, dir);
            if (relative == ".") relative = "";

            yield return new ScannedModel(
                RelativePath: relative.Replace(Path.DirectorySeparatorChar, '/'),
                Name: relative == "" ? Path.GetFileName(root) : Path.GetFileName(dir),
                Files: files);
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string root, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            yield return dir;
            foreach (var sub in SafeEnumerateDirectories(dir))
                if (!IsIgnored(sub))
                    stack.Push(sub);
        }
    }

    private bool ContainsModelFolder(string dir, CancellationToken ct)
    {
        foreach (var d in EnumerateDirectories(dir, ct))
            if (SafeEnumerateFiles(d).Any(f =>
                    FileKinds.FromExtension(Path.GetExtension(f)) is FileKind.Mesh or FileKind.Cad))
                return true;
        return false;
    }

    private static List<ScannedFile> ReadFiles(string root, string dir, bool recurse, CancellationToken ct)
    {
        var results = new List<ScannedFile>();
        var dirs = recurse ? EnumerateDirectories(dir, ct) : [dir];

        foreach (var d in dirs)
        {
            foreach (var path in SafeEnumerateFiles(d))
            {
                ct.ThrowIfCancellationRequested();
                FileInfo info;
                try { info = new FileInfo(path); if (!info.Exists) continue; }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                var ext = Path.GetExtension(path);
                results.Add(new ScannedFile(
                    RelativePath: Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                    FileName: info.Name,
                    Extension: ext.ToLowerInvariant(),
                    Kind: FileKinds.FromExtension(ext),
                    SizeBytes: info.Length,
                    ModifiedUtc: info.LastWriteTimeUtc));
            }
        }
        return results;
    }

    private static bool IsIgnored(string dir)
    {
        var name = Path.GetFileName(dir);
        return name.StartsWith('.') && !IgnoredDirectories.Contains(name)
            || IgnoredDirectories.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); }
        catch (UnauthorizedAccessException) { return []; }
        catch (IOException) { return []; }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir).ToList(); }
        catch (UnauthorizedAccessException) { return []; }
        catch (IOException) { return []; }
    }
}
