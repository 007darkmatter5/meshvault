namespace MeshVault.Core.Models;

/// <summary>How much of a paint is left, so a scheme can say what needs buying.</summary>
/// <remarks>
/// The numbers are fixed, not positional: they are already in people's
/// databases, so a new state has to be appended rather than slotted in
/// wherever it reads best.
/// </remarks>
public enum PaintStock
{
    Have = 0,
    Low = 1,
    Out = 2,

    /// <summary>
    /// On the shopping list rather than the shelf. Counts as not owned, so a
    /// scheme needing it still says so.
    /// </summary>
    Want = 3,
}

public static class PaintStocks
{
    /// <summary>
    /// Whether a bottle in this state could actually be painted with today.
    /// Running low still counts; out and wanted do not.
    /// </summary>
    public static bool IsOnTheShelf(this PaintStock stock) =>
        stock is PaintStock.Have or PaintStock.Low;
}

public enum PaintFinish
{
    Unspecified = 0,
    Matte,
    Satin,
    Gloss,
    Metallic,
    Wash,
    Contrast,
    Primer,
    Varnish,
}

/// <summary>
/// One bottle on somebody's shelf.
/// </summary>
/// <remarks>
/// Owned rather than shared: a paint rack is a physical thing a person owns,
/// and two people in a house rarely share bottles. Schemes are visible to
/// everyone, so a paint referenced by one stays readable in that context even
/// when it is not on your own shelf - which is what makes "you would need to
/// buy these three" possible.
/// </remarks>
public class Paint
{
    public int Id { get; set; }

    /// <summary>The account whose rack this bottle sits on.</summary>
    public string OwnerId { get; set; } = Users.LocalUserId;

    public string Name { get; set; } = "";

    /// <summary>Lower-cased name, for matching without duplicating a bottle.</summary>
    public string NormalizedName { get; set; } = "";

    /// <summary>Citadel, Vallejo, Army Painter and so on.</summary>
    public string? Brand { get; set; }

    /// <summary>The range within a brand, such as Base, Layer or Speedpaint.</summary>
    public string? Range { get; set; }

    /// <summary>Swatch colour as #rrggbb, for showing the scheme at a glance.</summary>
    public string? Hex { get; set; }

    public PaintFinish Finish { get; set; }
    public PaintStock Stock { get; set; }

    /// <summary>
    /// How many bottles of it are on the shelf.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Stock"/> rather than derived from it. Two
    /// bottles both a third full is "running low" with a quantity of two, and
    /// one unopened bottle is "have" with a quantity of one; neither number can be
    /// worked out from the other. Existing rows default to one, which is what
    /// a rack recorded before this existed meant.
    /// </remarks>
    public int Quantity { get; set; } = 1;

    public string? Notes { get; set; }
    public DateTimeOffset AddedUtc { get; set; }

    public List<PaintStep> Steps { get; set; } = [];
}

/// <summary>
/// A named recipe for painting one model.
/// </summary>
/// <remarks>
/// Owned by whoever wrote it but visible to everyone, so a model can carry
/// several: the same miniature painted as a red dragon and a bronze one are two
/// recipes, not a disagreement. Only the owner may change theirs.
/// </remarks>
public class PaintScheme
{
    public int Id { get; set; }

    public int ModelEntryId { get; set; }
    public ModelEntry? ModelEntry { get; set; }

    public string OwnerId { get; set; } = Users.LocalUserId;

    /// <summary>Display name of the account that wrote it, so a shared scheme has a face.</summary>
    public string? OwnerName { get; set; }

    public string Name { get; set; } = "";
    public string? Notes { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }

    public List<PaintStep> Steps { get; set; } = [];
    public List<SchemePhoto> Photos { get; set; } = [];
}

/// <summary>One instruction in a scheme: a paint, applied a way, somewhere.</summary>
public class PaintStep
{
    public int Id { get; set; }

    public int PaintSchemeId { get; set; }
    public PaintScheme? PaintScheme { get; set; }

    /// <summary>
    /// The bottle used, when it is still on a rack this instance knows about.
    /// </summary>
    /// <remarks>
    /// Nullable, and paired with the name below, so deleting a paint edits an
    /// inventory rather than quietly destroying every recipe that mentioned it.
    /// </remarks>
    public int? PaintId { get; set; }
    public Paint? Paint { get; set; }

    /// <summary>
    /// The paint's name as written when the step was recorded, kept even if the
    /// bottle is deleted or belongs to somebody else's rack.
    /// </summary>
    public string PaintName { get; set; } = "";

    /// <summary>Swatch recorded with the step, for the same reason.</summary>
    public string? Hex { get; set; }

    /// <summary>Basecoat, wash, drybrush, edge highlight, glaze.</summary>
    public string? Technique { get; set; }

    /// <summary>What it went on: scales, wings, base, cloak.</summary>
    public string? Area { get; set; }

    public string? Notes { get; set; }

    /// <summary>Position in the recipe, from zero.</summary>
    public int Order { get; set; }
}

/// <summary>
/// A photo of a finished model, hung off the scheme that produced it.
/// </summary>
/// <remarks>
/// Stored under the data directory beside thumbnails, never in the library. The
/// library holds files the user put there and is usually mounted read-only;
/// anything the app generates belongs with the app's own data.
/// </remarks>
public class SchemePhoto
{
    public int Id { get; set; }

    public int PaintSchemeId { get; set; }
    public PaintScheme? PaintScheme { get; set; }

    /// <summary>File name under the photos directory, including its extension.</summary>
    public string FileName { get; set; } = "";

    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string? Caption { get; set; }
    public DateTimeOffset AddedUtc { get; set; }
}
