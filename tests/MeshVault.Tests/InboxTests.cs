using MeshVault.Core.Models;
using MeshVault.Core.Services;
using MeshVault.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Tests;

/// <summary>
/// The inbox: a folder inside a library where downloads land until they have
/// been described well enough to file.
/// </summary>
public class InboxTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly IDbContextFactory<MeshVaultDbContext> _factory;
    private readonly OrganizePlanner _planner;

    private sealed class FakeUser : ICurrentUser
    {
        public string UserId => "alice";
    }

    private sealed class Factory(SqliteConnection conn) : IDbContextFactory<MeshVaultDbContext>
    {
        public MeshVaultDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<MeshVaultDbContext>().UseSqlite(conn).Options);
    }

    public InboxTests()
    {
        _conn.Open();
        _factory = new Factory(_conn);

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Libraries.Add(new Library { Name = "L", Path = "/l", InboxPath = "inbox" });
        db.Designers.Add(new Designer { Name = "Dungeon Blocks", NormalizedName = "dungeon blocks" });
        db.SaveChanges();

        _planner = new OrganizePlanner(_factory, new FakeUser(), new VariantRules());
    }

    private async Task<int> NewModel(string relativePath, int? designerId = null, string? tag = null)
    {
        await using var db = _factory.CreateDbContext();
        var model = new ModelEntry
        {
            LibraryId = 1,
            Name = relativePath.Split('/')[^1],
            RelativePath = relativePath,
            DesignerId = designerId,
            AddedUtc = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            Files =
            [
                new ModelFile
                {
                    RelativePath = $"{relativePath}/mesh.stl",
                    FileName = "mesh.stl",
                    Extension = ".stl",
                    Kind = FileKind.Mesh,
                    SculptKey = "wall",
                    SculptName = "Wall",
                },
            ],
        };

        if (tag is not null)
            model.Tags.Add(new Tag { Name = tag, NormalizedName = tag.ToLowerInvariant() });

        db.Models.Add(model);
        await db.SaveChangesAsync();
        return model.Id;
    }

    private Task<OrganizePlan> Plan(string template = "{designer}/{sculpt}") =>
        _planner.PlanAsync(1, new OrganizeRules { FolderTemplate = template });

    [Theory]
    [InlineData("inbox", true)]
    [InlineData("inbox/UD-Supported", true)]
    [InlineData("inbox/a/b/c", true)]
    [InlineData("INBOX/Loud", true)]
    [InlineData("inboxes/not-really", false)]
    [InlineData("dnd/terrain/wall", false)]
    public void Knows_what_is_in_the_inbox(string path, bool expected) =>
        Assert.Equal(expected, Inbox.Holds("inbox", path));

    [Fact]
    public void A_library_with_no_inbox_holds_nothing()
    {
        Assert.False(Inbox.Holds(null, "inbox/thing"));
        Assert.False(Inbox.Holds("", "inbox/thing"));
    }

    [Theory]
    [InlineData("/Inbox/", "Inbox")]
    [InlineData("inbox\\", "inbox")]
    [InlineData("  inbox  ", "inbox")]
    public void A_typed_inbox_path_is_stored_the_way_paths_are_compared(string typed, string stored) =>
        Assert.Equal(stored, Inbox.Normalize(typed));

    [Fact]
    public async Task An_unfiled_model_missing_a_designer_is_not_moved()
    {
        // Filing it now would put it under Unsorted/ and cost a second pass.
        await NewModel("inbox/UD-Supported");

        var plan = await Plan();

        Assert.Equal(0, plan.Moving);
        Assert.Equal(1, plan.Incomplete);
        Assert.Contains("a designer", plan.Moves[0].Problem);
    }

    [Fact]
    public async Task An_unfiled_model_with_what_it_needs_is_filed()
    {
        await NewModel("inbox/Goblin", designerId: 1);

        var plan = await Plan();

        Assert.Equal(0, plan.Incomplete);
        Assert.Equal("Dungeon Blocks/Wall", Assert.Single(plan.Moves).To);
    }

    [Fact]
    public async Task Only_what_the_template_asks_for_is_required()
    {
        // Filing by tag should not nag about a designer nobody sorts by.
        await NewModel("inbox/Goblin", tag: "terrain");

        var plan = await Plan("{tag}/{sculpt}");

        Assert.Equal(0, plan.Incomplete);
        Assert.Equal("terrain/Wall", Assert.Single(plan.Moves).To);
    }

    [Fact]
    public async Task A_model_outside_the_inbox_is_never_blocked()
    {
        // Everything already filed predates the inbox and is left to the
        // template's fallbacks, exactly as before.
        await NewModel("dnd/terrain/wall");

        var plan = await Plan();

        Assert.Equal(0, plan.Incomplete);
        Assert.Equal("Unsorted/Wall", Assert.Single(plan.Moves).To);
    }

    [Fact]
    public async Task Missing_reads_as_a_sentence()
    {
        await NewModel("inbox/Nameless");

        var plan = await Plan("{designer}/{tag}/{sculpt}");

        Assert.Equal("Still in the inbox and needs a designer or a tag.", plan.Moves[0].Problem);
    }

    [Fact]
    public async Task Browse_can_show_what_is_in_no_collection()
    {
        // Invisible without this: a model with no collection files under a
        // placeholder and looks like any other row until the folder appears.
        await NewModel("dnd/filed", designerId: 1);
        await NewModel("dnd/loose", designerId: 1);

        await using (var db = _factory.CreateDbContext())
        {
            var model = await db.Models.Include(m => m.Collections)
                .FirstAsync(m => m.RelativePath == "dnd/filed");
            model.Collections.Add(new Collection
            {
                Name = "Terrain", NormalizedName = "terrain", OwnerId = "alice",
            });
            await db.SaveChangesAsync();
        }

        var catalog = new ModelCatalog(_factory, new FakeUser());
        var loose = await catalog.SearchAsync(new ModelQuery { MissingCollection = true });

        Assert.Equal("loose", Assert.Single(loose.Items).Model.Name);
    }

    [Fact]
    public async Task Browse_can_show_only_what_is_unfiled()
    {
        await NewModel("inbox/New Thing", designerId: 1);
        await NewModel("dnd/terrain/filed", designerId: 1);

        var catalog = new ModelCatalog(_factory, new FakeUser());

        Assert.Equal(2, (await catalog.SearchAsync(new ModelQuery())).TotalCount);

        var unfiled = await catalog.SearchAsync(new ModelQuery { UnfiledOnly = true });
        Assert.Equal("New Thing", Assert.Single(unfiled.Items).Model.Name);
    }

    [Fact]
    public async Task A_library_with_no_inbox_shows_nothing_as_unfiled()
    {
        await using (var db = _factory.CreateDbContext())
        {
            var library = await db.Libraries.FirstAsync();
            library.InboxPath = null;
            await db.SaveChangesAsync();
        }

        await NewModel("inbox/New Thing", designerId: 1);

        var catalog = new ModelCatalog(_factory, new FakeUser());
        Assert.Empty((await catalog.SearchAsync(new ModelQuery { UnfiledOnly = true })).Items);
    }

    [Fact]
    public async Task Setting_the_inbox_normalises_what_was_typed()
    {
        var editor = new ModelEditor(_factory, new FakeUser());
        await editor.UpdateLibraryAsync(1, "L", allowOrganize: true, inboxPath: "/Inbox/");

        await using var db = _factory.CreateDbContext();

        // Slashes and spaces go; the case the user typed stays, because the
        // folder on disk may genuinely be spelled that way.
        Assert.Equal("Inbox", (await db.Libraries.FirstAsync()).InboxPath);

        // Blank means the library has no inbox, not a folder called "".
        await editor.UpdateLibraryAsync(1, "L", allowOrganize: true, inboxPath: "   ");

        await using var check = _factory.CreateDbContext();
        Assert.Null((await check.Libraries.FirstAsync()).InboxPath);
    }

    public void Dispose()
    {
        _conn.Dispose();
    }
}
