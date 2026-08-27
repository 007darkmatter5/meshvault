using Microsoft.AspNetCore.Identity;

namespace MeshVault.Data;

/// <summary>A person with an account on this MeshVault instance.</summary>
public class ApplicationUser : IdentityUser
{
    /// <summary>Shown instead of the login name where there is room for it.</summary>
    public string? DisplayName { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public string FriendlyName => string.IsNullOrWhiteSpace(DisplayName) ? UserName ?? "Someone" : DisplayName;
}

public static class Roles
{
    /// <summary>Manages libraries, scans, imports and other accounts.</summary>
    /// <remarks>
    /// There is exactly one. <see cref="MeshVault.Web.Services.UserAdmin"/> holds
    /// that invariant, and the reason is in CLAUDE.md: <c>{collection}</c> is a
    /// folder token resolved per-user, so two of these would file the same
    /// library two different ways on disk.
    /// </remarks>
    public const string Admin = "Admin";

    /// <summary>Browses the catalog and keeps their own collections and favorites.</summary>
    public const string Member = "Member";

    public static readonly string[] All = [Admin, Member];

    /// <summary>
    /// What to call a role on screen.
    /// </summary>
    /// <remarks>
    /// The stored strings stay "Admin" and "Member" — they are in the database,
    /// in policy declarations and in every claim already issued, and renaming
    /// them would be a migration for no gain. What changed is the shape of the
    /// thing: with one administrator and everyone else reading, "owner" and
    /// "viewer" say it, where "administrator" and "member" suggest a hierarchy
    /// you could have several rungs of.
    /// </remarks>
    public static string Label(string role) => role == Admin ? "Owner" : "Viewer";
}
