using MeshVault.Core.Models;
using MeshVault.Core.Services;
using MeshVault.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Tests;

/// <summary>
/// Editing many models at once. The risk here is not the happy path but the
/// edges: a stale id in the selection, a tag half the models already carry, and
/// favorites belonging to one account leaking into another's.
/// </summary>
public class BulkEditTests : IDisposable
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

    public BulkEditTests()
    {
        _conn.Open();
        _factory = new Factory(_conn);

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Libraries.Add(new Library { Name = "L", Path = "/l" });
        db.SaveChanges();

        _editor = new ModelEditor(_factory, new FakeUser("alice"));
        _catalog = new ModelCatalog(_factory, new FakeUser("alice"));
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
            FileModifiedUtc = DateTimeOffset.UtcNow,
        };
        db.Models.Add(model);
        await db.SaveChangesAsync();
        return model.Id;
    }

    private async Task<ModelEntry> Load(int id)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Models.AsNoTracking()
            .Include(m => m.Tags)
            .Include(m => m.Designer)
            .Include(m => m.Collections)
            .SingleAsync(m => m.Id == id);
    }

    [Fact]
    public async Task A_designer_is_set_on_every_selected_model()
    {
        var a = await NewModel("a");
        var b = await NewModel("b");

        var result = await _editor.ApplyBulkEditAsync([a, b], new BulkEdit { DesignerName = "Prusa" });

        Assert.Equal(2, result.DesignerChanged);
        Assert.Equal("Prusa", (await Load(a)).Designer!.Name);
        Assert.Equal("Prusa", (await Load(b)).Designer!.Name);
    }

    [Fact]
    public async Task The_designer_is_created_once_and_shared()
    {
        var a = await NewModel("a");
        var b = await NewModel("b");

        await _editor.ApplyBulkEditAsync([a, b], new BulkEdit { DesignerName = "Prusa" });

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Designers.CountAsync());
    }

    [Fact]
    public async Task An_existing_designer_is_reused_whatever_the_casing()
    {
        var a = await NewModel("a");
        await _editor.CreateDesignerAsync("Prusa");

        await _editor.ApplyBulkEditAsync([a], new BulkEdit { DesignerName = "PRUSA" });

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Designers.CountAsync());
    }

    [Fact]
    public async Task Clearing_the_designer_beats_setting_one()
    {
        var a = await NewModel("a");
        await _editor.ApplyBulkEditAsync([a], new BulkEdit { DesignerName = "Prusa" });

        await _editor.ApplyBulkEditAsync([a],
            new BulkEdit { DesignerName = "Someone", ClearDesigner = true });

        Assert.Null((await Load(a)).Designer);
    }

    [Fact]
    public async Task Models_that_already_have_the_designer_are_not_counted_as_changed()
    {
        var a = await NewModel("a");
        var b = await NewModel("b");
        await _editor.ApplyBulkEditAsync([a], new BulkEdit { DesignerName = "Prusa" });

        var result = await _editor.ApplyBulkEditAsync([a, b], new BulkEdit { DesignerName = "Prusa" });

        Assert.Equal(1, result.DesignerChanged);
    }

    [Fact]
    public async Task Tags_are_added_to_every_selected_model()
    {
        var a = await NewModel("a");
        var b = await NewModel("b");

        var result = await _editor.ApplyBulkEditAsync([a, b],
            new BulkEdit { TagsToAdd = ["dragon", "resin"] });

        Assert.Equal(4, result.TagsAdded);
        Assert.Equal(2, (await Load(a)).Tags.Count);
        Assert.Equal(2, (await Load(b)).Tags.Count);
    }

    [Fact]
    public async Task A_tag_a_model_already_carries_is_not_added_twice()
    {
        var a = await NewModel("a");
        var b = await NewModel("b");
        await _editor.AddTagAsync(a, "dragon");

        var result = await _editor.ApplyBulkEditAsync([a, b], new BulkEdit { TagsToAdd = ["dragon"] });

        Assert.Equal(1, result.TagsAdded);
        Assert.Single((await Load(a)).Tags);
    }

    [Fact]
    public async Task Removing_a_tag_that_then_labels_nothing_deletes_it()
    {
        // Otherwise it lingers in the filter sidebar with a count of zero.
        var a = await NewModel("a");
        await _editor.ApplyBulkEditAsync([a], new BulkEdit { TagsToAdd = ["wip"] });

        await _editor.ApplyBulkEditAsync([a], new BulkEdit { TagsToRemove = ["wip"] });

        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.Tags.CountAsync());
    }

    [Fact]
    public async Task A_tag_still_used_elsewhere_survives_removal()
    {
        var a = await NewModel("a");
        var b = await NewModel("b");
        await _editor.ApplyBulkEditAsync([a, b], new BulkEdit { TagsToAdd = ["wip"] });

        await _editor.ApplyBulkEditAsync([a], new BulkEdit { TagsToRemove = ["wip"] });

        Assert.Empty((await Load(a)).Tags);
        Assert.Single((await Load(b)).Tags);
    }

    [Fact]
    public async Task Tags_can_be_added_and_removed_in_one_edit()
    {
        var a = await NewModel("a");
        await _editor.ApplyBulkEditAsync([a], new BulkEdit { TagsToAdd = ["unsorted"] });

        await _editor.ApplyBulkEditAsync([a],
            new BulkEdit { TagsToAdd = ["dragon"], TagsToRemove = ["unsorted"] });

        Assert.Equal("dragon", Assert.Single((await Load(a)).Tags).Name);
    }

    [Fact]
    public async Task Models_are_added_to_a_collection()
    {
        var a = await NewModel("a");
        var b = await NewModel("b");
        var collection = await _editor.CreateCollectionAsync("Printed");

        var result = await _editor.ApplyBulkEditAsync([a, b],
            new BulkEdit { CollectionId = collection.Id });

        Assert.Equal(2, result.CollectionChanged);
        Assert.Single((await Load(a)).Collections);
    }

    [Fact]
    public async Task Adding_to_a_collection_twice_changes_nothing_the_second_time()
    {
        var a = await NewModel("a");
        var collection = await _editor.CreateCollectionAsync("Printed");
        await _editor.ApplyBulkEditAsync([a], new BulkEdit { CollectionId = collection.Id });

        var result = await _editor.ApplyBulkEditAsync([a], new BulkEdit { CollectionId = collection.Id });

        Assert.Equal(0, result.CollectionChanged);
        Assert.Single((await Load(a)).Collections);
    }

    [Fact]
    public async Task Models_can_be_removed_from_a_collection()
    {
        var a = await NewModel("a");
        var collection = await _editor.CreateCollectionAsync("Printed");
        await _editor.ApplyBulkEditAsync([a], new BulkEdit { CollectionId = collection.Id });

        var result = await _editor.ApplyBulkEditAsync([a],
            new BulkEdit { CollectionId = collection.Id, RemoveFromCollection = true });

        Assert.Equal(1, result.CollectionChanged);
        Assert.Empty((await Load(a)).Collections);
    }

    [Fact]
    public async Task A_collection_made_by_somebody_else_can_still_be_filled()
    {
        // Collections belong to the library rather than to an account, so there
        // is no "somebody else's collection" to refuse. This used to silently
        // change nothing, which on a shared library read as a broken button.
        var a = await NewModel("a");
        var bob = new ModelEditor(_factory, new FakeUser("bob"));
        var terrain = await bob.CreateCollectionAsync("Terrain");

        var result = await _editor.ApplyBulkEditAsync([a], new BulkEdit { CollectionId = terrain.Id });

        Assert.Equal(1, result.CollectionChanged);
        Assert.Equal("Terrain", Assert.Single((await Load(a)).Collections).Name);
    }

    [Fact]
    public async Task Favorites_belong_to_the_account_that_set_them()
    {
        var a = await NewModel("a");
        var bob = new ModelEditor(_factory, new FakeUser("bob"));

        await _editor.ApplyBulkEditAsync([a], new BulkEdit { Favorite = true });

        await using var db = _factory.CreateDbContext();
        Assert.True(await db.Favorites.AnyAsync(f => f.UserId == "alice"));
        Assert.False(await db.Favorites.AnyAsync(f => f.UserId == "bob"));

        // And Bob unfavoriting does not take Alice's away.
        await bob.ApplyBulkEditAsync([a], new BulkEdit { Favorite = false });
        Assert.True(await db.Favorites.AnyAsync(f => f.UserId == "alice"));
    }

    [Fact]
    public async Task Favoriting_twice_does_not_duplicate()
    {
        var a = await NewModel("a");

        await _editor.ApplyBulkEditAsync([a], new BulkEdit { Favorite = true });
        var result = await _editor.ApplyBulkEditAsync([a], new BulkEdit { Favorite = true });

        Assert.Equal(0, result.FavoritesChanged);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Favorites.CountAsync());
    }

    [Fact]
    public async Task An_id_that_no_longer_exists_does_not_fail_the_edit()
    {
        // A selection gathered before a rescan is expected to go stale.
        var a = await NewModel("a");

        var result = await _editor.ApplyBulkEditAsync([a, 9999],
            new BulkEdit { DesignerName = "Prusa", TagsToAdd = ["dragon"], Favorite = true });

        Assert.Equal(1, result.DesignerChanged);
        Assert.Equal(1, result.TagsAdded);
        Assert.Equal(1, result.FavoritesChanged);
    }

    [Fact]
    public async Task A_duplicated_id_is_only_applied_once()
    {
        var a = await NewModel("a");

        var result = await _editor.ApplyBulkEditAsync([a, a, a], new BulkEdit { TagsToAdd = ["dragon"] });

        Assert.Equal(1, result.TagsAdded);
        Assert.Single((await Load(a)).Tags);
    }

    [Fact]
    public async Task An_empty_edit_touches_nothing()
    {
        var a = await NewModel("a");

        var result = await _editor.ApplyBulkEditAsync([a], new BulkEdit());

        Assert.True(result.ChangedNothing);
        Assert.Equal(0, result.Models);
    }

    [Fact]
    public async Task An_empty_selection_touches_nothing()
    {
        var result = await _editor.ApplyBulkEditAsync([], new BulkEdit { DesignerName = "Prusa" });

        Assert.Equal(0, result.Models);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.Designers.CountAsync());
    }

    [Fact]
    public async Task Selecting_everything_that_matches_respects_the_filters()
    {
        // "Select all matching" has to agree with what the page was showing, or
        // an edit lands on models the user never saw.
        var a = await NewModel("dragon one");
        await NewModel("boat");
        var c = await NewModel("dragon two");

        var ids = await _catalog.GetMatchingIdsAsync(new ModelQuery { Search = "dragon" });

        Assert.Equal([a, c], ids);
    }

    [Fact]
    public async Task Selecting_everything_ignores_paging()
    {
        for (var i = 0; i < 30; i++) await NewModel($"model {i:00}");

        var ids = await _catalog.GetMatchingIdsAsync(new ModelQuery { PageSize = 5, Page = 1 });

        Assert.Equal(30, ids.Count);
    }

    public void Dispose() => _conn.Dispose();
}
