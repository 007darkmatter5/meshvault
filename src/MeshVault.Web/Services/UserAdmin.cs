using MeshVault.Core.Models;
using MeshVault.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Web.Services;

public record UserSummary(
    string Id,
    string UserName,
    string? DisplayName,
    string? Email,
    string Role,
    DateTimeOffset CreatedUtc,
    // No collection count: collections are shared by the whole library rather
    // than owned, so there is no per-account number to report and nothing an
    // account takes with it when it goes. Favorites really are per-person.
    int Favorites,
    bool IsLockedOut);

/// <summary>
/// Administration of accounts.
/// </summary>
/// <remarks>
/// The safeguards here matter more than the features: it must not be possible
/// to lock every administrator out of the instance, which on a self-hosted app
/// would mean editing the database by hand to recover.
/// </remarks>
public class UserAdmin(
    UserManager<ApplicationUser> users,
    RoleManager<IdentityRole> roles,
    IDbContextFactory<MeshVaultDbContext> factory,
    ILogger<UserAdmin> log)
{
    public async Task<List<UserSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var all = await db.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync(ct);

        var roleByUser = await db.UserRoles
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .ToDictionaryAsync(x => x.UserId, x => x.Name ?? Roles.Member, ct);

        var favorites = await db.Favorites.AsNoTracking()
            .GroupBy(f => f.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);

        var now = DateTimeOffset.UtcNow;

        return all.Select(u => new UserSummary(
            u.Id,
            u.UserName ?? "",
            u.DisplayName,
            u.Email,
            roleByUser.GetValueOrDefault(u.Id, Roles.Member),
            u.CreatedUtc,
            favorites.GetValueOrDefault(u.Id, 0),
            u.LockoutEnd is { } end && end > now)).ToList();
    }

    public async Task<int> CountAdminsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.UserRoles
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name)
            .CountAsync(name => name == Roles.Admin, ct);
    }

    /// <summary>
    /// Creates an account directly, so someone can be added without opening
    /// registration to anyone who can reach the site.
    /// </summary>
    public async Task<IdentityResult> CreateAsync(
        string userName, string? email, string? displayName, string password, string role,
        CancellationToken ct = default)
    {
        foreach (var name in Roles.All)
        {
            if (!await roles.RoleExistsAsync(name)) await roles.CreateAsync(new IdentityRole(name));
        }

        var user = new ApplicationUser
        {
            UserName = userName.Trim(),
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        // Asked for before the account is made, so a refusal leaves nothing
        // half-created to clean up.
        if (role == Roles.Admin && await CountAdminsAsync(ct) > 0)
            return Fail("OneAdmin", OneAdminReason);

        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded) return result;

        await users.AddToRoleAsync(user, Roles.All.Contains(role) ? role : Roles.Member);
        log.LogInformation("Created account {User} as {Role}", user.UserName, role);
        return result;
    }

    /// <summary>
    /// Why a second administrator is refused.
    /// </summary>
    /// <remarks>
    /// One administrator, and everyone else reads. The reason is not really
    /// about accounts: <c>{collection}</c> is a folder token whose value is
    /// per-user, so the folder tree Organize produces depends on who is signed
    /// in when it runs. Two administrators would file the same library two
    /// different ways on disk. Until a collection is a property of the model
    /// rather than of the viewer, one administrator is what keeps the layout
    /// single-valued.
    /// </remarks>
    public const string OneAdminReason =
        "MeshVault has one owner. Everyone else reads the library, keeping their "
        + "own collections and favourites. Add this account as a viewer, then set its role "
        + "to owner from the list to hand ownership over.";

    /// <summary>Moves an account between roles, refusing to remove the last administrator.</summary>
    public async Task<IdentityResult> SetRoleAsync(string userId, string role, CancellationToken ct = default)
    {
        if (!Roles.All.Contains(role))
            return Fail("UnknownRole", $"There is no role called \"{role}\".");

        var user = await users.FindByIdAsync(userId);
        if (user is null) return Fail("NoSuchUser", "That account no longer exists.");

        var current = await users.GetRolesAsync(user);
        if (current.Contains(role) && current.Count == 1) return IdentityResult.Success;

        if (current.Contains(Roles.Admin) && role != Roles.Admin && await CountAdminsAsync(ct) <= 1)
        {
            return Fail("LastAdmin",
                "This account owns this MeshVault. Make someone else the owner instead — that hands it over in one step.");
        }

        // Promoting somebody hands the role over rather than adding a second.
        //
        // Refusing it instead would lock the pair: demoting the only
        // administrator is refused just above, so with no transfer there is no
        // sequence of single steps that moves the role at all. Doing it here
        // keeps the count at exactly one through every state, with no moment
        // where the library has none.
        if (role == Roles.Admin && !current.Contains(Roles.Admin))
        {
            foreach (var previous in await users.GetUsersInRoleAsync(Roles.Admin))
            {
                await users.RemoveFromRolesAsync(previous, await users.GetRolesAsync(previous));
                await users.AddToRoleAsync(previous, Roles.Member);
                log.LogInformation(
                    "Administrator handed from {From} to {To}", previous.UserName, user.UserName);
            }
        }

        await users.RemoveFromRolesAsync(user, current);
        return await users.AddToRoleAsync(user, role);
    }

    public async Task<IdentityResult> ResetPasswordAsync(string userId, string newPassword,
        CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null) return Fail("NoSuchUser", "That account no longer exists.");

        // An administrator resets without knowing the old password, so go
        // through a generated token rather than ChangePasswordAsync.
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, newPassword);

        if (result.Succeeded)
        {
            // Clear any lockout, otherwise a reset does not actually let them in.
            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
            log.LogInformation("Reset the password for {User}", user.UserName);
        }

        return result;
    }

    public async Task<IdentityResult> SetLockedOutAsync(string userId, bool lockedOut,
        CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null) return Fail("NoSuchUser", "That account no longer exists.");

        if (lockedOut && await IsOnlyAdminAsync(user, ct))
            return Fail("LastAdmin", "This is the only administrator, so it cannot be suspended.");

        return lockedOut
            ? await users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue)
            : await users.SetLockoutEndDateAsync(user, null);
    }

    /// <summary>
    /// Deletes an account and the personal data attached to it. Refuses to
    /// remove the account doing the deleting, or the last administrator.
    /// </summary>
    public async Task<IdentityResult> DeleteAsync(string userId, string actingUserId,
        CancellationToken ct = default)
    {
        if (userId == actingUserId)
            return Fail("Self", "You cannot delete the account you are signed in with.");

        var user = await users.FindByIdAsync(userId);
        if (user is null) return Fail("NoSuchUser", "That account no longer exists.");

        if (await IsOnlyAdminAsync(user, ct))
            return Fail("LastAdmin", "This is the only administrator, so it cannot be deleted.");

        // Favorites reference the owner by id rather than by a foreign key, so
        // they would otherwise be left behind unreachable.
        //
        // Collections are deliberately not touched. They used to go with the
        // account that made them, which was right while they were private; now
        // they name folders on disk and belong to the library, so deleting a
        // member would have re-filed everything they had ever collected.
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await db.Favorites.Where(f => f.UserId == userId).ExecuteDeleteAsync(ct);
        }

        var result = await users.DeleteAsync(user);
        if (result.Succeeded) log.LogInformation("Deleted the account {User}", user.UserName);
        return result;
    }

    private async Task<bool> IsOnlyAdminAsync(ApplicationUser user, CancellationToken ct)
    {
        var roleNames = await users.GetRolesAsync(user);
        return roleNames.Contains(Roles.Admin) && await CountAdminsAsync(ct) <= 1;
    }

    private static IdentityResult Fail(string code, string description) =>
        IdentityResult.Failed(new IdentityError { Code = code, Description = description });
}
