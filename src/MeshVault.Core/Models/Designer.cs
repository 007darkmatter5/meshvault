namespace MeshVault.Core.Models;

/// <summary>
/// The person or studio who made a model. First-class rather than a text field
/// so that "show me everything by this designer" is a real query and renaming
/// fixes every model at once.
/// </summary>
public class Designer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Lowercased <see cref="Name"/>, kept unique so designers don't fork on casing.</summary>
    public string NormalizedName { get; set; } = "";
    /// <summary>Their page on MakerWorld, Printables, Patreon, etc.</summary>
    public string? ProfileUrl { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }

    public List<ModelEntry> Models { get; set; } = [];
}
