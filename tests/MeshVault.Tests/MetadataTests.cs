using MeshVault.Core.Models;
using MeshVault.Core.Services;
using MeshVault.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Tests;

public class MetadataTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly IDbContextFactory<MeshVaultDbContext> _factory;
    private readonly ModelEditor _editor;
    private readonly ModelCatalog _catalog;

    private sealed class FakeUser(string id) : ICurrentUser
    {
        public string UserId { get; } = id;
    }

    private sealed class Factory(SqliteConnection conn) : IDbContextFactory<MeshVaultDbContext>
    {
        public MeshVaultDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<MeshVaultDbContext>().UseSqlite(conn).Options);
    }

    public MetadataTests()
    {
        _conn.Open();
        _factory = new Factory(_conn);

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Libraries.Add(new Library { Name = "L", Path = "/l" });
        db.SaveChanges();

        _editor = new ModelEditor(_factory, new FakeUser(Users.LocalUserId));
        _catalog = new ModelCatalog(_factory, new FakeUser(Users.LocalUserId));
    }

    private async Task<int> NewModel(string name)
    {
        await using var db = _factory.CreateDbContext();
        var model = new ModelEntry
        {
            LibraryId = 1,
            Name = name,
            RelativePath = name,
            AddedUtc = DateTimeOffset.UtcNow,
        };
        db.Models.Add(model);
        await db.SaveChangesAsync();
        return model.Id;
    }

    // Designers -------------------------------------------------------------

    [Fact]
    public async Task Setting_a_designer_creates_them_once_and_reuses_them()
    {
        var a = await NewModel("A");
        var b = await NewModel("B");

        var first = await _editor.SetDesignerAsync(a, "Loubie");
        var second = await _editor.SetDesignerAsync(b, "  loubie  ");

        Assert.NotNull(first);
        Assert.Equal(first!.Id, second!.Id);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Designers.CountAsync());
        Assert.Equal("Loubie", (await db.Designers.SingleAsync()).Name);
    }

    [Fact]
    public async Task Clearing_a_designer_leaves_the_designer_record_intact()
    {
        var id = await NewModel("A");
        await _editor.SetDesignerAsync(id, "Loubie");

        await _editor.SetDesignerAsync(id, null);

        await using var db = _factory.CreateDbContext();
        Assert.Null((await db.Models.SingleAsync()).DesignerId);
        Assert.Equal(1, await db.Designers.CountAsync());
    }

    [Fact]
    public async Task Deleting_a_designer_keeps_their_models()
    {
        var id = await NewModel("A");
        var designer = await _editor.SetDesignerAsync(id, "Loubie");

        await _editor.DeleteDesignerAsync(designer!.Id);

        await using var db = _factory.CreateDbContext();
        var model = await db.Models.SingleAsync();
        Assert.Equal("A", model.Name);
        Assert.Null(model.DesignerId);
    }

    // Source URLs -----------------------------------------------------------

    [Theory]
    [InlineData("https://makerworld.com/models/123", "MakerWorld")]
    [InlineData("makerworld.com/models/123", "MakerWorld")]
    [InlineData("https://www.printables.com/model/456", "Printables")]
    [InlineData("https://thingiverse.com/thing:1", "Thingiverse")]
    [InlineData("https://example.com/thing", "Other")]
    public void Source_site_is_detected_from_the_url(string url, string expected)
    {
        Assert.Equal(expected, SourceSites.Detect(url));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://files.example.com/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public void Unusable_source_urls_are_rejected(string url)
    {
        Assert.Null(SourceSites.Normalize(url));
    }

    [Fact]
    public async Task Saving_a_source_url_records_the_site()
    {
        var id = await NewModel("A");

        Assert.True(await _editor.SetSourceUrlAsync(id, "makerworld.com/models/9"));

        await using var db = _factory.CreateDbContext();
        var model = await db.Models.SingleAsync();
        Assert.Equal("https://makerworld.com/models/9", model.SourceUrl);
        Assert.Equal("MakerWorld", model.SourceSite);
    }

    [Fact]
    public async Task A_bad_source_url_is_refused_and_changes_nothing()
    {
        var id = await NewModel("A");
        await _editor.SetSourceUrlAsync(id, "https://printables.com/model/1");

        Assert.False(await _editor.SetSourceUrlAsync(id, "nonsense"));

        await using var db = _factory.CreateDbContext();
        Assert.Equal("Printables", (await db.Models.SingleAsync()).SourceSite);
    }

    [Fact]
    public async Task Clearing_a_source_url_clears_the_site_too()
    {
        var id = await NewModel("A");
        await _editor.SetSourceUrlAsync(id, "makerworld.com/models/9");

        Assert.True(await _editor.SetSourceUrlAsync(id, null));

        await using var db = _factory.CreateDbContext();
        var model = await db.Models.SingleAsync();
        Assert.Null(model.SourceUrl);
        Assert.Null(model.SourceSite);
    }

    // Favorites are per user ------------------------------------------------

    [Fact]
    public async Task Favorites_are_not_shared_between_users()
    {
        var id = await NewModel("A");
        var mine = new ModelEditor(_factory, new FakeUser("alice"));
        var theirs = new ModelCatalog(_factory, new FakeUser("bob"));
        var aliceView = new ModelCatalog(_factory, new FakeUser("alice"));

        await mine.ToggleFavoriteAsync(id);

        Assert.True((await aliceView.GetAsync(id))!.IsFavorite);
        Assert.False((await theirs.GetAsync(id))!.IsFavorite);
    }

    [Fact]
    public async Task Toggling_a_favorite_twice_removes_it()
    {
        var id = await NewModel("A");

        Assert.True(await _editor.ToggleFavoriteAsync(id));
        Assert.False(await _editor.ToggleFavoriteAsync(id));

        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.Favorites.CountAsync());
    }

    [Fact]
    public async Task Favorites_filter_only_returns_the_current_users_stars()
    {
        var a = await NewModel("A");
        await NewModel("B");
        await new ModelEditor(_factory, new FakeUser("alice")).ToggleFavoriteAsync(a);

        var alice = new ModelCatalog(_factory, new FakeUser("alice"));
        var bob = new ModelCatalog(_factory, new FakeUser("bob"));

        Assert.Equal(1, (await alice.SearchAsync(new ModelQuery { FavoritesOnly = true })).TotalCount);
        Assert.Equal(0, (await bob.SearchAsync(new ModelQuery { FavoritesOnly = true })).TotalCount);
    }

    // Collections are per user ----------------------------------------------

    [Fact]
    public async Task Two_users_may_each_have_a_collection_with_the_same_name()
    {
        var alice = new ModelEditor(_factory, new FakeUser("alice"));
        var bob = new ModelEditor(_factory, new FakeUser("bob"));

        await alice.CreateCollectionAsync("To Print");
        await bob.CreateCollectionAsync("To Print");

        await using var db = _factory.CreateDbContext();
        Assert.Equal(2, await db.Collections.CountAsync());
    }

    [Fact]
    public async Task One_user_may_not_reuse_their_own_collection_name()
    {
        await _editor.CreateCollectionAsync("To Print");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _editor.CreateCollectionAsync("to print "));
    }

    [Fact]
    public async Task A_user_only_sees_their_own_collections()
    {
        await new ModelEditor(_factory, new FakeUser("alice")).CreateCollectionAsync("Alice list");
        await new ModelEditor(_factory, new FakeUser("bob")).CreateCollectionAsync("Bob list");

        var visible = await new ModelCatalog(_factory, new FakeUser("alice")).GetCollectionsAsync();

        Assert.Equal("Alice list", Assert.Single(visible).Collection.Name);
    }

    [Fact]
    public async Task A_user_cannot_add_models_to_someone_elses_collection()
    {
        var id = await NewModel("A");
        var bobsCollection = await new ModelEditor(_factory, new FakeUser("bob"))
            .CreateCollectionAsync("Bob list");

        await new ModelEditor(_factory, new FakeUser("alice"))
            .SetCollectionMembershipAsync(id, bobsCollection.Id, true);

        await using var db = _factory.CreateDbContext();
        var collection = await db.Collections.Include(c => c.Models).SingleAsync();
        Assert.Empty(collection.Models);
    }

    [Fact]
    public async Task Collection_membership_can_be_added_and_removed()
    {
        var id = await NewModel("A");
        var collection = await _editor.CreateCollectionAsync("To Print");

        await _editor.SetCollectionMembershipAsync(id, collection.Id, true);
        Assert.Equal(1, (await _catalog.SearchAsync(
            new ModelQuery { CollectionId = collection.Id })).TotalCount);

        await _editor.SetCollectionMembershipAsync(id, collection.Id, false);
        Assert.Equal(0, (await _catalog.SearchAsync(
            new ModelQuery { CollectionId = collection.Id })).TotalCount);
    }

    [Fact]
    public async Task Deleting_a_collection_keeps_its_models()
    {
        var id = await NewModel("A");
        var collection = await _editor.CreateCollectionAsync("To Print");
        await _editor.SetCollectionMembershipAsync(id, collection.Id, true);

        await _editor.DeleteCollectionAsync(collection.Id);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Models.CountAsync());
        Assert.Equal(0, await db.Collections.CountAsync());
    }

    // Filtering -------------------------------------------------------------

    [Fact]
    public async Task Models_can_be_filtered_by_designer_and_site()
    {
        var a = await NewModel("A");
        var b = await NewModel("B");
        var designer = await _editor.SetDesignerAsync(a, "Loubie");
        await _editor.SetSourceUrlAsync(a, "makerworld.com/models/1");
        await _editor.SetSourceUrlAsync(b, "printables.com/model/2");

        Assert.Equal(1, (await _catalog.SearchAsync(
            new ModelQuery { DesignerId = designer!.Id })).TotalCount);
        Assert.Equal(1, (await _catalog.SearchAsync(
            new ModelQuery { SourceSite = "Printables" })).TotalCount);
        Assert.Equal(1, (await _catalog.SearchAsync(
            new ModelQuery { MissingDesigner = true })).TotalCount);
        Assert.Equal(0, (await _catalog.SearchAsync(
            new ModelQuery { MissingSource = true })).TotalCount);
    }

    [Fact]
    public async Task Search_matches_the_designer_name()
    {
        var a = await NewModel("Nondescript");
        await _editor.SetDesignerAsync(a, "Loubie");
        await NewModel("Other");

        var result = await _catalog.SearchAsync(new ModelQuery { Search = "loub" });

        Assert.Equal("Nondescript", Assert.Single(result.Items).Model.Name);
    }

    public void Dispose() => _conn.Dispose();
}
