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

        var plan = await Plan(new OrganizeRules { RenameFiles = true });

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
            RenameFiles = true, FileTemplate = "{model}", FileCase = NameCase.Kebab,
        });

        Assert.Equal(2, plan.Numberings.Count);
        Assert.Equal(("variant", 2), Assert.Single(plan.NumberingFixes));
    }

    public void Dispose() => _conn.Dispose();
}
