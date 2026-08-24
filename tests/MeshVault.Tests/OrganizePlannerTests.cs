using MeshVault.Core.Models;
using MeshVault.Core.Services;
using MeshVault.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Tests;

/// <summary>
/// Planning a reorganisation. Nothing here touches a disk; the point is that
/// the plan a person reads is exactly what would happen, including the parts
/// that would not.
/// </summary>
public class OrganizePlannerTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly IDbContextFactory<MeshVaultDbContext> _factory;
    private readonly OrganizePlanner _planner;
    private readonly ModelEditor _editor;

    private sealed class FakeUser : ICurrentUser
    {
        public string UserId => "alice";
    }

    private sealed class Factory(SqliteConnection conn) : IDbContextFactory<MeshVaultDbContext>
    {
        public MeshVaultDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<MeshVaultDbContext>().UseSqlite(conn).Options);
    }

    public OrganizePlannerTests()
    {
        _conn.Open();
        _factory = new Factory(_conn);

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Libraries.Add(new Library { Name = "L", Path = "/l" });
        db.SaveChanges();

        _planner = new OrganizePlanner(_factory, new FakeUser());
        _editor = new ModelEditor(_factory, new FakeUser());
    }

    private async Task<int> NewModel(string name, string relativePath, params string[] files)
    {
        await using var db = _factory.CreateDbContext();
        var model = new ModelEntry
        {
            LibraryId = 1,
            Name = name,
            RelativePath = relativePath,
            AddedUtc = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            FileModifiedUtc = DateTimeOffset.UtcNow,
        };

        foreach (var file in files)
        {
            model.Files.Add(new ModelFile
            {
                RelativePath = $"{relativePath}/{file}",
                FileName = file,
                Extension = Path.GetExtension(file),
                Kind = FileKind.Mesh,
                ModifiedUtc = DateTimeOffset.UtcNow,
            });
        }

        db.Models.Add(model);
        await db.SaveChangesAsync();
        return model.Id;
    }

    private Task<OrganizePlan> Plan(OrganizeRules? rules = null) =>
        _planner.PlanAsync(1, rules ?? new OrganizeRules());

    [Fact]
    public async Task A_model_is_planned_into_its_designer_folder()
    {
        var id = await NewModel("3DBenchy", "thing_4821", "benchy.stl");
        await _editor.SetDesignerAsync(id, "Prusa Research");

        var move = Assert.Single((await Plan()).Moves);

        Assert.Equal(MoveOutcome.Move, move.Outcome);
        Assert.Equal("thing_4821", move.From);
        Assert.Equal("Prusa Research/3DBenchy", move.To);
    }

    [Fact]
    public async Task A_model_with_no_designer_lands_somewhere_obvious()
    {
        await NewModel("Cable Clip", "cable clip", "clip.stl");

        var move = Assert.Single((await Plan()).Moves);

        Assert.Equal("Unsorted/Cable Clip", move.To);
    }

    [Fact]
    public async Task A_model_already_in_place_is_reported_as_such()
    {
        var id = await NewModel("3DBenchy", "Prusa Research/3DBenchy", "benchy.stl");
        await _editor.SetDesignerAsync(id, "Prusa Research");

        var plan = await Plan();

        Assert.Equal(MoveOutcome.AlreadyThere, Assert.Single(plan.Moves).Outcome);
        Assert.Equal(0, plan.Moving);
    }

    [Fact]
    public async Task Running_it_twice_would_change_nothing_the_second_time()
    {
        // The plan has to be stable, or organising becomes something you can
        // never finish doing.
        var id = await NewModel("3DBenchy", "thing_4821", "benchy.stl");
        await _editor.SetDesignerAsync(id, "Prusa Research");

        var first = await Plan();
        var destination = first.Moves[0].To;

        await using (var db = _factory.CreateDbContext())
        {
            var model = await db.Models.SingleAsync(m => m.Id == id);
            model.RelativePath = destination;
            await db.SaveChangesAsync();
        }

        Assert.True((await Plan()).IsEmpty);
    }

    [Fact]
    public async Task Two_models_heading_for_the_same_folder_are_flagged_not_merged()
    {
        // Silently letting one land inside the other is how a library gets
        // quietly destroyed.
        var a = await NewModel("Dragon", "dragon-a", "a.stl");
        var b = await NewModel("Dragon", "dragon-b", "b.stl");
        await _editor.SetDesignerAsync(a, "Cinderwing3D");
        await _editor.SetDesignerAsync(b, "Cinderwing3D");

        var plan = await Plan();

        Assert.Equal(1, plan.Moving);
        Assert.Equal(1, plan.Colliding);
        Assert.Contains(plan.Moves, m => m.Problem is not null);
    }

    [Fact]
    public async Task A_model_that_is_staying_put_still_blocks_that_folder()
    {
        // The one already there was never going to move, so the newcomer must
        // not be told the path is free.
        var settled = await NewModel("Dragon", "Cinderwing3D/Dragon", "a.stl");
        var incoming = await NewModel("Dragon", "elsewhere", "b.stl");
        await _editor.SetDesignerAsync(settled, "Cinderwing3D");
        await _editor.SetDesignerAsync(incoming, "Cinderwing3D");

        var plan = await Plan();

        Assert.Equal(MoveOutcome.Collides,
            plan.Moves.Single(m => m.ModelId == incoming).Outcome);
    }

    [Fact]
    public async Task Files_are_left_alone_unless_renaming_was_asked_for()
    {
        var id = await NewModel("Dragon", "dragon", "dragon_presupported.stl", "base.stl");
        await _editor.SetDesignerAsync(id, "Cinderwing3D");

        Assert.Equal(0, (await Plan()).Renames);
    }

    [Fact]
    public async Task Renaming_keeps_the_extension()
    {
        var id = await NewModel("Dragon", "dragon", "weird name.stl");
        await _editor.SetDesignerAsync(id, "Cinderwing3D");

        var plan = await Plan(new OrganizeRules { RenameFiles = true, FileTemplate = "{model}" });

        Assert.Equal("Dragon.stl", Assert.Single(plan.Moves[0].Renames).To);
    }

    [Fact]
    public async Task The_default_file_template_keeps_what_the_original_name_encoded()
    {
        var id = await NewModel("Dragon", "dragon", "presupported.stl");
        await _editor.SetDesignerAsync(id, "Cinderwing3D");

        var plan = await Plan(new OrganizeRules { RenameFiles = true });

        Assert.Equal("Dragon - presupported.stl", Assert.Single(plan.Moves[0].Renames).To);
    }

    [Fact]
    public async Task Files_that_would_collide_are_numbered_rather_than_overwritten()
    {
        // Two meshes rendering to one name would leave a model with half its
        // files and no error.
        var id = await NewModel("Dragon", "dragon", "a.stl", "b.stl");
        await _editor.SetDesignerAsync(id, "Cinderwing3D");

        var plan = await Plan(new OrganizeRules { RenameFiles = true, FileTemplate = "{model}" });
        var names = plan.Moves[0].Renames.Select(r => r.To).ToList();

        Assert.Equal(2, names.Count);
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Contains("Dragon (2).stl", names);
    }

    [Fact]
    public async Task A_file_already_correctly_named_is_not_listed_as_a_rename()
    {
        var id = await NewModel("Dragon", "dragon", "Dragon.stl");
        await _editor.SetDesignerAsync(id, "Cinderwing3D");

        var plan = await Plan(new OrganizeRules { RenameFiles = true, FileTemplate = "{model}" });

        Assert.Empty(plan.Moves[0].Renames);
    }

    [Fact]
    public async Task A_template_that_renders_to_nothing_leaves_the_model_alone()
    {
        await NewModel("Benchy", "benchy", "b.stl");

        var plan = await Plan(new OrganizeRules { FolderTemplate = "{nonsense}" });

        Assert.Equal(MoveOutcome.Unusable, Assert.Single(plan.Moves).Outcome);
        Assert.Equal(0, plan.Moving);
    }

    [Fact]
    public async Task A_designer_whose_name_contains_a_slash_cannot_add_a_folder_level()
    {
        var id = await NewModel("Dragon", "dragon", "a.stl");
        await _editor.SetDesignerAsync(id, "Cinder/wing");

        var move = Assert.Single((await Plan()).Moves);

        Assert.Equal("Cinder-wing/Dragon", move.To);
    }

    [Fact]
    public async Task The_plan_counts_what_it_would_and_would_not_do()
    {
        var a = await NewModel("A", "Prusa/A", "a.stl");
        await _editor.SetDesignerAsync(a, "Prusa");
        var b = await NewModel("B", "loose", "b.stl");
        await _editor.SetDesignerAsync(b, "Prusa");

        var plan = await Plan();

        Assert.Equal(1, plan.AlreadyThere);
        Assert.Equal(1, plan.Moving);
        Assert.False(plan.IsEmpty);
    }

    public void Dispose() => _conn.Dispose();
}
