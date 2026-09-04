namespace MeshVault.Core.Models;

/// <summary>
/// A curated group of models.
/// </summary>
/// <remarks>
/// Shared rather than owned, because a collection names a folder on disk:
/// <c>{collection}</c> is a level in the folder template. That makes it a fact
/// about the model rather than about whoever is looking at it.
///
/// While they were per-user the layout of the share depended on who was signed
/// in, and the app had to allow exactly one administrator to stop two people
/// filing the same library two different ways. Measured on the author's
/// library at the time: 2 models unfiled as the owner, 106 as a second account.
///
/// <see cref="ModelFavorite"/> is the contrast worth keeping in mind, and stays
/// per-user. Two people can disagree about what is a favourite and nothing on
/// disk moves.
/// </remarks>
public class Collection
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Lowercased <see cref="Name"/>, so the library cannot hold both "To Print" and "to print".</summary>
    public string NormalizedName { get; set; } = "";
    public string? Description { get; set; }
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
