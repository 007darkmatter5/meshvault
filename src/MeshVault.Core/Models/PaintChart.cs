using System.Globalization;

namespace MeshVault.Core.Models;

/// <summary>One colour on a maker's chart.</summary>
/// <param name="Hex">
/// Swatch as #rrggbb, sampled from the maker's own poster. A screen colour
/// standing in for a pigment: close enough to tell two bottles apart, and
/// not something to match a mix against.
/// </param>
public record ChartPaint(string Name, string Hex)
{
    /// <summary>The same key a rack stores, so a chart is matched the way a bottle is.</summary>
    public string NormalizedName { get; } = Name.Trim().ToLowerInvariant();

    /// <summary>
    /// Whether a label over this colour should be dark. Whichever of black and
    /// white contrasts better, which is the choice the printed chart makes too.
    /// </summary>
    public bool IsLight { get; } = Luminance(Hex) > 0.179;

    private static double Luminance(string hex)
    {
        double Channel(int at)
        {
            var value = int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber) / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }
}

/// <summary>
/// Six paints running dark to light, which is what makes the triads flexible:
/// any three taken in order are a shadow, a midtone and a highlight.
/// </summary>
public record ChartFamily(string Name, IReadOnlyList<ChartPaint> Paints);

/// <summary>
/// A maker's published chart of one range, as a reference to read a rack against.
/// </summary>
/// <remarks>
/// Shipped with the app, which <see cref="PaintBrand"/> deliberately is not, and
/// the difference is what each one is. A list of brands is an opinion about
/// what people own and goes stale; a chart is a document its maker printed,
/// and is the same next year. Nothing here is written to the database either:
/// it is matched to a rack by name when the page is read, so owning none of it
/// costs nothing and a rack is never filled with bottles nobody bought.
/// </remarks>
public record PaintChart(
    string Brand, string Range, string Title, string SourceUrl, IReadOnlyList<ChartFamily> Families)
{
    public IEnumerable<ChartPaint> Paints => Families.SelectMany(f => f.Paints);
}

public static class PaintCharts
{
    /// <summary>
    /// The Army Painter's "Flexible Triad System" poster for Warpaints Fanatic,
    /// in the order it is printed.
    /// </summary>
    public static PaintChart WarpaintsFanatic { get; } = new(
        "The Army Painter",
        "Warpaints Fanatic",
        "Flexible Triad System",
        "https://cdn.shopify.com/s/files/1/0781/9850/5747/files/A3_Flexible_Triad_System_Poster.pdf",
        [
            Family("Black & Greys",
                ("Matt Black", "#090400"), ("Deep Grey", "#595e65"), ("Uniform Grey", "#70757c"),
                ("Ash Grey", "#909196"), ("Company Grey", "#c1c2c7"), ("Brigade Grey", "#d5d6d8")),
            Family("Warm Greys & White",
                ("Grey Castle", "#9a9a90"), ("Gargoyle Grey", "#b0ac9a"), ("Great Hall Grey", "#c7beb0"),
                ("Worn Stone", "#cdc9b8"), ("Brainmatter Beige", "#e9e7d9"), ("Matt White", "#fefefe")),
            Family("Desaturated Blue-Greys",
                ("Night Sky", "#3a4959"), ("Thunderous Blue", "#365579"), ("Stratos Blue", "#33628d"),
                ("Wolf Grey", "#537ca3"), ("Runic Cobalt", "#7a97b1"), ("Frost Blue", "#a5c1dd")),
            Family("Strong Pale Blues",
                ("Triumphant Navy", "#1a2b52"), ("Gothic Blue", "#283872"), ("Ultramarine Blue", "#274588"),
                ("Alpha Blue", "#687ab6"), ("Baron Blue", "#8596c8"), ("Augur Blue", "#b3c2e3")),
            Family("Vivid Blues",
                ("Imperial Navy", "#00335f"), ("Regal Blue", "#004b85"), ("Royal Blue", "#0059a7"),
                ("Crystal Blue", "#006cb4"), ("Arctic Gem", "#008ecd"), ("Bright Sapphire", "#92ccec")),
            Family("Deep Green-Blues",
                ("Deep Ocean Blue", "#233243"), ("Abyssal Blue", "#004f6a"), ("Tidal Blue", "#006c94"),
                ("Phalanx Blue", "#0086ae"), ("Shieldwall Blue", "#009ec7"), ("Aegis Aqua", "#3fafd0")),
            Family("Turquoises",
                ("Deep Azure", "#006879"), ("Hydra Turquoise", "#008d94"), ("Turquoise Siren", "#21aab1"),
                ("Aquamarine", "#5ebec5"), ("Neptune Glow", "#81cace"), ("Marine Mist", "#9cd5d0")),
            Family("Teals",
                ("Scarab Green", "#1c3c40"), ("Temple Gate Teal", "#005d4f"), ("Pharaoh Guard", "#00765a"),
                ("Talisman Teal", "#00a787"), ("Aqua Alchemy", "#27b399"), ("Amulet Aqua", "#6ac1ae")),
            Family("Vibrant Greens",
                ("Emerald Forest", "#51af35"), ("Leafy Green", "#7ab717"), ("Rainforest", "#8cbd0b"),
                ("Electric Lime", "#b0c900"), ("Vivid Volt", "#d0da39"), ("Afterglow", "#e5e989")),
            Family("Desaturated Cool Greens",
                ("Evergreen Fog", "#3d675d"), ("Medieval Forest", "#447c6b"), ("Patagon Pine", "#66927d"),
                ("Autumn Sage", "#75ab93"), ("Forest Faun", "#a3caaa"), ("Mossy Green", "#b6dabc")),
            Family("Olive Greens",
                ("Woodland Camo", "#3d4a32"), ("Army Green", "#4d5e37"), ("Camouflage Green", "#617046"),
                ("Olive Drab", "#728451"), ("Necrotic Flesh", "#a4ad84"), ("Grotesque Green", "#b7c29c")),
            Family("Deep Greens",
                ("Angel Green", "#1f3d2a"), ("Guardian Green", "#226042"), ("Greenskin", "#0e6c2d"),
                ("Eternal Hunt", "#2f8f37"), ("Wild Green", "#45a346"), ("Ferocious Green", "#73bc68")),
            Family("Ochres & Tans",
                ("Tundra Taupe", "#51543b"), ("Prairie Ochre", "#6e683f"), ("Desert Yellow", "#87743f"),
                ("Wasteland Clay", "#aa8c4d"), ("Burnt Turf", "#c39b4e"), ("Barren Dune", "#dbbc6b")),
            Family("Light Neutrals",
                ("Dusty Skull", "#928771"), ("Tomb King Tan", "#ab9a7f"), ("Skeleton Bone", "#c4b797"),
                ("Ancient Stone", "#d3cba8"), ("Bony Spikes", "#eadac1"), ("Pale Sand", "#f3edd8")),
            Family("Yellows",
                ("Demigod Flames", "#d27a33"), ("Fiendish Yellow", "#e49e12"), ("Daemonic Yellow", "#fecc00"),
                ("Warped Yellow", "#fad333"), ("Space Dust", "#f7e07e"), ("Ice Yellow", "#feecac")),
            Family("Oranges",
                ("Molten Lava", "#eb3f20"), ("Burning Ore", "#ed590d"), ("Lava Orange", "#f06e00"),
                ("Flickering Flame", "#f18427"), ("Glowing Inferno", "#f4982f"), ("Inner Light", "#fac84e")),
            Family("Warm Reds",
                ("Resplendent Red", "#982427"), ("Angelic Red", "#cb333b"), ("Legendary Red", "#e9392f"),
                ("Sacred Scarlet", "#ec543d"), ("Violent Vermillion", "#ef7559"), ("Raging Rouge", "#f28d7f")),
            Family("Browns",
                ("Brigandine Brown", "#2c241d"), ("Bootstrap Brown", "#4d322b"), ("Leather Brown", "#694433"),
                ("Paratrooper Tan", "#9b7c6b"), ("Command Khaki", "#ba9a89"), ("Urban Buff", "#d8b5a6")),
            Family("Cool Reds",
                ("Basilisk Red", "#702837"), ("Wyvern Fury", "#992e3e"), ("Dragon Red", "#c02431"),
                ("Pure Red", "#c8030e"), ("Blood Chalice", "#d94244"), ("Raging Rose", "#e15d5c")),
            Family("Ruddy Browns",
                ("Oak Brown", "#3f2e2a"), ("Tree Ancient", "#553d34"), ("Dryad Brown", "#663f32"),
                ("Fur Brown", "#7e4943"), ("Ruddy Umber", "#9b5b4b"), ("Buffed Hide", "#cb8d83")),
            Family("Purples",
                ("Terrestrial Titan", "#373348"), ("Alien Purple", "#5e4796"), ("Cultist Purple", "#6c5aa3"),
                ("Hexed Violet", "#9381b8"), ("Violet Coven", "#af9ac7"), ("Kraken Lavender", "#d9cee6")),
            Family("Pinks",
                ("Wicked Pink", "#cd0077"), ("Impish Rouge", "#d94a93"), ("Pixie Pink", "#d969a3"),
                ("Weird Elixir", "#e094be"), ("Pink Potion", "#f3bad6"), ("Doomfire Drab", "#f8d9e9")),
            Family("Desaturated Violets",
                ("Mulled Berry", "#663749"), ("Moldy Wine", "#82334f"), ("Elder Flower", "#aa5779"),
                ("Forbidden Fruit", "#d48eaa"), ("Figgy Pink", "#e4b2c7"), ("Wilted Rose", "#edc9db")),
            Family("Magentas",
                ("Diabolic Plum", "#54296a"), ("Magecast Magenta", "#723389"), ("Warlock Magenta", "#844091"),
                ("Spellbound Fuchsia", "#ad428f"), ("Enchanted Pink", "#b768a5"), ("Diviner Light", "#d6a0c7")),
            Family("Rose Skin Tones",
                ("Moonstone Skin", "#c87b72"), ("Agate Skin", "#da8679"), ("Barbarian Flesh", "#f59a7f"),
                ("Ruby Skin", "#f2b89e"), ("Opal Skin", "#fcd5c7"), ("Pearl Skin", "#fce5db")),
            Family("Deep Skin Tones",
                ("Obsidian Skin", "#4e4545"), ("Onyx Skin", "#6c5048"), ("Mocca Skin", "#8a6b5c"),
                ("Amber Skin", "#c5a383"), ("Dorado Skin", "#efc99f"), ("Quartz Skin", "#f0d4b4")),
            Family("Warm Skin Tones",
                ("Carnelian Skin", "#644144"), ("Tiger's Eye Skin", "#934f4e"), ("Topaz Skin", "#b7564c"),
                ("Jasper Skin", "#be7873"), ("Tourmaline Skin", "#e19b8a"), ("Leopard Stone Skin", "#e9b6b3")),
        ]);

    private static ChartFamily Family(string name, params (string Name, string Hex)[] paints) =>
        new(name, [.. paints.Select(p => new ChartPaint(p.Name, p.Hex))]);
}
