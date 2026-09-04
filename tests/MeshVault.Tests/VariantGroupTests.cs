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

    private GroupReconciler NewReconciler() => new(Factory);
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
    public async Task Folders_holding_the_same_sculpt_become_one_group()
    {
        await SeedFourFoldersAsync();

        Assert.Equal(4, await NewReconciler().ReconcileAsync(1));

        await using var db = await Factory.CreateDbContextAsync();
        var models = await db.Models.ToListAsync();

        Assert.All(models, m => Assert.Equal("is 130 ground", m.GroupKey));

        // The plain export leads, so the group's name has no abbreviation
        // buried in it and its card shows the sculpt rather than supports.
        Assert.All(models, m => Assert.Equal("Is 130 Ground", m.GroupName));
        Assert.Equal("t/unsupported/is-130#75",
            Assert.Single(models, m => m.GroupPrimary).RelativePath);
    }

    [Fact]
    public async Task Reconciling_a_settled_library_changes_nothing()
    {
        // The property that lets this run after every scan rather than being
        // approved once: it has to settle instead of oscillating, or a library
        // would rearrange itself on a loop.
        await SeedFourFoldersAsync();
        await NewReconciler().ReconcileAsync(1);

        Assert.Equal(0, await NewReconciler().ReconcileAsync(1));
    }

    [Fact]
    public async Task A_sculpt_in_only_one_folder_is_not_a_group()
    {
        await ModelAsync("t/lonely#1", "lonely", null, 0);

        await NewReconciler().ReconcileAsync(1);

        await using var db = await Factory.CreateDbContextAsync();
        Assert.Null((await db.Models.SingleAsync()).GroupKey);
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

        await NewReconciler().ReconcileAsync(1);

        await using var check = await Factory.CreateDbContextAsync();
        Assert.All(await check.Models.ToListAsync(), m => Assert.Null(m.GroupKey));

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
    public async Task Browse_lists_a_group_once()
    {
        await SeedFourFoldersAsync();
        await ModelAsync("t/other#9", "something else", null, 0);

        var before = await NewCatalog().SearchAsync(new ModelQuery());
        Assert.Equal(5, before.TotalCount);

        await NewReconciler().ReconcileAsync(1);

        var after = await NewCatalog().SearchAsync(new ModelQuery());
        Assert.Equal(2, after.TotalCount);
        Assert.Contains(after.Items, c => c.Model.Name == "Is 130 Ground");
    }

    [Fact]
    public async Task Correcting_a_sculpt_takes_that_folder_out_of_the_group()
    {
        // What replaced the Ungroup button. Grouping is read from the files, so
        // a group is separated by disagreeing with the reading rather than by
        // overriding the result of it -- and a button that undid this would only
        // last until the next scan put the group back.
        await SeedFourFoldersAsync();
        await NewReconciler().ReconcileAsync(1);

        int fileId;
        await using (var db = await Factory.CreateDbContextAsync())
        {
            fileId = (await db.Files
                .Include(f => f.ModelEntry)
                .FirstAsync(f => f.ModelEntry!.RelativePath == "t/no-logo/is-130-nl#59")).Id;
        }

        await NewEditor().SetVariantAsync(fileId, "Something Else Entirely", []);
        await NewReconciler().ReconcileAsync(1);

        await using var check = await Factory.CreateDbContextAsync();
        var models = await check.Models.ToListAsync();

        var moved = models.Single(m => m.RelativePath == "t/no-logo/is-130-nl#59");
        Assert.Null(moved.GroupKey);

        // The other three are still one thing, and the odd one out now shows on
        // its own -- four cards' worth of folders listed as two.
        Assert.Equal(3, models.Count(m => m.GroupKey == "is 130 ground"));
        Assert.Equal(2, (await NewCatalog().SearchAsync(new ModelQuery())).TotalCount);
    }

    [Fact]
    public async Task A_group_worn_down_to_one_folder_stops_being_a_group()
    {
        await ModelAsync("t/plain#1", "wall", null, 0, "Wall");
        await ModelAsync("t/supported#2", "wall", "Supported", 30, "Wall Sup");
        await NewReconciler().ReconcileAsync(1);

        await using (var db = await Factory.CreateDbContextAsync())
        {
            db.Models.Remove(await db.Models.FirstAsync(m => m.RelativePath == "t/supported#2"));
            await db.SaveChangesAsync();
        }

        await NewReconciler().ReconcileAsync(1);

        await using var check = await Factory.CreateDbContextAsync();
        var left = await check.Models.SingleAsync();

        // A stale key would leave Browse filtering on GroupPrimary for a group
        // of one, and the survivor is not the primary if it was the supported cut.
        Assert.Null(left.GroupKey);
        Assert.False(left.GroupPrimary);
        Assert.Equal(1, (await NewCatalog().SearchAsync(new ModelQuery())).TotalCount);
    }

    [Fact]
    public async Task A_group_shows_every_folders_files_on_one_page()
    {
        await SeedFourFoldersAsync();
        await NewReconciler().ReconcileAsync(1);

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
        await NewReconciler().ReconcileAsync(1);

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
        await NewReconciler().ReconcileAsync(1);

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
        await NewReconciler().ReconcileAsync(1);

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

    // Editing a sculpt rather than a file at a time ---------------------------

    [Fact]
    public async Task Renaming_a_sculpt_reaches_every_folder_in_its_group()
    {
        // The operation that did not exist: a sculpt with four exports could
        // only be renamed by editing four files and typing the same name into
        // each, where one slip left two sculpts a letter apart.
        await SeedFourFoldersAsync();
        await NewReconciler().ReconcileAsync(1);

        int primaryId;
        await using (var db = await Factory.CreateDbContextAsync())
            primaryId = (await db.Models.FirstAsync(m => m.GroupPrimary)).Id;

        Assert.Equal(4,
            await NewEditor().RenameSculptAsync(primaryId, "is 130 ground", "Is 130 Grid Garage"));

        await using var check = await Factory.CreateDbContextAsync();
        Assert.All(await check.Files.ToListAsync(), f =>
        {
            Assert.Equal("Is 130 Grid Garage", f.SculptName);
            Assert.Equal("is 130 grid garage", f.SculptKey);

            // A decision, so no later pass argues with it.
            Assert.True(f.VariantSetByUser);
        });
    }

    [Fact]
    public async Task Renaming_onto_another_sculpts_name_merges_the_two()
    {
        // Not a special case: the key is what groups, so two files carrying one
        // key are one sculpt. That is why there is no separate merge to drift
        // out of step with rename.
        var model = await ModelAsync("t/wall#1", "wall", null, 0, "Wall");
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var entry = await db.Models.Include(m => m.Files).SingleAsync(m => m.Id == model.Id);
            entry.Files.Add(new ModelFile
            {
                RelativePath = "t/wall#1/wal.stl", FileName = "wal.stl", Extension = ".stl",
                Kind = FileKind.Mesh, SculptKey = "wal", SculptName = "Wal",
            });
            await db.SaveChangesAsync();
        }

        // "Wal" was a typo in the creator's filename, and no vocabulary can
        // spell-check. Renaming it onto "Wall" is the way out.
        Assert.Equal(1, await NewEditor().RenameSculptAsync(model.Id, "wal", "Wall"));

        await using var check = await Factory.CreateDbContextAsync();
        var files = await check.Files.ToListAsync();

        Assert.All(files, f => Assert.Equal("wall", f.SculptKey));
        Assert.Single(VariantGrouper.Group(files));
    }

    [Fact]
    public async Task Moving_models_to_a_sculpt_keeps_the_variants_they_carry()
    {
        // Which mini this is and which cut of it are different questions. A move
        // that answered both would quietly declare a supported export plain.
        var model = await ModelAsync("t/pack#1", "wrong", "Supported", 30, "Pack");

        int fileId;
        await using (var db = await Factory.CreateDbContextAsync())
            fileId = (await db.Files.SingleAsync()).Id;

        var supported = new VariantDefinition { Name = "Supported", PreviewRank = 30 };
        Assert.Equal(1, await NewEditor().SetSculptAsync([fileId], "Orc Chief", [supported]));

        await using var check = await Factory.CreateDbContextAsync();
        var file = await check.Files.SingleAsync();

        Assert.Equal("orc chief", file.SculptKey);
        Assert.Equal("Supported", file.VariantLabel);
        Assert.Equal(30, file.VariantRank);
    }

    [Fact]
    public async Task Setting_variants_in_bulk_leaves_each_sculpt_where_it_is()
    {
        await ModelAsync("t/a#1", "orc chief", null, 0, "A");
        await ModelAsync("t/b#2", "orc grunt", null, 0, "B");

        List<int> ids;
        await using (var db = await Factory.CreateDbContextAsync())
            ids = await db.Files.Select(f => f.Id).ToListAsync();

        var hollowed = new VariantDefinition { Name = "Hollowed", PreviewRank = 3 };
        var supported = new VariantDefinition { Name = "Supported", PreviewRank = 30 };

        Assert.Equal(2, await NewEditor().SetVariantsAsync(ids, [supported, hollowed]));

        await using var check = await Factory.CreateDbContextAsync();
        var files = await check.Files.OrderBy(f => f.Id).ToListAsync();

        // Alphabetical, not by rank, so the label reads the same however the
        // preview ranks are tuned.
        Assert.All(files, f => Assert.Equal("Hollowed, Supported", f.VariantLabel));
        Assert.All(files, f => Assert.Equal(33, f.VariantRank));

        // Two sculpts still, which marking them both supported must not change.
        Assert.Equal(["orc chief", "orc grunt"], files.Select(f => f.SculptKey).Order());
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
