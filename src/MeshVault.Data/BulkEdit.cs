using MeshVault.Core.Models;
using MeshVault.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Data;

/// <summary>What a bulk edit asked for. Every part is optional.</summary>
/// <remarks>
/// One record rather than a call per action, because someone tidying a folder
/// of downloads sets the designer, adds a tag and files the lot in a collection
/// as a single thought. Applying them together also means one pass and one
/// undo-sized unit of surprise.
/// </remarks>
public record BulkEdit
{
    /// <summary>Designer to assign. Ignored when <see cref="ClearDesigner"/> is set.</summary>
    public string? DesignerName { get; init; }

    /// <summary>Removes the designer from every selected model.</summary>
    public bool ClearDesigner { get; init; }

    public IReadOnlyList<string> TagsToAdd { get; init; } = [];
    public IReadOnlyList<string> TagsToRemove { get; init; } = [];

    /// <summary>Collection to add to or remove from, paired with <see cref="RemoveFromCollection"/>.</summary>
    public int? CollectionId { get; init; }
    public bool RemoveFromCollection { get; init; }

    /// <summary>True to favorite, false to unfavorite, null to leave alone.</summary>
    public bool? Favorite { get; init; }

    public bool IsEmpty =>
        !ClearDesigner
        && string.IsNullOrWhiteSpace(DesignerName)
        && TagsToAdd.Count == 0
        && TagsToRemove.Count == 0
        && CollectionId is null
        && Favorite is null;
}

/// <summary>What a bulk edit actually changed, for reporting back to the user.</summary>
public record BulkEditResult(
    int Models,
    int DesignerChanged,
    int TagsAdded,
    int TagsRemoved,
    int CollectionChanged,
    int FavoritesChanged)
{
    public bool ChangedNothing =>
        DesignerChanged == 0 && TagsAdded == 0 && TagsRemoved == 0
        && CollectionChanged == 0 && FavoritesChanged == 0;
}
