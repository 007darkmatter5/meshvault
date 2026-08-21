namespace MeshVault.Core.Models;

/// <summary>
/// A label shared across the catalog. Tags describe the model, not the viewer,
/// so they are not owned by a user.
/// </summary>
public class Tag
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Lowercased <see cref="Name"/>, kept unique so tags don't fork on casing.</summary>
    public string NormalizedName { get; set; } = "";
    public string? Color { get; set; }

    public List<ModelEntry> Models { get; set; } = [];
}
