using MeshVault.Core.Models;
using MeshVault.Core.Services;
using MeshVault.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeshVault.Tests;

public class VariantGroupTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly ServiceProvider _services;

    public VariantGroupTests()
    {
        _conn.Open();

        var services = new ServiceCollection();
        services.AddDbContextFactory<MeshVaultDbContext>(o => o.UseSqlite(_conn));
        _services = services.BuildServiceProvider();

        using var db = Factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Libraries.Add(new Library { Name = "Test", Path = "/library" });
        db.SaveChanges();
    }

    private IDbContextFactory<MeshVaultDbContext> Factory =>
        _services.GetRequiredService<IDbContextFactory<MeshVaultDbContext>>();

    private GroupPlanner NewPlanner() => new(Factory);
    private GroupStore NewStore() => new(Factory);
    private ModelCatalog NewCatalog() => new(Factory, new LocalUser());
    private ModelEditor NewEditor() => new(Factory, new LocalUser());

    /// <summary>
    /// A folder holding one export of one sculpt — the shape every terrain
    /// model in a Manyfold-organised library has.
    /// </summary>
    private async Task<ModelEntry> ModelAsync(
        string path, string sculptKey, string? label, int rank, string? name = null)
    {
        await using var db = await Factory.CreateDbContextAsync();
        var model = new ModelEntry
        {
            LibraryId = 1,
            RelativePath = path,
            Name = name ?? path.Split('/')[^1],
            Files =
            [
                new ModelFile
                {
                    RelativePath = $"{path}/mesh.stl",
                    FileName = "mesh.stl",
                    Extension = ".stl",
                    Kind = FileKind.Mesh,
                    SculptKey = sculptKey,
                    SculptName = sculptKey,
                    VariantLabel = label,
                    VariantRank = rank,
                },
            ],
        };
        db.Models.Add(model);
        await db.SaveChangesAsync();
        return model;
    }

    /// <summary>The four folders a set of terrain actually ships in.</summary>
    private async Task SeedFourFoldersAsync()
    {
        await ModelAsync("t/unsupported/is-130#75", "is 130 ground", null, 0, "Is 130 Ground");
        await ModelAsync("t/hollowed/is-130-hol#51", "is 130 ground", "Hollowed", 3, "Is 130 Hol Ground");
        await ModelAsync("t/no-logo/is-130-nl#59", "is 130 ground", "No logo", 4, "Is 130 Nl Ground");
        await ModelAsync("t/supported/is-130-sup#67", "is 130 ground", "Supported", 30, "Is 130 Sup Ground");
    }

    [Fact]
    public async Task Proposes_folders_holding_the_same_sculpt()
    {
        await SeedFourFoldersAsync();

        var plan = await NewPlanner().PlanAsync(1);
        var proposal = Assert.Single(plan.Pending);

        Assert.Equal("is 130 ground", proposal.Key);
        Assert.Equal(4, proposal.Members.Count);

        // The plain export leads, so the group's name has no abbreviation
        // buried in it and its card shows the sculpt rather than supports.
        Assert.Equal("Is 130 Ground", proposal.Name);
        Assert.Equal("Plain", proposal.Primary.Variant);
        Assert.Equal("t", proposal.CommonParent);
    }

    [Fact]
    public async Task A_sculpt_in_only_one_folder_is_not_a_group()
    {
        await ModelAsync("t/lonely#1", "lonely", null, 0);

        Assert.Empty((await NewPlanner().PlanAsync(1)).Pending);
    }

    [Fact]
    public async Task A_folder_holding_many_sculpts_is_left_alone()
    {
        // The raw pack drop: one folder, ninety-eight sculpts. Folding it in
        // would claim the whole pack is one mini.
        await using (var db = await Factory.CreateDbContextAsync())
        {
            db.Models.Add(new ModelEntry
            {
                LibraryId = 1,
                RelativePath = "inbox/UD-Supported",
                Name = "UD-Supported",
                Files =
                [
                    Mesh("inbox/UD-Supported/a.stl", "ud 001 wall"),
                    Mesh("inbox/UD-Supported/b.stl", "ud 002 door"),
                ],
            });
            await db.SaveChangesAsync();
        }

        await ModelAsync("t/wall#1", "ud 001 wall", null, 0);

        Assert.Empty((await NewPlanner().PlanAsync(1)).Pending);

        static ModelFile Mesh(string path, string key) => new()
        {
            RelativePath = path,
            FileName = path.Split('/')[^1],
            Extension = ".stl",
            Kind = FileKind.Mesh,
            SculptKey = key,
            SculptName = key,
        };
    }

    [Fact]
    public async Task Applying_marks_one_member_as_the_one_shown()
    {
        await SeedFourFoldersAsync();
        var plan = await NewPlanner().PlanAsync(1);

        Assert.Equal(4, await NewStore().ApplyAsync(plan.Pending));

        await using var db = await Factory.CreateDbContextAsync();
        var models = await db.Models.ToListAsync();

        Assert.All(models, m => Assert.Equal("is 130 ground", m.GroupKey));
        Assert.All(models, m => Assert.Equal("Is 130 Ground", m.GroupName));

        var primary = Assert.Single(models, m => m.GroupPrimary);
        Assert.Equal("t/unsupported/is-130#75", primary.RelativePath);
    }

    [Fact]
    public async Task Browse_lists_a_group_once()
    {
        await SeedFourFoldersAsync();
        await ModelAsync("t/other#9", "something else", null, 0);

        var before = await NewCatalog().SearchAsync(new ModelQuery());
        Assert.Equal(5, before.TotalCount);

        await NewStore().ApplyAsync((await NewPlanner().PlanAsync(1)).Pending);

        var after = await NewCatalog().SearchAsync(new ModelQuery());
        Assert.Equal(2, after.TotalCount);
        Assert.Contains(after.Items, c => c.Model.Name == "Is 130 Ground");
    }

    [Fact]
    public async Task Applying_twice_is_not_proposed_again()
    {
        await SeedFourFoldersAsync();
        var store = NewStore();
        await store.ApplyAsync((await NewPlanner().PlanAsync(1)).Pending);

        var plan = await NewPlanner().PlanAsync(1);

        Assert.Empty(plan.Pending);
        Assert.Single(plan.Proposals);
        Assert.True(plan.Proposals[0].AlreadyApplied);
    }

    [Fact]
    public async Task Ungrouping_puts_every_folder_back()
    {
        await SeedFourFoldersAsync();
        var store = NewStore();
        await store.ApplyAsync((await NewPlanner().PlanAsync(1)).Pending);

        Assert.Equal(4, await store.UngroupAsync(1, "is 130 ground"));

        // A complete undo: nothing was deleted, so all four stand alone again.
        Assert.Equal(4, (await NewCatalog().SearchAsync(new ModelQuery())).TotalCount);

        await using var db = await Factory.CreateDbContextAsync();
        Assert.All(await db.Models.ToListAsync(), m =>
        {
            Assert.Null(m.GroupKey);
            Assert.False(m.GroupPrimary);
        });
    }

    [Fact]
    public async Task A_group_shows_every_folders_files_on_one_page()
    {
        await SeedFourFoldersAsync();
        await NewStore().ApplyAsync((await NewPlanner().PlanAsync(1)).Pending);

        await using var db = await Factory.CreateDbContextAsync();
        var primary = await db.Models.FirstAsync(m => m.GroupPrimary);

        var members = await NewCatalog().GetGroupMembersAsync(primary.Id);

        Assert.Equal(4, members.Count);
        Assert.Equal(4, members.SelectMany(m => m.Files).Count());

        // Best export first, so the viewer opens on the clean copy.
        Assert.Equal("t/unsupported/is-130#75", members[0].RelativePath);
        Assert.Equal("t/supported/is-130-sup#67", members[^1].RelativePath);
    }

    [Fact]
    public async Task An_ungrouped_model_reports_no_members()
    {
        var lonely = await ModelAsync("t/lonely#1", "lonely", null, 0);

        Assert.Empty(await NewCatalog().GetGroupMembersAsync(lonely.Id));
    }

    [Fact]
    public async Task Tagging_a_group_tags_every_folder_in_it()
    {
        await SeedFourFoldersAsync();
        await NewStore().ApplyAsync((await NewPlanner().PlanAsync(1)).Pending);

        await using (var db = await Factory.CreateDbContextAsync())
        {
            var primary = await db.Models.FirstAsync(m => m.GroupPrimary);
            await NewEditor().AddTagAsync(primary.Id, "terrain");
        }

        await using var check = await Factory.CreateDbContextAsync();
        var models = await check.Models.Include(m => m.Tags).ToListAsync();

        // A tag describes the sculpt, not the export.
        Assert.All(models, m => Assert.Contains(m.Tags, t => t.Name == "terrain"));
    }

    [Fact]
    public async Task Untagging_a_group_clears_every_folder()
    {
        await SeedFourFoldersAsync();
        await NewStore().ApplyAsync((await NewPlanner().PlanAsync(1)).Pending);

        var editor = NewEditor();
        int primaryId, tagId;

        await using (var db = await Factory.CreateDbContextAsync())
            primaryId = (await db.Models.FirstAsync(m => m.GroupPrimary)).Id;

        tagId = (await editor.AddTagAsync(primaryId, "terrain"))!.Id;
        await editor.RemoveTagAsync(primaryId, tagId);

        await using var check = await Factory.CreateDbContextAsync();
        Assert.All(await check.Models.Include(m => m.Tags).ToListAsync(),
            m => Assert.Empty(m.Tags));
    }

    [Fact]
    public async Task Favoriting_a_group_favorites_the_whole_of_it()
    {
        await SeedFourFoldersAsync();
        await NewStore().ApplyAsync((await NewPlanner().PlanAsync(1)).Pending);

        int primaryId;
        await using (var db = await Factory.CreateDbContextAsync())
            primaryId = (await db.Models.FirstAsync(m => m.GroupPrimary)).Id;

        var editor = NewEditor();
        Assert.True(await editor.ToggleFavoriteAsync(primaryId));

        await using (var db = await Factory.CreateDbContextAsync())
            Assert.Equal(4, await db.Favorites.CountAsync());

        // And off again, so a group never sits half-starred.
        Assert.False(await editor.ToggleFavoriteAsync(primaryId));

        await using (var db = await Factory.CreateDbContextAsync())
            Assert.Equal(0, await db.Favorites.CountAsync());
    }

    [Fact]
    public void Common_parent_is_the_deepest_shared_folder()
    {
        Assert.Equal("dnd/terrain", Paths.CommonParent(
        [
            "dnd/terrain/supported/a#1",
            "dnd/terrain/unsupported/no-logo/b#2",
        ]));

        Assert.Equal("", Paths.CommonParent(["inbox/a#1", "dnd/b#2"]));
        Assert.Equal("", Paths.CommonParent(["top#1"]));
    }

    public void Dispose()
    {
        _services.Dispose();
        _conn.Dispose();
    }
}
