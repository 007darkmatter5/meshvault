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
    public const string Admin = "Admin";

    /// <summary>Browses the catalog and keeps their own collections and favorites.</summary>
    public const string Member = "Member";

    public static readonly string[] All = [Admin, Member];
}
