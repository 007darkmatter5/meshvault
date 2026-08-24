namespace MeshVault.Core.Models;

/// <summary>
/// Common miniature paint brands and the ranges within them, to suggest from.
/// </summary>
/// <remarks>
/// Suggestions, never a closed list. Ranges are reissued and renamed constantly
/// and no list stays right for long, so both fields accept anything typed. This
/// exists to save keystrokes on the ninety per cent, not to police the rest.
/// </remarks>
public static class PaintBrands
{
    public static readonly IReadOnlyDictionary<string, string[]> Known =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Citadel"] = ["Base", "Layer", "Shade", "Contrast", "Dry", "Technical", "Air", "Spray"],
            ["Vallejo"] = ["Model Color", "Game Color", "Model Air", "Game Air", "Xpress Color", "Mecha Color", "Panzer Aces", "Liquid Gold"],
            ["The Army Painter"] = ["Warpaints Fanatic", "Warpaints", "Speedpaint", "Air", "Quickshade", "Colour Primer"],
            ["Scale75"] = ["Scalecolor", "Artist Range", "Instant Colors", "Metal n Alchemy"],
            ["Pro Acryl"] = ["Standard", "Bold", "Signature", "Transparent"],
            ["Two Thin Coats"] = ["Standard", "Metallics"],
            ["Kimera Kolors"] = ["Pure Pigments", "Ready Mixed"],
            ["Reaper"] = ["Master Series", "Bones", "Pathfinder"],
            ["AK Interactive"] = ["3rd Generation", "Real Colors", "Dual Exo"],
            ["Turbo Dork"] = ["Metallic", "Colorshift", "Turboshift", "Zenishift"],
            ["Green Stuff World"] = ["Dipping Ink", "Chameleon", "Metal Paint"],
            ["Monument Hobbies"] = ["Pro Acryl"],
            ["Formula P3"] = ["Base", "Highlight", "Ink", "Wash"],
            ["Golden"] = ["Fluid Acrylics", "High Flow"],
        };

    public static IEnumerable<string> Brands => Known.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ranges belonging to a brand. An unknown brand offers every range there
    /// is rather than nothing, since a name typed by hand is usually a brand
    /// this list has simply not heard of.
    /// </summary>
    public static IEnumerable<string> RangesFor(string? brand)
    {
        if (!string.IsNullOrWhiteSpace(brand) && Known.TryGetValue(brand.Trim(), out var ranges))
            return ranges;

        return Known.Values.SelectMany(r => r).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase);
    }
}
