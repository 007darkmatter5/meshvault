namespace MeshVault.Data;

/// <summary>Path arithmetic shared by grouping and the pages that show it.</summary>
public static class Paths
{
    /// <summary>
    /// Deepest folder every path sits under, or "" when they share no ancestor.
    /// </summary>
    public static string CommonParent(IEnumerable<string> paths)
    {
        string[]? common = null;

        foreach (var path in paths)
        {
            // The model's own folder is not an ancestor of itself.
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[..^1];

            if (common is null)
            {
                common = parts;
                continue;
            }

            var shared = 0;
            while (shared < common.Length && shared < parts.Length
                && string.Equals(common[shared], parts[shared], StringComparison.OrdinalIgnoreCase))
                shared++;

            common = common[..shared];
        }

        return common is null ? "" : string.Join('/', common);
    }
}
