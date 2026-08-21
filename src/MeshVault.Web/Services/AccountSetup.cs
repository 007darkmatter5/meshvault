using MeshVault.Core.Models;
using MeshVault.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Web.Services;

/// <summary>
/// Rules about who may create an account, and what happens to the data that
/// existed before there were accounts.
/// </summary>
public class AccountSetup(
    UserManager<ApplicationUser> users,
    RoleManager<IdentityRole> roles,
    IDbContextFactory<MeshVaultDbContext> factory,
    SettingsStore settings,
    ILogger<AccountSetup> log)
{
    /// <summary>True when nobody has signed up yet, so the instance is unclaimed.</summary>
    public async Task<bool> IsUnclaimedAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return !await db.Users.AnyAsync(ct);
    }

    /// <summary>
    /// Whether the register page accepts new sign-ups. Open while unclaimed so
    /// the owner can create the first account, then closed unless an admin
    /// deliberately opens it.
    /// </summary>
    public async Task<bool> RegistrationAllowedAsync(CancellationToken ct = default)
    {
        if (await IsUnclaimedAsync(ct)) return true;
        return await settings.GetBoolAsync(SettingKeys.RegistrationOpen, false, ct);
    }

    public Task SetRegistrationOpenAsync(bool open, CancellationToken ct = default) =>
        settings.SetBoolAsync(SettingKeys.RegistrationOpen, open, ct);

    public async Task EnsureRolesAsync()
    {
        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole(role));
        }
    }

    /// <summary>
    /// Creates an account. The first one is an admin and adopts everything that
    /// was created before sign-in existed.
    /// </summary>
    public async Task<IdentityResult> RegisterAsync(
        string userName, string? email, string password, string? displayName, CancellationToken ct = default)
    {
        await EnsureRolesAsync();

        var first = await IsUnclaimedAsync(ct);
        if (!first && !await RegistrationAllowedAsync(ct))
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = "RegistrationClosed",
                Description = "Registration is closed. Ask an administrator for an account.",
            });
        }

        var user = new ApplicationUser
        {
            UserName = userName.Trim(),
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded) return result;

        await users.AddToRoleAsync(user, first ? Roles.Admin : Roles.Member);

        if (first)
        {
            await AdoptLegacyDataAsync(user.Id, ct);
            // An unclaimed instance is open to anyone; once claimed it must not be.
            await SetRegistrationOpenAsync(false, ct);
        }

        return result;
    }

    /// <summary>
    /// Hands collections and favorites created before sign-in existed to the
    /// first real account, so the owner does not lose them.
    /// </summary>
    private async Task AdoptLegacyDataAsync(string userId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var collections = await db.Collections
            .Where(c => c.OwnerId == Users.LocalUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.OwnerId, userId), ct);

        var favorites = await db.Favorites
            .Where(f => f.UserId == Users.LocalUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.UserId, userId), ct);

        if (collections + favorites > 0)
        {
            log.LogInformation(
                "Transferred {Collections} collection(s) and {Favorites} favorite(s) to the first account",
                collections, favorites);
        }
    }
}
