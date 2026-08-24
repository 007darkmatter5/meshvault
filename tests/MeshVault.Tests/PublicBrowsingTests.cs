using System.Security.Claims;
using MeshVault.Core.Models;
using MeshVault.Core.Services;
using MeshVault.Data;
using MeshVault.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Tests;

/// <summary>
/// Letting a signed-out visitor read the catalog. The dangerous half is not
/// what they may see but who the server thinks they are, so both are pinned.
/// </summary>
public class PublicBrowsingTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly IDbContextFactory<MeshVaultDbContext> _factory;
    private readonly SettingsStore _settings;

    private sealed class Factory(SqliteConnection conn) : IDbContextFactory<MeshVaultDbContext>
    {
        public MeshVaultDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<MeshVaultDbContext>().UseSqlite(conn).Options);
    }

    private sealed class FixedContext(HttpContext? context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    public PublicBrowsingTests()
    {
        _conn.Open();
        _factory = new Factory(_conn);

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        _settings = new SettingsStore(_factory);
    }

    private static HttpContext SignedIn(string userId)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test");
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    private static HttpContext SignedOut() =>
        new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };

    private async Task<bool> Allowed(HttpContext context)
    {
        var handler = new ViewHandler(_settings);
        var requirement = new ViewRequirement();
        var authContext = new AuthorizationHandlerContext([requirement], context.User, null);

        await handler.HandleAsync(authContext);
        return authContext.HasSucceeded;
    }

    // Who the server thinks a visitor is -------------------------------------

    [Fact]
    public void A_signed_out_visitor_is_never_the_legacy_local_user()
    {
        // The dangerous default. Handing every visitor the legacy id would show
        // them one account's collections and favorites, and make all of them a
        // single shared identity.
        var user = new SignedInUser(new FixedContext(SignedOut()));

        Assert.NotEqual(Users.LocalUserId, user.UserId);
        Assert.Equal(Users.AnonymousId, user.UserId);
        Assert.False(user.IsAuthenticated);
    }

    [Fact]
    public void A_signed_in_visitor_is_their_own_account()
    {
        var user = new SignedInUser(new FixedContext(SignedIn("alice")));

        Assert.Equal("alice", user.UserId);
        Assert.True(user.IsAuthenticated);
    }

    [Fact]
    public void A_request_with_no_context_at_all_is_anonymous()
    {
        var user = new SignedInUser(new FixedContext(null));

        Assert.Equal(Users.AnonymousId, user.UserId);
        Assert.False(user.IsAuthenticated);
    }

    [Fact]
    public async Task An_anonymous_visitor_owns_nothing()
    {
        // Not merely hidden: the per-user queries must find nothing for them,
        // whatever anyone else has created.
        var alice = new ModelEditor(_factory, new SignedInUser(new FixedContext(SignedIn("alice"))));
        await alice.CreateCollectionAsync("Alice's list");

        var anonymous = new ModelCatalog(_factory, new SignedInUser(new FixedContext(SignedOut())));

        Assert.Empty(await anonymous.GetCollectionsAsync());
    }

    [Fact]
    public async Task An_anonymous_visitor_does_not_inherit_legacy_local_data()
    {
        // Rows owned by the pre-accounts stand-in must not resurface just
        // because somebody browsed without signing in.
        var local = new ModelEditor(_factory, new LocalUser());
        await local.CreateCollectionAsync("Left over from before accounts");

        var anonymous = new ModelCatalog(_factory, new SignedInUser(new FixedContext(SignedOut())));

        Assert.Empty(await anonymous.GetCollectionsAsync());
    }

    // Whether they get in at all ---------------------------------------------

    [Fact]
    public async Task A_signed_out_visitor_is_refused_by_default()
    {
        Assert.False(await Allowed(SignedOut()));
    }

    [Fact]
    public async Task A_signed_out_visitor_is_allowed_once_it_is_turned_on()
    {
        await _settings.SetBoolAsync(SettingKeys.PublicBrowsing, true);

        Assert.True(await Allowed(SignedOut()));
    }

    [Fact]
    public async Task Turning_it_off_shuts_the_door_at_once()
    {
        // Read per request rather than cached, so revoking access does not wait
        // for a restart.
        await _settings.SetBoolAsync(SettingKeys.PublicBrowsing, true);
        Assert.True(await Allowed(SignedOut()));

        await _settings.SetBoolAsync(SettingKeys.PublicBrowsing, false);
        Assert.False(await Allowed(SignedOut()));
    }

    [Fact]
    public async Task A_signed_in_visitor_gets_in_whatever_the_setting_says()
    {
        Assert.True(await Allowed(SignedIn("alice")));

        await _settings.SetBoolAsync(SettingKeys.PublicBrowsing, true);
        Assert.True(await Allowed(SignedIn("alice")));
    }

    public void Dispose() => _conn.Dispose();
}
