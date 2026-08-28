namespace MeshVault.Core.Models;

/// <summary>
/// One application of an organize plan, kept so it can be taken back.
/// </summary>
/// <remarks>
/// The page used to say "There is no undo. Back the library up first" — true,
/// and no comfort at all once several hundred files have been renamed under a
/// template that turned out to be wrong. Restoring a share from backup is a far
/// worse afternoon than pressing a button.
///
/// The executor already does the hard part: it moves file by file and records
/// each step as it goes, so that every step is either done and written down or
/// not attempted. This keeps that record instead of discarding it.
/// </remarks>
public class OrganizeRun
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public DateTimeOffset RanUtc { get; set; }

    /// <summary>When it was taken back, or null while it still stands.</summary>
    public DateTimeOffset? UndoneUtc { get; set; }

    /// <summary>
    /// Files removed as proven-identical copies.
    /// </summary>
    /// <remarks>
    /// The one part of a run that cannot come back: the bytes are gone. Every
    /// one was proved byte-for-byte identical to a file that survived, so an
    /// undo restores the tree minus copies of things still in it — but that is
    /// a real difference and the page says so rather than burying it.
    /// </remarks>
    public int FilesDeleted { get; set; }

    /// <summary>
    /// Models the run folded into others, whose own tags and notes went with
    /// them. Merging is not reversible from this record, so a run that did any
    /// is undone as far as the files go and no further.
    /// </summary>
    public int ModelsRemoved { get; set; }

    public List<OrganizeStep> Steps { get; set; } = [];

    /// <summary>Whether taking this run back would restore everything it did.</summary>
    public bool FullyReversible => FilesDeleted == 0 && ModelsRemoved == 0;
}

/// <summary>One thing a run moved, and where it was before.</summary>
/// <param name="FileId">
/// Null for a model's own folder, which moves without being a file.
/// </param>
public class OrganizeStep
{
    public int Id { get; set; }
    public int OrganizeRunId { get; set; }
    public OrganizeRun? Run { get; set; }

    public int? FileId { get; set; }
    public int? ModelId { get; set; }

    /// <summary>Where it was before the run, relative to the library root.</summary>
    public string From { get; set; } = "";

    /// <summary>Where the run put it.</summary>
    public string To { get; set; } = "";

    /// <summary>
    /// The model that owned this file beforehand, so a merge can be unpicked as
    /// far as ownership goes even when the folder it came from has gone.
    /// </summary>
    public int? FromModelId { get; set; }

    /// <summary>
    /// Whether the run created the model this file was given to. On the way
    /// back such a model is left empty, and an empty model it invented is one
    /// it should take away again.
    /// </summary>
    public bool ToModelCreated { get; set; }
}
