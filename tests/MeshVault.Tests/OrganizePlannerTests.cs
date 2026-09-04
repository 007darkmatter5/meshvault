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

        _planner = new OrganizePlanner(_factory, new FakeUser(), new VariantRules());
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
                // Read from the extension, not assumed. Calling every file a
                // mesh made a readme its own sculpt, which is a shape the app
                // never produces and would have hidden the real behaviour here.
                Kind = FileKinds.FromExtension(Path.GetExtension(file)),
                ModifiedUtc = DateTimeOffset.UtcNow,
            });
        }

        db.Models.Add(model);
        await db.SaveChangesAsync();
        return model.Id;
    }

    /// <summary>
    /// Plans with a folder per model, unless the test chose its own template.
    /// </summary>
    /// <remarks>
    /// Nearly everything below was written when a folder per model was what the
    /// app shipped. The default is a folder per sculpt now, which asks a
    /// different question of each of them -- so the old shape is pinned here
    /// rather than inherited, where changing what ships would quietly rewrite
    /// thirteen tests into testing something else.
    ///
    /// <see cref="The_shipped_default_files_a_pack_by_sculpt"/> covers the
    /// default itself, and goes to the planner directly so that this cannot
    /// stand in front of it.
    /// </remarks>
    private Task<OrganizePlan> Plan(OrganizeRules? rules = null) =>
        _planner.PlanAsync(1, rules ?? new OrganizeRules { FolderTemplate = PerModel });

    /// <summary>The shape most of these tests were written against.</summary>
    private const string PerModel = "{designer}/{model}";

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

        var plan = await Plan(new OrganizeRules { FolderTemplate = PerModel, RenameFiles = true, FileTemplate = "{model}" });

        Assert.Equal("Dragon.stl", Assert.Single(plan.Moves[0].Renames).To);
    }

    [Fact]
    public async Task The_default_file_template_keeps_what_the_original_name_encoded()
    {
        var id = await NewModel("Dragon", "dragon", "presupported.stl");
        await _editor.SetDesignerAsync(id, "Cinderwing3D");

        var plan = await Plan(new OrganizeRules { FolderTemplate = PerModel, RenameFiles = true });

        Assert.Equal("Dragon - presupported.stl", Assert.Single(plan.Moves[0].Renames).To);
    }

    [Fact]
    public async Task Files_that_would_collide_are_numbered_rather_than_overwritten()
    {
        // Two meshes rendering to one name would leave a model with half its
        // files and no error.
        var id = await NewModel("Dragon", "dragon", "a.stl", "b.stl");
        await _editor.SetDesignerAsync(id, "Cinderwing3D");

        var plan = await Plan(new OrganizeRules { FolderTemplate = PerModel, RenameFiles = true, FileTemplate = "{model}" });
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

        var plan = await Plan(new OrganizeRules { FolderTemplate = PerModel, RenameFiles = true, FileTemplate = "{model}" });

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

    [Fact]
    public async Task Narrowing_a_plan_keeps_only_the_chosen_models()
    {
        var a = await NewModel("A", "loose/a", "a.stl");
        await _editor.SetDesignerAsync(a, "Prusa");
        var b = await NewModel("B", "loose/b", "b.stl");
        await _editor.SetDesignerAsync(b, "Prusa");

        var plan = await Plan();
        Assert.Equal(2, plan.Moving);

        var narrowed = plan.For(new HashSet<int> { a });

        Assert.Equal(1, narrowed.Moving);
        Assert.All(narrowed.Moves, m => Assert.Equal(a, m.ModelId));
    }

    [Fact]
    public async Task Narrowing_a_plan_cannot_add_a_move_of_its_own()
    {
        var a = await NewModel("A", "loose/a", "a.stl");
        await _editor.SetDesignerAsync(a, "Prusa");

        var plan = await Plan();

        // Asking for a model the plan never mentioned yields nothing rather
        // than inventing a destination for it. What runs is always a subset of
        // what was on screen.
        var narrowed = plan.For(new HashSet<int> { a, 9999 });

        Assert.Equal(plan.Moves.Count, narrowed.Moves.Count);
    }

    [Fact]
    public async Task A_folder_a_left_out_model_still_sits_in_is_called_out()
    {
        // A wants B's folder, and the planner allowed it only because B is
        // leaving in the same run. Run A alone and B is still there.
        var a = await NewModel("Wall", "loose/wall", "wall.stl");
        await _editor.SetDesignerAsync(a, "Prusa");
        var b = await NewModel("Other", "Prusa/Wall", "other.stl");
        await _editor.SetDesignerAsync(b, "Elegoo");

        var plan = await Plan();
        Assert.Equal(2, plan.Moving);

        var blocked = plan.VacancyNeeded(new HashSet<int> { a });

        Assert.Equal(b, Assert.Single(blocked).ModelId);

        // Take both and there is nothing left to warn about.
        Assert.Empty(plan.VacancyNeeded(new HashSet<int> { a, b }));
    }

    [Fact]
    public async Task Models_that_do_not_want_each_other_s_folders_raise_nothing()
    {
        var a = await NewModel("A", "loose/a", "a.stl");
        await _editor.SetDesignerAsync(a, "Prusa");
        var b = await NewModel("B", "loose/b", "b.stl");
        await _editor.SetDesignerAsync(b, "Prusa");

        var plan = await Plan();

        Assert.Empty(plan.VacancyNeeded(new HashSet<int> { a }));
    }

    [Fact]
    public async Task A_casing_convention_reaches_folders_and_files_separately()
    {
        var id = await NewModel("Spring Dragon", "dragon", "Wall 01.stl");
        await _editor.SetDesignerAsync(id, "Cinderwing3D");

        var plan = await Plan(new OrganizeRules
        {
            FolderTemplate = PerModel,
            RenameFiles = true,
            FolderCase = NameCase.Pascal,
            FileCase = NameCase.Kebab,
        });

        Assert.Equal("Cinderwing3D/SpringDragon", plan.Moves[0].To);
        Assert.Equal("spring-dragon-wall-01.stl", Assert.Single(plan.Moves[0].Renames).To);
    }

    [Fact]
    public async Task The_extension_keeps_its_own_case_whatever_the_convention()
    {
        // The extension is what tells every other program on the machine what
        // the file is, so it is appended after rendering rather than templated.
        var id = await NewModel("Dragon", "dragon", "body.STL");
        await _editor.SetDesignerAsync(id, "Cinderwing3D");

        var plan = await Plan(new OrganizeRules
        {
            FolderTemplate = PerModel,
            RenameFiles = true, FileTemplate = "{model}", FileCase = NameCase.Kebab,
        });

        Assert.Equal("dragon.STL", Assert.Single(plan.Moves[0].Renames).To);
    }

    [Fact]
    public async Task A_numbered_duplicate_obeys_the_convention_too()
    {
        // "dragon (2).stl" is not kebab-case, and a rule that held for every
        // name but the duplicates would be worse than no rule at all.
        var id = await NewModel("Dragon", "dragon", "a.stl", "b.stl");
        await _editor.SetDesignerAsync(id, "Cinderwing3D");

        var plan = await Plan(new OrganizeRules
        {
            FolderTemplate = PerModel,
            RenameFiles = true, FileTemplate = "{model}", FileCase = NameCase.Kebab,
        });
        var names = plan.Moves[0].Renames.Select(r => r.To).ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Contains("dragon-2.stl", names);
    }

    [Fact]
    public async Task Leaving_the_casing_alone_plans_exactly_what_it_always_did()
    {
        var id = await NewModel("Spring Dragon", "dragon", "Wall 01.stl");
        await _editor.SetDesignerAsync(id, "Cinderwing3D");

        var plan = await Plan(new OrganizeRules { FolderTemplate = PerModel, RenameFiles = true });

        Assert.Equal("Cinderwing3D/Spring Dragon", plan.Moves[0].To);
        Assert.Equal("Spring Dragon - Wall 01.stl", Assert.Single(plan.Moves[0].Renames).To);
    }

    [Fact]
    public async Task The_variant_token_carries_which_flavour_a_file_is()
    {
        // It rendered "Plain" for every file in the library. That is the one
        // token that can carry "this one is hollowed" through a rename which
        // throws the original name away, so a dead one turns an organise into
        // silent data loss.
        var id = await NewModel("Wall", "pack", "UD-001-HOL-Wall.stl", "UD-001-Wall.stl");
        await _editor.SetDesignerAsync(id, "Dungeon Blocks");

        await using (var db = _factory.CreateDbContext())
        {
            var classifier = new VariantClassifier();
            var model = await db.Models.Include(m => m.Files).SingleAsync(m => m.Id == id);
            foreach (var file in model.Files) classifier.Apply(model, file);
            await db.SaveChangesAsync();
        }

        var plan = await Plan(new OrganizeRules
        {
            FolderTemplate = PerModel,
            RenameFiles = true, FileTemplate = "{model} - {variant}",
        });
        var names = plan.Moves.SelectMany(m => m.Renames).Select(r => r.To).ToList();

        Assert.Contains(names, n => n.Contains("Hollowed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n == "Wall - Plain (2).stl" && names.Count(x => x == n) > 1);
    }

    [Fact]
    public async Task The_sculpt_token_names_the_mini_a_file_holds()
    {
        // It rendered its "Unsorted" fallback for every file, which read as a
        // template that simply did not work.
        var id = await NewModel("Pack", "pack", "UD-001-Wall.stl", "UD-002-Door.stl");
        await _editor.SetDesignerAsync(id, "Dungeon Blocks");

        await using (var db = _factory.CreateDbContext())
        {
            var classifier = new VariantClassifier();
            var model = await db.Models.Include(m => m.Files).SingleAsync(m => m.Id == id);
            foreach (var file in model.Files) classifier.Apply(model, file);
            await db.SaveChangesAsync();
        }

        var plan = await Plan(new OrganizeRules
        {
            FolderTemplate = PerModel,
            RenameFiles = true, FileTemplate = "{sculpt}",
        });
        var names = plan.Moves.SelectMany(m => m.Renames).Select(r => r.To).ToList();

        Assert.NotEmpty(names);
        Assert.DoesNotContain(names, n => n.StartsWith("Unsorted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_companion_takes_the_variant_of_the_mesh_it_sits_beside()
    {
        // The Lychee project of a hollowed mesh was rendering as "Plain" —
        // stating the opposite of what the file next to it says, and the
        // opposite of what the folder it lands in says too.
        var id = await NewModel("Wall", "pack", "UD-001-HOL-Wall.stl", "UD-001-HOL-Wall.lys");
        await _editor.SetDesignerAsync(id, "Dungeon Blocks");

        await using (var db = _factory.CreateDbContext())
        {
            var classifier = new VariantClassifier();
            var model = await db.Models.Include(m => m.Files).SingleAsync(m => m.Id == id);

            // What a scan does: the .lys is not a mesh, so it is left unkeyed.
            foreach (var file in model.Files)
            {
                file.Kind = file.Extension == ".lys" ? FileKind.Other : FileKind.Mesh;
                classifier.Apply(model, file);
            }
            await db.SaveChangesAsync();

            Assert.Null(model.Files.Single(f => f.Extension == ".lys").VariantLabel);
        }

        var plan = await Plan(new OrganizeRules
        {
            FolderTemplate = PerModel,
            RenameFiles = true, FileTemplate = "{model}-{variant}", FileCase = NameCase.Kebab,
        });
        var names = plan.Moves.SelectMany(m => m.Renames).Select(r => r.To).ToList();

        Assert.Contains("wall-hollowed.lys", names);
        Assert.DoesNotContain("wall-plain.lys", names);
    }

    [Fact]
    public async Task Tidying_the_original_names_loses_nothing()
    {
        // {file} in kebab-case is only a change of case. The creator already
        // encoded the variant in the name -- HOL, SUP, NL, and nothing at all
        // for the plain one -- so nothing has to be reconstructed from
        // MeshVault's own classification, and nothing can be lost if that
        // classification is wrong.
        var id = await NewModel("UD 067 Hole Trap", "pack",
            "UD-067-HOL-Hole Trap.stl", "UD-067-SUP-Hole Trap.stl",
            "UD-067-NL-Hole Trap.stl", "UD-067-Hole Trap.stl");
        await _editor.SetDesignerAsync(id, "Dungeon Blocks");
        var collection = await _editor.CreateCollectionAsync("The Ultimate Dungeon");
        await _editor.SetCollectionMembershipAsync(id, collection.Id, true);

        await using (var db = _factory.CreateDbContext())
        {
            var classifier = new VariantClassifier();
            var model = await db.Models.Include(m => m.Files).SingleAsync(m => m.Id == id);
            foreach (var f in model.Files) classifier.Apply(model, f);
            await db.SaveChangesAsync();
        }

        var rules = new OrganizeRules
        {
            FolderTemplate = "{designer}/{collection}/{sculpt}",
            RenameFiles = true,
            FileTemplate = "{file}",
            FileCase = NameCase.Kebab,
        };

        var paths = (await Plan(rules))
            .Moves.SelectMany(m => m.Renames.Select(r => $"{m.To}/{r.To}"))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
        [
            "Dungeon Blocks/The Ultimate Dungeon/UD 067 Hole Trap/ud-067-hol-hole-trap.stl",
            "Dungeon Blocks/The Ultimate Dungeon/UD 067 Hole Trap/ud-067-hole-trap.stl",
            "Dungeon Blocks/The Ultimate Dungeon/UD 067 Hole Trap/ud-067-nl-hole-trap.stl",
            "Dungeon Blocks/The Ultimate Dungeon/UD 067 Hole Trap/ud-067-sup-hole-trap.stl",
        ], paths);

        // Four distinct names out of four files, with no numbering. Renaming to
        // a scheme that has to invent the distinguishing part is where "(2)"
        // comes from; keeping the original name cannot collide, because those
        // names were already unique on disk.
        Assert.Equal(4, paths.Distinct().Count());
        Assert.DoesNotContain(paths, p => p.Contains("-2."));
    }

    [Fact]
    public async Task Tidying_the_original_names_settles_after_one_pass()
    {
        // A name already in the convention renders to itself, so there is
        // nothing left to do on a second run. A scheme that kept finding work
        // would be one you could never finish applying.
        var id = await NewModel("Hole Trap", "Dungeon Blocks/Hole Trap", "ud-067-hol-hole-trap.stl");
        await _editor.SetDesignerAsync(id, "Dungeon Blocks");

        var plan = await Plan(new OrganizeRules
        {
            FolderTemplate = "{designer}/{model}",
            RenameFiles = true,
            FileTemplate = "{file}",
            FileCase = NameCase.Kebab,
        });

        Assert.Equal(0, plan.Renames);
    }

    /// <summary>Applies the classifier the way a scan would.</summary>
    private async Task ClassifyAsync(int modelId)
    {
        await using var db = _factory.CreateDbContext();
        var classifier = new VariantClassifier();
        var model = await db.Models.Include(m => m.Files).SingleAsync(m => m.Id == modelId);
        foreach (var file in model.Files) classifier.Apply(model, file);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task An_unmarked_file_is_not_labelled_plain()
    {
        // "otto-bismark-plain" tells you nothing "otto-bismark" does not, and
        // a suffix distinguishing a file from nothing is noise on every name
        // in the library.
        var id = await NewModel("Otto Bismark", "otto", "Otto Bismark.stl");
        await _editor.SetDesignerAsync(id, "Logan Lalich");
        await ClassifyAsync(id);

        var plan = await Plan(new OrganizeRules
        {
            FolderTemplate = PerModel,
            RenameFiles = true, FileTemplate = "{sculpt}-{variant}", FileCase = NameCase.Kebab,
        });

        Assert.Equal("otto-bismark.stl", Assert.Single(plan.Moves[0].Renames).To);
    }

    [Fact]
    public async Task A_marked_file_says_so_even_as_the_only_copy_you_own()
    {
        // The variant is information about the file, not merely a way of
        // telling it from a sibling. Owning only the hollowed cut does not
        // make "hollowed" worth dropping.
        var id = await NewModel("Wall", "wall", "UD-001-HOL-Wall.stl");
        await _editor.SetDesignerAsync(id, "Dungeon Blocks");
        await ClassifyAsync(id);

        var plan = await Plan(new OrganizeRules
        {
            FolderTemplate = PerModel,
            RenameFiles = true, FileTemplate = "{sculpt}-{variant}", FileCase = NameCase.Kebab,
        });

        Assert.Contains("hollowed", Assert.Single(plan.Moves[0].Renames).To);
    }

    [Fact]
    public async Task A_marked_and_an_unmarked_export_still_get_different_names()
    {
        // The suffix has to go only where it says nothing. Dropping it from
        // both would collapse the pair and number one of them.
        var id = await NewModel("Well", "well", "UD-015-SUP-Well.stl", "UD-015-Well.stl");
        await _editor.SetDesignerAsync(id, "Dungeon Blocks");
        await ClassifyAsync(id);

        var plan = await Plan(new OrganizeRules
        {
            FolderTemplate = PerModel,
            RenameFiles = true, FileTemplate = "{sculpt}-{variant}", FileCase = NameCase.Kebab,
        });
        var names = plan.Moves.SelectMany(m => m.Renames).Select(r => r.To).ToList();

        Assert.Equal(2, names.Count);
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.DoesNotContain(names, n => n.Contains("-2."));
    }

    [Fact]
    public async Task Two_models_merging_do_not_strand_a_file_over_a_name()
    {
        // {sculpt} exists to bring separate folders holding one mini together.
        // When they merge, their file names are computed a model at a time, so
        // two files can arrive under the same name each knowing nothing of the
        // other -- and one is left behind rather than numbered.
        var a = await NewModel("IS 045 Tunnel Corner", "a", "IS-045-SUP-Tunnel-Corner.stl");
        var b = await NewModel("IS 045 Tunnel Corner", "b", "IS-045-Tunnel-Corner.stl");
        await _editor.SetDesignerAsync(a, "Dungeon Blocks");
        await _editor.SetDesignerAsync(b, "Dungeon Blocks");
        await ClassifyAsync(a);
        await ClassifyAsync(b);

        // Different lengths, so they are certainly not copies of each other.
        await using (var db = _factory.CreateDbContext())
        {
            var files = await db.Files.OrderBy(f => f.Id).ToListAsync();
            files[0].SizeBytes = 100;
            files[1].SizeBytes = 200;
            await db.SaveChangesAsync();
        }

        var plan = await Plan(new OrganizeRules
        {
            FolderTemplate = "{designer}/{sculpt}",
            RenameFiles = true,
            FileTemplate = "{model}",
            FileCase = NameCase.Kebab,
        });

        var names = plan.Moves.SelectMany(m => m.Renames).Select(r => r.To).ToList();

        Assert.Empty(plan.Conflicts);
        Assert.Equal(2, names.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task A_numbered_file_says_which_token_would_have_named_it()
    {
        // The live case: the plain and supported cuts of one mini, filed under
        // {model}, both wanting one name. Numbering keeps both files, but only
        // {variant} tells anyone which is which.
        var a = await NewModel("IS 045 Tunnel Corner", "a", "IS-045-SUP-Tunnel-Corner.stl");
        var b = await NewModel("IS 045 Tunnel Corner", "b", "IS-045-Tunnel-Corner.stl");
        await _editor.SetDesignerAsync(a, "Dungeon Blocks");
        await _editor.SetDesignerAsync(b, "Dungeon Blocks");
        await ClassifyAsync(a);
        await ClassifyAsync(b);

        await using (var db = _factory.CreateDbContext())
        {
            var files = await db.Files.OrderBy(f => f.Id).ToListAsync();
            files[0].SizeBytes = 100;
            files[1].SizeBytes = 200;
            await db.SaveChangesAsync();
        }

        var plan = await Plan(new OrganizeRules
        {
            FolderTemplate = "{designer}/{sculpt}",
            RenameFiles = true,
            FileTemplate = "{model}",
            FileCase = NameCase.Kebab,
        });

        var numbering = Assert.Single(plan.Numberings);
        Assert.Equal("variant", numbering.Distinguisher);
        Assert.Equal(("variant", 1), Assert.Single(plan.NumberingFixes));
    }

    [Fact]
    public async Task Naming_by_variant_leaves_nothing_to_warn_about()
    {
        // The warning has to go away when its advice is taken, or it is noise.
        var a = await NewModel("IS 045 Tunnel Corner", "a", "IS-045-SUP-Tunnel-Corner.stl");
        var b = await NewModel("IS 045 Tunnel Corner", "b", "IS-045-Tunnel-Corner.stl");
        await _editor.SetDesignerAsync(a, "Dungeon Blocks");
        await _editor.SetDesignerAsync(b, "Dungeon Blocks");
        await ClassifyAsync(a);
        await ClassifyAsync(b);

        var plan = await Plan(new OrganizeRules
        {
            FolderTemplate = "{designer}/{sculpt}",
            RenameFiles = true,
            FileTemplate = "{model}-{variant}",
            FileCase = NameCase.Kebab,
        });

        Assert.Empty(plan.Numberings);
        Assert.Empty(plan.Conflicts);

        var names = plan.Moves.SelectMany(m => m.Renames).Select(r => r.To).ToList();
        Assert.Contains("is-045-tunnel-corner.stl", names);
        Assert.Contains("is-045-tunnel-corner-supported.stl", names);
    }

    [Fact]
    public async Task Files_alike_in_everything_known_get_no_advice()
    {
        // Two unmarked meshes of one mini. A number is the only honest answer,
        // and suggesting a token that would not help would be worse than
        // saying nothing.
        var id = await NewModel("Dragon", "dragon", "a.stl", "b.stl");
        await _editor.SetDesignerAsync(id, "Cinderwing3D");

        var plan = await Plan(new OrganizeRules
        {
            FolderTemplate = PerModel,
            RenameFiles = true, FileTemplate = "{model}", FileCase = NameCase.Kebab,
        });

        Assert.Empty(plan.NumberingFixes);
    }

    [Fact]
    public async Task Numbering_inside_one_model_is_warned_about_too()
    {
        // The commoner shape by far: one mini, several cuts, filed under
        // {model}. PlanRenames numbers these, and for a while only clashes
        // *between* models were reported -- so the warning would have been
        // silent for most of a library.
        var id = await NewModel("Wall", "wall",
            "UD-001-HOL-Wall.stl", "UD-001-SUP-Wall.stl", "UD-001-Wall.stl");
        await _editor.SetDesignerAsync(id, "Dungeon Blocks");
        await ClassifyAsync(id);

        var plan = await Plan(new OrganizeRules
        {
            FolderTemplate = PerModel,
            RenameFiles = true, FileTemplate = "{model}", FileCase = NameCase.Kebab,
        });

        Assert.Equal(2, plan.Numberings.Count);
        Assert.Equal(("variant", 2), Assert.Single(plan.NumberingFixes));
    }

    [Fact]
    public async Task A_mesh_whose_name_says_only_its_variant_is_not_filed_under_a_placeholder()
    {
        // The shape the inbox bug arrived in. "presupported.stl" names a
        // flavour and never a mini, and {sculpt} used to render its "Unsorted"
        // placeholder for it -- shelving real work somewhere that reads as
        // filed. Worse, the sculpt name was borrowed from the containing
        // folder, so a loose download landed under a mini called "inbox".
        var id = await NewModel("inbox", "inbox", "presupported.stl");
        await _editor.SetDesignerAsync(id, "Dungeon Blocks");
        await ClassifyAsync(id);

        var plan = await Plan(new OrganizeRules { FolderTemplate = "{designer}/{sculpt}" });
        var move = Assert.Single(plan.Moves);

        Assert.Equal(MoveOutcome.Incomplete, move.Outcome);
        Assert.Contains("presupported.stl", move.Problem);
        Assert.DoesNotContain(plan.Moves, m => m.To.Contains("Unsorted", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Moves, m => m.To.Contains("inbox", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_named_mini_beside_an_unnamed_one_is_still_filed()
    {
        // Holding back the whole folder because one file needs a name would
        // punish the ninety-seven that do not. The unnamed one stays exactly
        // where it is, and says so.
        var id = await NewModel("Pack", "pack", "UD-001-Wall.stl", "presupported.stl");
        await _editor.SetDesignerAsync(id, "Dungeon Blocks");
        await ClassifyAsync(id);

        var plan = await Plan(new OrganizeRules { FolderTemplate = "{designer}/{sculpt}" });

        Assert.Contains(plan.Moves, m => m.To == "Dungeon Blocks/UD 001 Wall");
        Assert.Contains(plan.Moves, m => m.Outcome == MoveOutcome.Incomplete);

        // And it does not hitch a ride as an orphan of the mini that did file.
        var filed = plan.Moves.Single(m => m.Outcome != MoveOutcome.Incomplete);
        var carried = await FileNamesAsync(filed.FileIds);
        Assert.DoesNotContain("presupported.stl", carried);
    }

    [Fact]
    public async Task The_plan_says_which_token_produced_which_part_of_a_path()
    {
        // "Can't tell why it chose that path" was the complaint, and the page
        // genuinely could not answer it: a rendered destination looks the same
        // whichever token filled each segment.
        var id = await NewModel("Wall", "wall", "UD-001-Wall.stl");
        await _editor.SetDesignerAsync(id, "Dungeon Blocks");
        await ClassifyAsync(id);

        var move = Assert.Single(await Plan(new OrganizeRules
        {
            FolderTemplate = "{designer}/{collection}/{sculpt}",
        }) is var plan ? plan.Moves : []);

        Assert.Equal(
            [("designer", "Dungeon Blocks"), ("collection", null), ("sculpt", "UD 001 Wall")],
            move.Tokens.Select(t => (t.Name, t.Value)));
    }

    [Fact]
    public async Task A_model_files_under_the_collection_it_has_starred()
    {
        // Not the first alphabetically, which is what this used to be. "Archive"
        // beats "The Ultimate Dungeon" on the alphabet and is plainly not the
        // one anybody organises by.
        var id = await NewModel("Wall", "wall", "wall.stl");
        await _editor.SetDesignerAsync(id, "Dungeon Blocks");

        var dungeon = await _editor.CreateCollectionAsync("The Ultimate Dungeon");
        var archive = await _editor.CreateCollectionAsync("Archive");
        await _editor.SetCollectionMembershipAsync(id, dungeon.Id, true);
        await _editor.SetCollectionMembershipAsync(id, archive.Id, true);

        var move = Assert.Single((await Plan(new OrganizeRules
        {
            FolderTemplate = "{designer}/{collection}/{model}",
        })).Moves);

        Assert.Equal("Dungeon Blocks/The Ultimate Dungeon/Wall", move.To);
    }

    [Fact]
    public async Task A_model_in_no_collection_loses_the_level_rather_than_gaining_Unfiled()
    {
        // Most of a library is in no collection, and shelving all of it under a
        // folder named after an absence is worse than not having the level.
        var id = await NewModel("Wall", "wall", "wall.stl");
        await _editor.SetDesignerAsync(id, "Dungeon Blocks");

        var move = Assert.Single((await Plan(new OrganizeRules
        {
            FolderTemplate = "{designer}/{collection}/{model}",
        })).Moves);

        Assert.Equal("Dungeon Blocks/Wall", move.To);
    }

    [Fact]
    public async Task A_model_with_several_collections_and_no_star_loses_the_level_too()
    {
        // Unstarring is how a model opts out of being filed by collection, so
        // it has to land where an uncollected one does rather than guessing.
        var id = await NewModel("Wall", "wall", "wall.stl");
        await _editor.SetDesignerAsync(id, "Dungeon Blocks");

        var dungeon = await _editor.CreateCollectionAsync("The Ultimate Dungeon");
        var archive = await _editor.CreateCollectionAsync("Archive");
        await _editor.SetCollectionMembershipAsync(id, dungeon.Id, true);
        await _editor.SetCollectionMembershipAsync(id, archive.Id, true);
        await _editor.SetPrimaryCollectionAsync(id, null);

        var move = Assert.Single((await Plan(new OrganizeRules
        {
            FolderTemplate = "{designer}/{collection}/{model}",
        })).Moves);

        Assert.Equal("Dungeon Blocks/Wall", move.To);
    }

    [Fact]
    public async Task The_shipped_default_files_a_pack_by_sculpt()
    {
        // The default used to be {designer}/{model}, which has no {sculpt} in
        // it -- so a library that had never had a template chosen never split a
        // pack at all. A folder called "Orc Warband" holding three minis stayed
        // one folder called "Orc Warband", which is the exact shape organizing
        // exists to undo.
        var id = await NewModel("Orc Warband", "Orc Warband",
            "orc-chief.stl", "orc-grunt.stl", "orc-grunt-supported.stl", "orc-shaman.stl");
        await _editor.SetDesignerAsync(id, "Dungeon Blocks");
        await ClassifyAsync(id);

        var warband = await _editor.CreateCollectionAsync("Orc Warband");
        await _editor.SetCollectionMembershipAsync(id, warband.Id, true);

        // No rules passed: this is what a library with no template chosen gets.
        var plan = await _planner.PlanAsync(1, new OrganizeRules());

        Assert.Equal(
            [
                "Dungeon Blocks/Orc Warband/orc chief",
                "Dungeon Blocks/Orc Warband/orc grunt",
                "Dungeon Blocks/Orc Warband/orc shaman",
            ],
            plan.Moves.Select(m => m.To).Order());
    }

    [Fact]
    public async Task A_rebuilt_name_separates_the_sculpt_from_its_variants()
    {
        // "wall-no-logo" cannot say whether the sculpt is "Wall" or "Wall No".
        // Two dashes can, and they have to survive the casing pass, which
        // treats every other non-alphanumeric character as a word break.
        var id = await NewModel("Wall", "wall",
            "UD-001-Wall.stl", "UD-001-SUP-Wall-NL.stl", "UD-001-HOL-SUP-Wall.stl");
        await _editor.SetDesignerAsync(id, "Dungeon Blocks");
        await ClassifyAsync(id);

        var plan = await Plan(new OrganizeRules
        {
            FolderTemplate = PerModel,
            RenameFiles = true,
            FileTemplate = $"{{sculpt}}{NameCasing.VariantSeparator}{{variant}}",
            FileCase = NameCase.Kebab,
        });

        Assert.Equal(
            [
                // Alphabetical, not by rank, so tuning which export previews
                // best cannot reorder the words in a filename.
                "ud-001-wall--hollowed-supported.stl",
                "ud-001-wall--no-logo-supported.stl",

                // No variants, so no dangling separator: Sanitize trims it.
                "ud-001-wall.stl",
            ],
            plan.Moves.SelectMany(m => m.Renames).Select(r => r.To).Order());
    }

    [Fact]
    public async Task Rebuilt_names_keep_the_separator_under_snake_case()
    {
        // The reason for two dashes rather than the underscore that reads more
        // naturally: snake_case spends underscores on every word break, so a
        // single one would vanish into the name it was meant to divide.
        var id = await NewModel("Wall", "wall", "UD-001-HOL-Wall.stl");
        await ClassifyAsync(id);

        var plan = await Plan(new OrganizeRules
        {
            FolderTemplate = PerModel,
            RenameFiles = true,
            FileTemplate = $"{{sculpt}}{NameCasing.VariantSeparator}{{variant}}",
            FileCase = NameCase.Snake,
        });

        Assert.Equal("ud_001_wall--hollowed.stl",
            Assert.Single(plan.Moves.SelectMany(m => m.Renames)).To);
    }

    [Fact]
    public async Task Two_folders_of_one_sculpt_say_which_cut_each_is_carrying()
    {
        // Both rows are the same sculpt heading for nearly the same path, so
        // the destination alone cannot tell them apart -- the only clue used to
        // be whether the folder being left happened to be named after what was
        // in it, which is luck rather than information.
        var plain = await NewModel("Otto Bismark", "Otto Bismark", "otto-bismark.stl");
        var supported = await NewModel(
            "Otto Bismark supported", "Otto Bismark supported", "otto-bismark-supported.stl");

        await _editor.SetDesignerAsync(plain, "Loubie");
        await _editor.SetDesignerAsync(supported, "Loubie");
        await ClassifyAsync(plain);
        await ClassifyAsync(supported);

        var plan = await Plan(new OrganizeRules { FolderTemplate = "{designer}/{sculpt}" });

        Assert.Equal(["Plain"], plan.Moves.Single(m => m.From == "Otto Bismark").Variants);
        Assert.Equal(["Supported"],
            plan.Moves.Single(m => m.From == "Otto Bismark supported").Variants);
    }

    [Fact]
    public async Task Two_rows_landing_on_one_folder_name_say_what_lands_in_it()
    {
        // A destination is a folder and a folder is a sculpt, so two cuts of one
        // mini rendering the same path is the point rather than a clash -- and
        // it reads as a clash until the names landing in it are on screen.
        var plain = await NewModel("Otto Bismark", "Otto Bismark", "otto-bismark.stl");
        var supported = await NewModel(
            "Otto Bismark supported", "Otto Bismark supported", "otto-bismark-supported.stl");

        await _editor.SetDesignerAsync(plain, "Loubie");
        await _editor.SetDesignerAsync(supported, "Loubie");
        await ClassifyAsync(plain);
        await ClassifyAsync(supported);

        var plan = await Plan(new OrganizeRules { FolderTemplate = "{designer}/{sculpt}" });

        // The same folder, deliberately: one sculpt, one folder.
        Assert.Equal(["Loubie/otto bismark"], plan.Moves.Select(m => m.To).Distinct());

        // And the two files that keep them apart inside it.
        Assert.Equal(["otto-bismark.stl"],
            plan.Moves.Single(m => m.From == "Otto Bismark").Landing);
        Assert.Equal(["otto-bismark-supported.stl"],
            plan.Moves.Single(m => m.From == "Otto Bismark supported").Landing);
    }

    [Fact]
    public async Task A_row_carrying_several_cuts_lists_them_plain_first()
    {
        var id = await NewModel("Wall", "wall",
            "UD-001-Wall.stl", "UD-001-SUP-Wall.stl", "UD-001-HOL-Wall.stl");
        await ClassifyAsync(id);

        var move = Assert.Single((await Plan(
            new OrganizeRules { FolderTemplate = "{designer}/{sculpt}" })).Moves);

        // Plain leads because it is the mini rather than a cut of it; the rest
        // read alphabetically for the same reason the labels do.
        Assert.Equal(["Plain", "Hollowed", "Supported"], move.Variants);
    }

    [Fact]
    public async Task A_packs_readme_follows_the_pack_rather_than_stranding_its_folder()
    {
        // Splitting a pack used to leave its readme exactly where it was, and
        // so left the pack folder standing with nothing but that readme in it.
        // Browse then showed a model holding no models, beside the three
        // sculpts that had just come out of it -- and a scan would never have
        // made that row, because a folder becomes a model by holding a mesh.
        var id = await NewModel("Orc Warband", "Orc Warband",
            "orc-chief.stl", "orc-grunt.stl", "orc-shaman.stl", "readme.txt");
        await _editor.SetDesignerAsync(id, "Dungeon Blocks");
        await ClassifyAsync(id);

        var warband = await _editor.CreateCollectionAsync("Orc Warband");
        await _editor.SetCollectionMembershipAsync(id, warband.Id, true);

        var plan = await Plan(new OrganizeRules
        {
            FolderTemplate = "{designer}/{collection}/{sculpt}",
        });

        // The folder every mini from this pack now shares, which is what the
        // readme was describing.
        var carried = plan.Moves.Single(m => m.Landing.Contains("readme.txt"));
        Assert.Equal("Dungeon Blocks/Orc Warband", carried.To);

        // And nothing is left behind for it to strand.
        Assert.Equal(4, plan.Moves.Sum(m => m.FileIds.Count));
    }

    [Fact]
    public async Task A_readme_stays_put_when_there_is_no_shared_folder_to_send_it_to()
    {
        // A template of nothing but {sculpt} leaves the library root as the only
        // folder the minis share, and the root is not an answer -- dropping a
        // readme there would scatter every pack's paperwork into one heap.
        var id = await NewModel("Orc Warband", "Orc Warband",
            "orc-chief.stl", "orc-grunt.stl", "readme.txt");
        await ClassifyAsync(id);

        var plan = await Plan(new OrganizeRules { FolderTemplate = "{sculpt}" });

        Assert.DoesNotContain(plan.Moves, m => m.Landing.Contains("readme.txt"));
    }

    /// <summary>The names of the files a move would carry.</summary>
    private async Task<List<string>> FileNamesAsync(IReadOnlyList<int> fileIds)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Files.Where(f => fileIds.Contains(f.Id)).Select(f => f.FileName).ToListAsync();
    }

    public void Dispose() => _conn.Dispose();
}
