namespace MeshVault.Core.Models;

/// <summary>A root folder on disk that MeshVault scans for models.</summary>
public class Library
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>When false, MeshVault will never move or rename files under this root.</summary>
    public bool AllowOrganize { get; set; }
    public DateTimeOffset? LastScannedUtc { get; set; }

    public List<ModelEntry> Models { get; set; } = [];
}
