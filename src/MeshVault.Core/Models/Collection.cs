namespace MeshVault.Core.Models;

/// <summary>
/// A user-curated group of models. Owned by whoever made it, so that when real
/// accounts arrive people do not see each other's collections.
/// </summary>
public class Collection
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Lowercased <see cref="Name"/>, so one user cannot have both "To Print" and "to print".</summary>
    public string NormalizedName { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Owning user id. <see cref="Users.LocalUserId"/> until accounts exist.</summary>
    public string OwnerId { get; set; } = Users.LocalUserId;
    public DateTimeOffset CreatedUtc { get; set; }

    public List<ModelEntry> Models { get; set; } = [];
}

/// <summary>
/// A model starred by one user. A join row rather than a flag on the model,
/// because two people can disagree about what is a favorite.
/// </summary>
public class ModelFavorite
{
    public int Id { get; set; }
    public int ModelEntryId { get; set; }
    public ModelEntry? ModelEntry { get; set; }
    public string UserId { get; set; } = Users.LocalUserId;
    public DateTimeOffset CreatedUtc { get; set; }
}

public static class Users
{
    /// <summary>
    /// Stands in for the signed-in user until authentication is added. Existing
    /// rows carrying this id get remapped to the first real account at that point.
    /// </summary>
    public const string LocalUserId = "local";

    /// <summary>
    /// Owner id for a visitor who is not signed in, used when public browsing
    /// is turned on.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="LocalUserId"/>. That id is a real owner with
    /// real rows behind it, so handing it to every anonymous visitor would show
    /// them one account's collections and favorites and let them all share a
    /// single identity. This one owns nothing and never will.
    /// </remarks>
    public const string AnonymousId = "anonymous";
}
