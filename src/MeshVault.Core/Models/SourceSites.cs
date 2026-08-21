namespace MeshVault.Core.Models;

/// <summary>
/// Recognises where a model came from by its URL host, so the catalog can group
/// and badge by site without the user picking from a dropdown.
/// </summary>
public static class SourceSites
{
    public const string Unknown = "Other";

    private static readonly (string Suffix, string Name)[] Known =
    [
        ("makerworld.com", "MakerWorld"),
        ("printables.com", "Printables"),
        ("thingiverse.com", "Thingiverse"),
        ("thangs.com", "Thangs"),
        ("cults3d.com", "Cults3D"),
        ("myminifactory.com", "MyMiniFactory"),
        ("patreon.com", "Patreon"),
        ("tribes.gg", "Tribes"),
        ("gumroad.com", "Gumroad"),
        ("etsy.com", "Etsy"),
        ("github.com", "GitHub"),
    ];

    /// <summary>Site name for a URL, or null when the URL is unusable.</summary>
    public static string? Detect(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!TryParse(url, out var uri)) return null;

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;

        foreach (var (suffix, name) in Known)
        {
            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return Unknown;
    }

    /// <summary>
    /// Normalises user-pasted links. Accepts "makerworld.com/models/1" without a
    /// scheme, and rejects anything that is not http(s).
    /// </summary>
    public static bool TryParse(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url)) return false;

        var candidate = url.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
            candidate = "https://" + candidate;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        if (!parsed.Host.Contains('.')) return false;

        uri = parsed;
        return true;
    }

    /// <summary>The absolute URL to store, or null when the input is not a usable link.</summary>
    public static string? Normalize(string? url) => TryParse(url, out var uri) ? uri.ToString() : null;
}
