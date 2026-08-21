using System.Text.Json;

namespace MeshVault.Core.Services;

/// <summary>What a Manyfold datapackage.json tells us about a model.</summary>
public record Datapackage(
    string? Title,
    IReadOnlyList<string> Keywords,
    string? Homepage,
    string? Author,
    string? License,
    IReadOnlyDictionary<string, string> UpAxisByFile,
    IReadOnlyList<string> Collections,
    string? Description)
{
    public static Datapackage Empty { get; } =
        new(null, [], null, null, null, new Dictionary<string, string>(), [], null);
}

/// <summary>
/// Reads the sidecar file that Manyfold writes beside an exported model. It
/// carries the real title and keywords, which the folder name usually does not.
/// </summary>
public static class DatapackageReader
{
    public const string FileName = "datapackage.json";

    /// <summary>
    /// Parses a datapackage. Returns <see cref="Datapackage.Empty"/> rather than
    /// throwing for malformed files: one bad sidecar should not abort an import
    /// across hundreds of models.
    /// </summary>
    public static Datapackage Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Datapackage.Empty;

            return new Datapackage(
                Title: ReadString(root, "title"),
                Keywords: ReadKeywords(root),
                Homepage: ReadString(root, "homepage"),
                Author: ReadAuthor(root),
                License: ReadLicense(root),
                UpAxisByFile: ReadUpAxes(root),
                Collections: ReadCollections(root),
                Description: ReadString(root, "description") ?? ReadString(root, "caption"));
        }
        catch (JsonException)
        {
            return Datapackage.Empty;
        }
    }

    public static Datapackage Read(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (IOException) { return Datapackage.Empty; }
        catch (UnauthorizedAccessException) { return Datapackage.Empty; }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Clean(value.GetString())
            : null;

    private static List<string> ReadKeywords(JsonElement root)
    {
        var keywords = new List<string>();
        if (!root.TryGetProperty("keywords", out var array) || array.ValueKind != JsonValueKind.Array)
            return keywords;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            if (Clean(item.GetString()) is { } word) keywords.Add(word);
        }
        return keywords;
    }

    /// <summary>Datapackage allows an author string or a contributors array.</summary>
    private static string? ReadAuthor(JsonElement root)
    {
        if (ReadString(root, "author") is { } author) return author;

        if (root.TryGetProperty("contributors", out var contributors)
            && contributors.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in contributors.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && Clean(item.GetString()) is { } name)
                    return name;
                if (item.ValueKind == JsonValueKind.Object && ReadString(item, "title") is { } title)
                    return title;
                if (item.ValueKind == JsonValueKind.Object && ReadString(item, "name") is { } n)
                    return n;
            }
        }
        return null;
    }

    /// <summary>Licences may be a plain string or an array of objects.</summary>
    private static string? ReadLicense(JsonElement root)
    {
        if (ReadString(root, "license") is { } license) return license;

        if (root.TryGetProperty("licenses", out var licenses)
            && licenses.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in licenses.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && Clean(item.GetString()) is { } name)
                    return name;
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (ReadString(item, "name") is { } n) return n;
                if (ReadString(item, "title") is { } t) return t;
                if (ReadString(item, "path") is { } p) return p;
            }
        }
        return null;
    }

    /// <summary>
    /// Collection names this model belongs to. Manyfold writes objects carrying
    /// a title plus a link back to its own instance; only the title is useful.
    /// </summary>
    private static List<string> ReadCollections(JsonElement root)
    {
        var names = new List<string>();
        if (!root.TryGetProperty("collections", out var value)) return names;

        if (value.ValueKind == JsonValueKind.String)
        {
            if (Clean(value.GetString()) is { } single) names.Add(single);
            return names;
        }

        if (value.ValueKind != JsonValueKind.Array) return names;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && Clean(item.GetString()) is { } name)
                names.Add(name);
            else if (item.ValueKind == JsonValueKind.Object && ReadString(item, "title") is { } title)
                names.Add(title);
        }
        return names;
    }

    /// <summary>
    /// Per-resource "up" axis, keyed by the resource path. Meshes are usually
    /// +Z for printing, but a model that says otherwise would render on its side.
    /// </summary>
    private static Dictionary<string, string> ReadUpAxes(JsonElement root)
    {
        var axes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("resources", out var resources)
            || resources.ValueKind != JsonValueKind.Array)
            return axes;

        foreach (var item in resources.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (ReadString(item, "path") is not { } path) continue;
            if (ReadString(item, "up") is not { } up) continue;

            axes[path.Replace('\\', '/')] = up;
        }
        return axes;
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
