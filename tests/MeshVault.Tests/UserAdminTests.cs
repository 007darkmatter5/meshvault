using MeshVault.Core.Models;
using MeshVault.Data;
using MeshVault.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshVault.Tests;

/// <summary>
/// The safeguards matter more than the features. Removing the last
/// administrator would leave a self-hosted instance unmanageable without
/// editing the database by hand.
/// </summary>
public class UserAdminTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly ServiceProvider _services;

    public UserAdminTests()
    {
        _conn.Open();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        // Password reset tokens are data-protected, so the provider must exist.
        services.AddDataProtection();
        services.AddDbContextFactory<MeshVaultDbContext>(o => o.UseSqlite(_conn));
        services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<MeshVaultDbContext>>().CreateDbContext());

        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.Password.RequiredLength = 10;
                o.Password.RequireDigit = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireLowercase = false;
                o.User.RequireUniqueEmail = false;
            })
            .AddRoles<IdentityRole>()
            .AddDefaultTokenProviders()
            .AddEntityFrameworkStores<MeshVaultDbContext>();

        services.AddScoped<SettingsStore>();
        services.AddScoped<AccountSetup>();
        services.AddScoped<UserAdmin>();

        _services = services.BuildServiceProvider();

        using var db = _services.GetRequiredService<IDbContextFactory<MeshVaultDbContext>>().CreateDbContext();
        db.Database.EnsureCreated();
    }

    private UserAdmin Admin => _services.GetRequiredService<UserAdmin>();
    private AccountSetup Accounts => _services.GetRequiredService<AccountSetup>();
    private UserManager<ApplicationUser> Users => _services.GetRequiredService<UserManager<ApplicationUser>>();

    private MeshVaultDbContext NewDb() =>
        _services.GetRequiredService<IDbContextFactory<MeshVaultDbContext>>().CreateDbContext();

    private async Task<string> SeedOwnerAsync()
    {
        await Accounts.RegisterAsync("owner", null, "a-long-passphrase", "Owner");
        return (await Users.FindByNameAsync("owner"))!.Id;
    }

    private async Task<string> AddMemberAsync(string name = "member")
    {
        var result = await Admin.CreateAsync(name, null, null, "another-passphrase", Roles.Member);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        return (await Users.FindByNameAsync(name))!.Id;
    }

    // Listing ---------------------------------------------------------------

    [Fact]
    public async Task Lists_accounts_with_their_role_and_owned_data()
    {
        var ownerId = await SeedOwnerAsync();
        await AddMemberAsync();

        await using (var db = NewDb())
        {
            db.Libraries.Add(new Library { Name = "L", Path = "/l" });
            db.Models.Add(new ModelEntry { LibraryId = 1, Name = "M", RelativePath = "m" });
            db.Collections.Add(new Collection { Name = "C", NormalizedName = "c" });
            await db.SaveChangesAsync();
            db.Favorites.Add(new ModelFavorite { ModelEntryId = 1, UserId = ownerId });
            await db.SaveChangesAsync();
        }

        var list = await Admin.ListAsync();

        Assert.Equal(2, list.Count);
        var owner = list.Single(u => u.UserName == "owner");
        Assert.Equal(Roles.Admin, owner.Role);
        Assert.Equal(1, owner.Favorites);
        Assert.Equal(Roles.Member, list.Single(u => u.UserName == "member").Role);
    }

    // Creating --------------------------------------------------------------

    [Fact]
    public async Task An_admin_can_add_an_account_without_opening_registration()
    {
        await SeedOwnerAsync();
        Assert.False(await Accounts.RegistrationAllowedAsync());

        await AddMemberAsync("natalie");

        Assert.NotNull(await Users.FindByNameAsync("natalie"));
        // Adding someone directly must not have opened the door for everyone.
        Assert.False(await Accounts.RegistrationAllowedAsync());
    }

    [Fact]
    public async Task A_created_account_gets_the_role_it_was_given()
    {
        await SeedOwnerAsync();
        await Admin.CreateAsync("second", null, null, "a-long-passphrase", Roles.Member);

        var user = await Users.FindByNameAsync("second");
        Assert.Contains(Roles.Member, await Users.GetRolesAsync(user!));
    }

    [Fact]
    public async Task A_second_administrator_cannot_be_created()
    {
        // One administrator, because {collection} is a folder token whose value
        // is per-user: two of them would file the same library two different
        // ways on disk.
        await SeedOwnerAsync();

        var result = await Admin.CreateAsync("second", null, null, "a-long-passphrase", Roles.Admin);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "OneAdmin");
        Assert.Equal(1, await Admin.CountAdminsAsync());

        // Refused before the account is made, so there is no half-created row
        // to tidy up afterwards.
        Assert.Null(await Users.FindByNameAsync("second"));
    }

    // Roles -----------------------------------------------------------------

    [Fact]
    public async Task Promoting_a_member_hands_the_role_over_rather_than_adding_a_second()
    {
        var ownerId = await SeedOwnerAsync();
        var memberId = await AddMemberAsync();

        Assert.True((await Admin.SetRoleAsync(memberId, Roles.Admin)).Succeeded);

        Assert.Equal(1, await Admin.CountAdminsAsync());
        Assert.Contains(Roles.Admin,
            await Users.GetRolesAsync((await Users.FindByIdAsync(memberId))!));
        Assert.Contains(Roles.Member,
            await Users.GetRolesAsync((await Users.FindByIdAsync(ownerId))!));
    }

    /// <summary>
    /// The pair of guards must not close on each other.
    /// </summary>
    /// <remarks>
    /// Demoting the only administrator is refused, so if promoting a second
    /// were refused too there would be no sequence of single steps that moves
    /// the role at all -- the account holding it could never hand it on, and on
    /// a self-hosted box that is unrecoverable without editing SQLite by hand.
    /// </remarks>
    [Fact]
    public async Task The_role_can_always_be_handed_on()
    {
        var ownerId = await SeedOwnerAsync();
        var memberId = await AddMemberAsync();

        // The only move available, and it has to be enough.
        Assert.False((await Admin.SetRoleAsync(ownerId, Roles.Member)).Succeeded);
        Assert.True((await Admin.SetRoleAsync(memberId, Roles.Admin)).Succeeded);

        Assert.Equal(1, await Admin.CountAdminsAsync());
        Assert.Contains(Roles.Admin,
            await Users.GetRolesAsync((await Users.FindByIdAsync(memberId))!));
    }

    [Fact]
    public async Task There_is_never_a_moment_with_no_administrator()
    {
        var ownerId = await SeedOwnerAsync();
        var memberId = await AddMemberAsync();

        await Admin.SetRoleAsync(memberId, Roles.Admin);
        Assert.Equal(1, await Admin.CountAdminsAsync());

        await Admin.SetRoleAsync(ownerId, Roles.Admin);
        Assert.Equal(1, await Admin.CountAdminsAsync());
    }

    /// <summary>The lockout guard: demoting the only admin must be refused.</summary>
    [Fact]
    public async Task The_last_administrator_cannot_be_demoted()
    {
        var ownerId = await SeedOwnerAsync();
        await AddMemberAsync();

        var result = await Admin.SetRoleAsync(ownerId, Roles.Member);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "LastAdmin");
        Assert.Equal(1, await Admin.CountAdminsAsync());
    }

    [Fact]
    public async Task An_administrator_can_be_demoted_once_another_exists()
    {
        var ownerId = await SeedOwnerAsync();
        var memberId = await AddMemberAsync();
        await Admin.SetRoleAsync(memberId, Roles.Admin);

        Assert.True((await Admin.SetRoleAsync(ownerId, Roles.Member)).Succeeded);
        Assert.Equal(1, await Admin.CountAdminsAsync());
    }

    // Deleting --------------------------------------------------------------

    [Fact]
    public async Task You_cannot_delete_the_account_you_are_using()
    {
        var ownerId = await SeedOwnerAsync();

        var result = await Admin.DeleteAsync(ownerId, actingUserId: ownerId);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "Self");
    }

    [Fact]
    public async Task The_last_administrator_cannot_be_deleted()
    {
        var ownerId = await SeedOwnerAsync();
        var memberId = await AddMemberAsync();

        // Even by someone else, and even though the acting user differs.
        var result = await Admin.DeleteAsync(ownerId, actingUserId: memberId);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "LastAdmin");
        Assert.NotNull(await Users.FindByIdAsync(ownerId));
    }

    /// <summary>
    /// Favorites reference the owner by id, not a foreign key, so deleting an
    /// account must take them too or they linger unreachable. Collections must
    /// not go: they name folders on disk and belong to the library.
    /// </summary>
    [Fact]
    public async Task Deleting_an_account_removes_its_favorites_and_leaves_the_collections()
    {
        var ownerId = await SeedOwnerAsync();
        var memberId = await AddMemberAsync();

        await using (var db = NewDb())
        {
            db.Libraries.Add(new Library { Name = "L", Path = "/l" });
            db.Models.Add(new ModelEntry { LibraryId = 1, Name = "M", RelativePath = "m" });
            db.Collections.Add(new Collection { Name = "Theirs", NormalizedName = "theirs" });
            db.Collections.Add(new Collection { Name = "Mine", NormalizedName = "mine" });
            await db.SaveChangesAsync();
            db.Favorites.Add(new ModelFavorite { ModelEntryId = 1, UserId = memberId });
            await db.SaveChangesAsync();
        }

        Assert.True((await Admin.DeleteAsync(memberId, ownerId)).Succeeded);

        await using (var db = NewDb())
        {
            // Both collections survive. Removing a member used to take every
            // collection they had made, which now would re-file on disk
            // everything those collections named.
            Assert.Equal(2, await db.Collections.CountAsync());
            Assert.Equal(0, await db.Favorites.CountAsync());
            Assert.Equal(1, await db.Models.CountAsync());
        }
    }

    // Passwords and lockout -------------------------------------------------

    [Fact]
    public async Task An_admin_can_reset_a_password_without_knowing_the_old_one()
    {
        await SeedOwnerAsync();
        var memberId = await AddMemberAsync();

        Assert.True((await Admin.ResetPasswordAsync(memberId, "brand-new-passphrase")).Succeeded);

        var user = await Users.FindByIdAsync(memberId);
        Assert.True(await Users.CheckPasswordAsync(user!, "brand-new-passphrase"));
        Assert.False(await Users.CheckPasswordAsync(user!, "another-passphrase"));
    }

    [Fact]
    public async Task A_weak_reset_is_refused_and_the_old_password_still_works()
    {
        await SeedOwnerAsync();
        var memberId = await AddMemberAsync();

        Assert.False((await Admin.ResetPasswordAsync(memberId, "short")).Succeeded);

        var user = await Users.FindByIdAsync(memberId);
        Assert.True(await Users.CheckPasswordAsync(user!, "another-passphrase"));
    }

    [Fact]
    public async Task An_account_can_be_suspended_and_restored()
    {
        await SeedOwnerAsync();
        var memberId = await AddMemberAsync();

        Assert.True((await Admin.SetLockedOutAsync(memberId, true)).Succeeded);
        Assert.True((await Admin.ListAsync()).Single(u => u.Id == memberId).IsLockedOut);

        Assert.True((await Admin.SetLockedOutAsync(memberId, false)).Succeeded);
        Assert.False((await Admin.ListAsync()).Single(u => u.Id == memberId).IsLockedOut);
    }

    [Fact]
    public async Task The_last_administrator_cannot_be_suspended()
    {
        var ownerId = await SeedOwnerAsync();

        var result = await Admin.SetLockedOutAsync(ownerId, true);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "LastAdmin");
    }

    /// <summary>A reset should let someone back in, not leave them locked out.</summary>
    [Fact]
    public async Task Resetting_a_password_clears_a_lockout()
    {
        await SeedOwnerAsync();
        var memberId = await AddMemberAsync();
        await Admin.SetLockedOutAsync(memberId, true);

        await Admin.ResetPasswordAsync(memberId, "brand-new-passphrase");

        Assert.False((await Admin.ListAsync()).Single(u => u.Id == memberId).IsLockedOut);
    }

    public void Dispose()
    {
        _services.Dispose();
        _conn.Dispose();
    }
}
