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
/// These encode security decisions: who may create an account, who becomes an
/// administrator, and what happens to data that predates sign-in.
/// </summary>
public class AccountSetupTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly ServiceProvider _services;

    public AccountSetupTests()
    {
        _conn.Open();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
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
            .AddEntityFrameworkStores<MeshVaultDbContext>();

        services.AddScoped<SettingsStore>();
        services.AddScoped<AccountSetup>();

        _services = services.BuildServiceProvider();

        using var db = _services.GetRequiredService<IDbContextFactory<MeshVaultDbContext>>().CreateDbContext();
        db.Database.EnsureCreated();
    }

    private AccountSetup Accounts => _services.GetRequiredService<AccountSetup>();
    private UserManager<ApplicationUser> Users => _services.GetRequiredService<UserManager<ApplicationUser>>();

    private MeshVaultDbContext NewDb() =>
        _services.GetRequiredService<IDbContextFactory<MeshVaultDbContext>>().CreateDbContext();

    [Fact]
    public async Task A_fresh_instance_is_unclaimed_and_open()
    {
        Assert.True(await Accounts.IsUnclaimedAsync());
        Assert.True(await Accounts.RegistrationAllowedAsync());
    }

    [Fact]
    public async Task The_first_account_becomes_an_administrator()
    {
        var result = await Accounts.RegisterAsync("mark", null, "correcthorse", "Mark");
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

        var user = await Users.FindByNameAsync("mark");
        Assert.NotNull(user);
        Assert.Contains(Roles.Admin, await Users.GetRolesAsync(user!));
    }

    /// <summary>
    /// The key security rule: an unclaimed instance is open so the owner can
    /// claim it, and must close immediately afterwards.
    /// </summary>
    [Fact]
    public async Task Registration_closes_once_the_instance_is_claimed()
    {
        await Accounts.RegisterAsync("mark", null, "correcthorse", null);

        Assert.False(await Accounts.IsUnclaimedAsync());
        Assert.False(await Accounts.RegistrationAllowedAsync());
    }

    [Fact]
    public async Task A_second_account_is_refused_while_registration_is_closed()
    {
        await Accounts.RegisterAsync("mark", null, "correcthorse", null);

        var result = await Accounts.RegisterAsync("intruder", null, "letmein12345", null);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "RegistrationClosed");
        Assert.Null(await Users.FindByNameAsync("intruder"));
    }

    [Fact]
    public async Task An_admin_can_open_registration_and_later_accounts_are_members()
    {
        await Accounts.RegisterAsync("mark", null, "correcthorse", null);
        await Accounts.SetRegistrationOpenAsync(true);

        var result = await Accounts.RegisterAsync("natalie", null, "anotherpass1", "Natalie");
        Assert.True(result.Succeeded);

        var user = await Users.FindByNameAsync("natalie");
        var roles = await Users.GetRolesAsync(user!);
        Assert.Contains(Roles.Member, roles);
        // Only the first account gets administrator rights.
        Assert.DoesNotContain(Roles.Admin, roles);
    }

    /// <summary>
    /// Favorites created before accounts existed carry the stand-in owner id,
    /// and must follow the owner into their real account. Collections have no
    /// owner to follow: they belong to the library.
    /// </summary>
    [Fact]
    public async Task The_first_account_adopts_data_created_before_sign_in_existed()
    {
        await using (var db = NewDb())
        {
            db.Libraries.Add(new Library { Name = "L", Path = "/l" });
            db.Models.Add(new ModelEntry { LibraryId = 1, Name = "M", RelativePath = "m" });
            db.Collections.Add(new Collection { Name = "To Print", NormalizedName = "to print" });
            await db.SaveChangesAsync();

            db.Favorites.Add(new ModelFavorite { ModelEntryId = 1, UserId = Users_LocalId });
            await db.SaveChangesAsync();
        }

        await Accounts.RegisterAsync("mark", null, "correcthorse", null);
        var user = await Users.FindByNameAsync("mark");

        await using (var db = NewDb())
        {
            Assert.Equal(user!.Id, (await db.Favorites.SingleAsync()).UserId);

            // Untouched rather than transferred, and still there. Adoption used
            // to rewrite an owner column that no longer exists.
            Assert.Equal("To Print", (await db.Collections.SingleAsync()).Name);
        }
    }

    [Fact]
    public async Task A_later_account_does_not_adopt_anything()
    {
        await using (var db = NewDb())
        {
            db.Libraries.Add(new Library { Name = "L", Path = "/l" });
            db.Models.Add(new ModelEntry { LibraryId = 1, Name = "M", RelativePath = "m" });
            await db.SaveChangesAsync();
        }

        await Accounts.RegisterAsync("mark", null, "correcthorse", null);
        var mark = await Users.FindByNameAsync("mark");

        await using (var db = NewDb())
        {
            db.Favorites.Add(new ModelFavorite { ModelEntryId = 1, UserId = mark!.Id });
            await db.SaveChangesAsync();
        }

        await Accounts.SetRegistrationOpenAsync(true);
        await Accounts.RegisterAsync("natalie", null, "anotherpass1", null);

        await using (var db = NewDb())
        {
            // Mark's favorite stays Mark's.
            Assert.Equal(mark!.Id, (await db.Favorites.SingleAsync()).UserId);
        }
    }

    [Fact]
    public async Task A_short_password_is_refused()
    {
        var result = await Accounts.RegisterAsync("mark", null, "short", null);

        Assert.False(result.Succeeded);
        Assert.True(await Accounts.IsUnclaimedAsync());
    }

    [Fact]
    public async Task A_duplicate_user_name_is_refused()
    {
        await Accounts.RegisterAsync("mark", null, "correcthorse", null);
        await Accounts.SetRegistrationOpenAsync(true);

        var result = await Accounts.RegisterAsync("mark", null, "correcthorse", null);

        Assert.False(result.Succeeded);
        Assert.Equal(1, NewDb().Users.Count());
    }

    private const string Users_LocalId = "local";

    public void Dispose()
    {
        _services.Dispose();
        _conn.Dispose();
    }
}
