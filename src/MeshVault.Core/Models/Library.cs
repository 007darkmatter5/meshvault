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

    /// <summary>
    /// Folder inside this library where new arrivals are dropped, relative to
    /// the root — "inbox". Null when the library has no such folder.
    /// </summary>
    /// <remarks>
    /// Inside the library rather than a library of its own, so filing something
    /// is a rename on one volume instead of a copy between two, and the model
    /// keeps its id — its tags, notes and grouping follow the new path rather
    /// than being rebuilt.
    ///
    /// Nothing moves things out of here. A folder template never produces a
    /// path inside the inbox, so filing a model empties it as a side effect.
    /// </remarks>
    public string? InboxPath { get; set; }

    // How this library is laid out. Kept per library rather than globally: a
    // staging disk and a finished collection are rarely organised the same way,
    // and the templates are the sort of thing somebody settles on once and does
    // not want to retype on every visit.

    /// <summary>Folder each model goes in. Null until someone has chosen one.</summary>
    public string? FolderTemplate { get; set; }

    /// <summary>Name for each file inside that folder, used only when renaming is on.</summary>
    public string? FileTemplate { get; set; }

    /// <summary>Whether organising this library also renames the files inside.</summary>
    public bool RenameFiles { get; set; }

    public DateTimeOffset? LastScannedUtc { get; set; }

    public List<ModelEntry> Models { get; set; } = [];
}
