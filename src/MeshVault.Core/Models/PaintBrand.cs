namespace MeshVault.Core.Models;

/// <summary>
/// A paint maker, and the ranges it sells under.
/// </summary>
/// <remarks>
/// Curated rather than seeded. A built-in list of brands is wrong the week it
/// ships and grows stale from there, and most people own two or three makes
/// rather than fourteen. Administrators add what this instance actually uses.
///
/// Nothing references these by id. A paint keeps the brand and range it was
/// given as plain text, so renaming a brand here is a change to a suggestion
/// list rather than a rewrite of everyone's rack.
/// </remarks>
public class PaintBrand
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Lower-cased, so one instance cannot hold both Citadel and citadel.</summary>
    public string NormalizedName { get; set; } = "";

    public List<PaintRange> Ranges { get; set; } = [];
}

/// <summary>
/// A line within a brand: Citadel's Base and Contrast, Vallejo's Model Color.
/// </summary>
/// <remarks>
/// Belongs to exactly one brand, which is what lets the range dropdown narrow
/// once a brand is chosen. "Base" means Citadel's when Citadel is selected and
/// nothing otherwise.
/// </remarks>
public class PaintRange
{
    public int Id { get; set; }

    public int PaintBrandId { get; set; }
    public PaintBrand? Brand { get; set; }

    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
}
