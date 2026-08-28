using System.Text;

namespace MeshVault.Core.Services;

/// <summary>A token a template may use, and what to put there when it is empty.</summary>
public record TemplateToken(string Name, string Description, string Fallback);

/// <summary>
/// Renders a folder or file name from a pattern like <c>{designer}/{model}</c>.
/// </summary>
/// <remarks>
/// Every result becomes a real path on someone's disk, so rendering is
/// deliberately paranoid: each segment is sanitised on its own, and a token can
/// never introduce a path separator or climb out of the library with "..". A
/// template is data typed by a user, and the library it is pointed at is
/// usually irreplaceable.
/// </remarks>
public static class PathTemplate
{
    /// <summary>Tokens describing the model. Valid in both folder and file templates.</summary>
    public static readonly IReadOnlyList<TemplateToken> ModelTokens =
    [
        // Where a value comes from is the thing people get wrong, so each says
        // it. Twice over, someone renamed a model and expected the files inside
        // it to follow: {file} keeps the name the file already has, and only a
        // template naming {model} pays any attention to a rename.
        new("model", "The model's name — its folder, unless you renamed it", "Unnamed"),
        new("designer", "Who made it", "Unsorted"),

        // The one token that changes how many folders come out of a model. A
        // pack folder holding ninety-eight minis becomes ninety-eight folders,
        // and four folders holding one mini between them become one. Both are
        // the same rule: a sculpt gets a folder, and its exports live in it.
        new("sculpt", "The mini, read from the file's name — a pack splits into one folder each",
            "Unsorted"),
        new("source", "Where it came from, such as MakerWorld", "Unknown source"),
        new("collection", "Your first collection containing it", "Unfiled"),
        new("tag", "Its first tag, alphabetically", "Untagged"),
        new("year", "The year it was added to MeshVault", "Undated"),
        new("license", "Its licence", "Unlicensed"),
    ];

    /// <summary>Extra tokens for naming a file inside a model's folder.</summary>
    public static readonly IReadOnlyList<TemplateToken> FileTokens =
    [
        new("file", "The name this file already has — a rename of the model does not reach it",
            "file"),
        new("index", "Its position among the model's files, from 1", "1"),
        new("kind", "Mesh, Cad, Image, Document and so on", "Other"),
        // Alone among the tokens in falling back to nothing rather than to a
        // word. The others name something every model has -- a designer, a
        // year -- so a gap is worth marking. A variant is a thing a file
        // either is or is not, and "otto-bismark-plain" tells you nothing
        // "otto-bismark" does not. A file that is marked still says so, even
        // when it is the only copy you own.
        new("variant", "Supported, Hollowed or No logo — nothing at all if the file is unmarked", ""),
    ];

    /// <summary>
    /// Characters no mainstream filesystem will accept, plus the separators,
    /// which a token must never be able to introduce.
    /// </summary>
    private static readonly char[] Illegal = ['<', '>', ':', '"', '|', '?', '*', '/', '\\'];

    /// <summary>
    /// Names Windows refuses whatever the extension, as devices rather than
    /// files. A library organised on Linux still has to be readable from a
    /// Windows machine over SMB, which is how most people reach an Unraid share.
    /// </summary>
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// Long enough for a descriptive model name, short enough that a few nested
    /// segments stay inside Windows' 260-character path limit.
    /// </summary>
    public const int MaxSegmentLength = 96;

    /// <summary>Tokens the template uses that are not defined, in the order found.</summary>
    public static List<string> UnknownTokens(string template, bool forFile)
    {
        var known = ModelTokens.Select(t => t.Name)
            .Concat(forFile ? FileTokens.Select(t => t.Name) : [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknown = new List<string>();
        foreach (var name in TokenNames(template))
        {
            if (!known.Contains(name) && !unknown.Contains(name, StringComparer.OrdinalIgnoreCase))
                unknown.Add(name);
        }
        return unknown;
    }

    /// <summary>Every token named in a template, including repeats.</summary>
    public static IEnumerable<string> TokenNames(string template)
    {
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{') continue;

            var close = template.IndexOf('}', i + 1);
            if (close < 0) yield break;

            yield return template[(i + 1)..close];
            i = close;
        }
    }

    /// <summary>
    /// Renders a template into a relative path using forward slashes.
    /// </summary>
    /// <remarks>
    /// An empty value falls back to the token's placeholder rather than
    /// collapsing the segment, so a model with no designer lands somewhere
    /// obvious instead of at the library root among the organised ones.
    /// </remarks>
    public static string Render(
        string template,
        IReadOnlyDictionary<string, string?> values,
        bool forFile,
        NameCase casing = NameCase.AsWritten)
    {
        var rendered = new StringBuilder();

        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] == '{')
            {
                var close = template.IndexOf('}', i + 1);
                if (close > 0)
                {
                    rendered.Append(Value(template[(i + 1)..close], values, forFile));
                    i = close;
                    continue;
                }
            }

            rendered.Append(template[i]);
        }

        // Split on both separators: someone typing a Windows-style template
        // means the same thing by it.
        //
        // Casing runs per segment and before sanitising. Per segment because it
        // treats anything that is not a letter or digit as a word break, so a
        // whole path handed to it would come back with the slashes eaten.
        // Before, because sanitising is what has the last word on length,
        // trailing dots and reserved device names -- a casing pass that ran
        // afterwards could turn "_CON" back into "con".
        var segments = rendered.ToString()
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => NameCasing.Apply(s, casing))
            .Select(Sanitize)
            .Where(s => s.Length > 0)
            .ToList();

        return string.Join('/', segments);
    }

    private static string Value(string name, IReadOnlyDictionary<string, string?> values, bool forFile)
    {
        if (values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            // Sanitised here, before the rendered text is split into segments,
            // so a value can only ever fill the level the template gave it. A
            // designer recorded as "Cinderwing3D/Dragons" must not quietly
            // become two folders, and one recorded as ".." must not climb.
            return Sanitize(value);
        }

        var token = ModelTokens.Concat(forFile ? FileTokens : [])
            .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        // An unknown token renders as nothing. UnknownTokens reports it to the
        // user before they ever get here; silently leaving "{nonsense}" in a
        // real folder name would be worse.
        return token?.Fallback ?? "";
    }

    /// <summary>Makes one path segment safe on Windows, macOS and Linux alike.</summary>
    public static string Sanitize(string segment)
    {
        var cleaned = new StringBuilder(segment.Length);
        foreach (var c in segment)
        {
            // Control characters break some tools outright and are invisible in
            // the ones they do not.
            if (char.IsControl(c)) continue;
            cleaned.Append(Illegal.Contains(c) ? '-' : c);
        }

        // "." and ".." would climb the tree rather than name anything.
        var text = cleaned.ToString().Trim();
        if (text.Length == 0 || text.All(c => c == '.')) return "";

        // Windows silently strips trailing dots and spaces, so a folder created
        // as "Model V2." can never be opened again by that name.
        text = text.TrimEnd('.', ' ');

        // A separator with nothing on the far side of it. "{sculpt}-{variant}"
        // on an unmarked file renders "Otto Bismark-", and the dash is the
        // template showing through rather than part of anyone's name. Trimmed
        // before the reserved-name check, which adds a leading underscore of
        // its own.
        text = text.Trim(' ', '-', '_');

        if (text.Length > MaxSegmentLength)
            text = text[..MaxSegmentLength].TrimEnd('.', ' ', '-', '_');

        if (text.Length == 0) return "";

        var stem = text.Split('.')[0];
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) text = "_" + text;

        return text;
    }
}
