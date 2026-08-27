using MeshVault.Core.Models;
using MeshVault.Core.Services;
using MeshVault.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Tests;

/// <summary>
/// Planning a reorganisation that gives each mini its own folder.
///
/// The same rule serves both shapes a library arrives in: a pack folder holding
/// ninety-eight minis is broken up, and several folders each holding one export
/// of the same mini are brought together. Nothing here touches a disk.
/// </summary>
public class OrganizeSplitTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly IDbContextFactory<MeshVaultDbContext> _factory;
    private readonly OrganizePlanner _planner;
    private readonly VariantClassifier _classifier = new();

    private sealed class FakeUser : ICurrentUser
    {
        public string UserId => "alice";
    }

    private sealed class Factory(SqliteConnection conn) : IDbContextFactory<MeshVaultDbContext>
    {
        public MeshVaultDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<MeshVaultDbContext>().UseSqlite(conn).Options);
    }

    public OrganizeSplitTests()
    {
        _conn.Open();
        _factory = new Factory(_conn);

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Libraries.Add(new Library { Name = "L", Path = "/l" });
        db.Designers.Add(new Designer { Name = "Dungeon Blocks", NormalizedName = "dungeon blocks" });
        db.SaveChanges();

        _planner = new OrganizePlanner(_factory, new FakeUser(), new VariantRules());
    }

    /// <summary>Adds a model whose files are classified exactly as indexing would.</summary>
    private async Task<int> NewModel(string name, string relativePath, params string[] files)
    {
        await using var db = _factory.CreateDbContext();
        var model = new ModelEntry
        {
            LibraryId = 1,
            Name = name,
            RelativePath = relativePath,
            DesignerId = 1,
            AddedUtc = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
        };

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file);
            model.Files.Add(new ModelFile
            {
                RelativePath = $"{relativePath}/{file}",
                FileName = file,
                Extension = extension,
                Kind = FileKinds.FromExtension(extension),
            });
        }

        db.Models.Add(model);
        await db.SaveChangesAsync();

        foreach (var file in model.Files) _classifier.Apply(model, file);
        await db.SaveChangesAsync();

        return model.Id;
    }

    private Task<OrganizePlan> Plan(string folderTemplate = "{designer}/{sculpt}") =>
        _planner.PlanAsync(1, new OrganizeRules { FolderTemplate = folderTemplate });

    [Fact]
    public async Task A_pack_folder_becomes_one_folder_per_mini()
    {
        // The Ultimate Dungeon shape: one folder, many minis, each shipped
        // supported and unsupported.
        await NewModel("UD-Supported", "inbox/UD-Supported",
            "UD-001-SUP-Wall.stl", "UD-002-SUP-Door.stl", "UD-003-SUP-Window.stl");

        var plan = await Plan();

        Assert.Equal(3, plan.Moving);
        Assert.Equal(1, plan.PacksSplit);
        Assert.Equal(
            ["Dungeon Blocks/UD 001 Wall", "Dungeon Blocks/UD 002 Door", "Dungeon Blocks/UD 003 Window"],
            plan.Moves.Select(m => m.To).Order().ToList());
    }

    [Fact]
    public async Task Separate_variant_folders_land_in_the_same_place()
    {
        // The Manyfold shape: four folders, one export of one mini in each.
        // The same rule that splits a pack merges these.
        await NewModel("Is 130 Ground", "t/unsupported/is-130#75", "IS-130-Ground.stl");
        await NewModel("Is 130 Hol Ground", "t/hollowed/is-130-hol#51", "IS-130-HOL-Ground.stl");
        await NewModel("Is 130 Nl Ground", "t/no-logo/is-130-nl#59", "IS-130-NL-Ground.stl");
        await NewModel("Is 130 Sup Ground", "t/supported/is-130-sup#67", "IS-130-SUP-Ground.stl");

        var plan = await Plan();

        Assert.Equal(4, plan.Moving);
        Assert.Single(plan.Moves.Select(m => m.To).Distinct());
        Assert.Equal("Dungeon Blocks/IS 130 Ground", plan.Moves[0].To);
    }

    [Fact]
    public async Task A_companion_file_follows_its_mesh()
    {
        // A slicer project named after the mesh belongs with it, not in a heap.
        await NewModel("UD-Supported", "inbox/UD-Supported",
            "UD-001-SUP-Wall.stl", "UD-001-SUP-Wall.lys",
            "UD-002-SUP-Door.stl", "UD-002-SUP-Door.lys");

        var plan = await Plan();

        Assert.Equal(2, plan.Moving);
        foreach (var move in plan.Moves) Assert.Equal(2, move.FileIds.Count);
    }

    [Fact]
    public async Task A_companion_left_behind_catches_up_with_its_mesh()
    {
        // What an interrupted run leaves: the meshes filed on the first pass and
        // a slicer project still sitting in the husk of the folder they left.
        // Requiring a mesh in the same folder would strand it for good, because
        // the meshes are never coming back for it.
        await NewModel("UD 001 Wall", "Dungeon Blocks/UD 001 Wall", "UD-001-SUP-Wall.stl");
        await NewModel("UD-Hollowed", "inbox/UD-Hollowed", "UD-001-HOL-Wall.lys");

        var plan = await Plan();

        Assert.Equal(0, plan.Unusable);

        var orphan = plan.Moves.Single(m => m.From == "inbox/UD-Hollowed");
        Assert.Equal("Dungeon Blocks/UD 001 Wall", orphan.To);
    }

    [Fact]
    public async Task A_companion_follows_its_mesh_rather_than_its_own_folder()
    {
        // The husk it was left in need not share the mesh's metadata. Rendering
        // the template against the husk would land it near the mesh but not with
        // it, which is no better than leaving it where it was.
        await NewModel("UD 001 Wall", "Dungeon Blocks/UD 001 Wall", "UD-001-SUP-Wall.stl");
        await NewModel("orphans", "inbox/orphans", "UD-001-HOL-Wall.lys");

        await using (var db = _factory.CreateDbContext())
        {
            // The mesh's folder is tagged; the husk is not.
            var mesh = await db.Models.Include(m => m.Tags)
                .FirstAsync(m => m.RelativePath == "Dungeon Blocks/UD 001 Wall");
            mesh.Tags.Add(new Tag { Name = "terrain", NormalizedName = "terrain" });
            await db.SaveChangesAsync();
        }

        var plan = await _planner.PlanAsync(1, new OrganizeRules
        {
            FolderTemplate = "{tag}/{sculpt}",
        });

        var orphan = plan.Moves.Single(m => m.From == "inbox/orphans");
        Assert.Equal("terrain/UD 001 Wall", orphan.To);
    }

    [Fact]
    public async Task A_companion_with_no_mesh_anywhere_is_still_left_alone()
    {
        // The guard that stops a stray readme being filed under a random mini
        // has to survive: only a companion whose sculpt genuinely exists gets
        // sent after it.
        await NewModel("notes", "inbox/notes", "UD-999-HOL-Nothing.lys");

        var plan = await Plan();

        Assert.Equal(0, plan.Moving);
        Assert.Equal(1, plan.Unusable);
    }

    [Fact]
    public async Task A_folder_reading_as_no_mini_is_left_alone()
    {
        await NewModel("Notes", "inbox/Notes", "readme.txt", "photo.png");

        var plan = await Plan();

        Assert.Equal(0, plan.Moving);
        Assert.Equal(1, plan.Unusable);
    }

    [Fact]
    public async Task Colliding_sidecars_are_listed_for_deletion()
    {
        // Four folders merging into one bring four files called the same thing.
        await NewModel("A", "t/unsupported/is-130#75", "IS-130-Ground.stl", "datapackage.json");
        await NewModel("B", "t/hollowed/is-130-hol#51", "IS-130-HOL-Ground.stl", "datapackage.json");
        await NewModel("C", "t/supported/is-130-sup#67", "IS-130-SUP-Ground.stl", "datapackage.json");

        var plan = await Plan();

        Assert.Equal(3, plan.Deletions.Count);
        Assert.All(plan.Deletions, d => Assert.EndsWith("datapackage.json", d.Path));

        // The meshes are distinct, so only the sidecars are affected.
        Assert.DoesNotContain(plan.Deletions, d => d.Path.EndsWith(".stl"));
    }

    [Fact]
    public async Task A_sidecar_arriving_alone_is_not_deleted()
    {
        // Nothing collides with it, so removing it would be tidying up
        // somebody's library uninvited.
        await NewModel("A", "t/unsupported/is-130#75", "IS-130-Ground.stl", "datapackage.json");

        var plan = await Plan();

        Assert.Empty(plan.Deletions);
    }

    [Fact]
    public async Task Without_the_sculpt_token_nothing_splits()
    {
        // The old behaviour, unchanged: one folder in, one folder out.
        await NewModel("UD-Supported", "inbox/UD-Supported",
            "UD-001-SUP-Wall.stl", "UD-002-SUP-Door.stl");

        var plan = await Plan("{designer}/{model}");

        var move = Assert.Single(plan.Moves);
        Assert.Equal("Dungeon Blocks/UD-Supported", move.To);
        Assert.False(move.IsSplit);
        Assert.Empty(plan.Deletions);
    }

    [Fact]
    public async Task A_mini_already_in_the_right_folder_is_reported_as_such()
    {
        await NewModel("Wall", "Dungeon Blocks/Wall", "Wall.stl");

        var plan = await Plan();

        Assert.Equal(0, plan.Moving);
        Assert.Equal(1, plan.AlreadyThere);
    }

    public void Dispose()
    {
        _conn.Dispose();
    }
}
