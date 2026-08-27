namespace MeshVault.Core.Services;

public enum ModelSort { Name, Newest, Largest }

/// <summary>Everything the browse page can filter and sort by.</summary>
public record ModelQuery
{
    public string? Search { get; init; }
    public int? LibraryId { get; init; }
    public int? DesignerId { get; init; }
    public int? CollectionId { get; init; }
    public string? SourceSite { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool FavoritesOnly { get; init; }
    /// <summary>Models with no designer set, for filling in provenance gaps.</summary>
    public bool MissingDesigner { get; init; }
    /// <summary>Models with no source URL set.</summary>
    public bool MissingSource { get; init; }

    /// <summary>Models in none of your collections, which file under the fallback.</summary>
    public bool MissingCollection { get; init; }

    /// <summary>Only what is still sitting in a library's inbox, waiting to be filed.</summary>
    public bool UnfiledOnly { get; init; }
    public ModelSort Sort { get; init; } = ModelSort.Name;
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 60;
}

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;
}
