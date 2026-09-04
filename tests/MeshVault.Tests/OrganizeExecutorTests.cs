using MeshVault.Core.Models;
using MeshVault.Core.Services;
using MeshVault.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshVault.Tests;

/// <summary>
/// Applying a plan. Unlike every other test here these touch a real temporary
/// folder, because the whole point of this class is what it does on disk — and
/// the thing most worth pinning is that the database keeps up with it.
/// </summary>
public class OrganizeExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mv-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly IDbContextFactory<MeshVaultDbContext> _factory;
    private readonly OrganizePlanner _planner;
    private readonly OrganizeExecutor _executor;
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

    public OrganizeExecutorTests()
    {
        _conn.Open();
        _factory = new Factory(_conn);
        Directory.CreateDirectory(_root);

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Libraries.Add(new Library
        {
            Name = "L", Path = _root, AllowOrganize = true, InboxPath = "inbox",
        });
        db.Designers.Add(new Designer { Name = "Dungeon Blocks", NormalizedName = "dungeon blocks" });
        db.SaveChanges();

        _planner = new OrganizePlanner(_factory, new FakeUser(), new VariantRules());
        _executor = new OrganizeExecutor(_factory, NullLogger<OrganizeExecutor>.Instance);
    }

    /// <summary>Writes real files and indexes them exactly as a scan would.</summary>
    private async Task<ModelEntry> NewModel(string relativePath, params string[] files)
    {
        var folder = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(folder);

        await using var db = _factory.CreateDbContext();
        var model = new ModelEntry
        {
            LibraryId = 1,
            Name = relativePath.Split('/')[^1],
            RelativePath = relativePath,
            DesignerId = 1,
            AddedUtc = DateTimeOffset.UtcNow,
        };

        foreach (var file in files)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, file), file);
            var extension = Path.GetExtension(file);
            model.Files.Add(new ModelFile
            {
                RelativePath = $"{relativePath}/{file}",
                FileName = file,
                Extension = extension,
                Kind = FileKinds.FromExtension(extension),
                SizeBytes = file.Length,
                ModifiedUtc = DateTimeOffset.UtcNow,
            });
        }

        db.Models.Add(model);
        await db.SaveChangesAsync();

        foreach (var file in model.Files) _classifier.Apply(model, file);
        await db.SaveChangesAsync();

        return model;
    }

    private async Task<OrganizeResult> Run(string template = "{designer}/{sculpt}")
    {
        var plan = await _planner.PlanAsync(1, new OrganizeRules { FolderTemplate = template });
        return await _executor.ApplyAsync(1, plan);
    }

    private bool Exists(string relative) =>
        File.Exists(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public async Task A_pack_becomes_one_folder_per_mini_on_disk()
    {
        await NewModel("inbox/UD-Supported",
            "UD-001-SUP-Wall.stl", "UD-002-SUP-Door.stl");

        var result = await Run();

        Assert.True(result.Clean, string.Join("; ", result.Problems));
        Assert.Equal(2, result.FilesMoved);

        Assert.True(Exists("Dungeon Blocks/UD 001 Wall/UD-001-SUP-Wall.stl"));
        Assert.True(Exists("Dungeon Blocks/UD 002 Door/UD-002-SUP-Door.stl"));
        Assert.False(Directory.Exists(Path.Combine(_root, "inbox", "UD-Supported")));
    }

    [Fact]
    public async Task Filing_everything_out_of_the_inbox_leaves_the_inbox_there()
    {
        // Emptying the inbox is what filing is for, and the husks inside it are
        // rubbish worth clearing. The inbox itself is not: it is a folder the
        // user configured and drops downloads into, and tidying it away meant
        // every successful organize ended with nowhere to put the next one.
        await NewModel("inbox/UD-Supported", "UD-001-SUP-Wall.stl");

        var result = await Run();

        Assert.True(result.Clean, string.Join("; ", result.Problems));
        Assert.True(Exists("Dungeon Blocks/UD 001 Wall/UD-001-SUP-Wall.stl"));

        // The husk goes, the inbox stays, and the inbox is empty.
        Assert.False(Directory.Exists(Path.Combine(_root, "inbox", "UD-Supported")));
        Assert.True(Directory.Exists(Path.Combine(_root, "inbox")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "inbox")));
    }

    [Fact]
    public async Task The_inbox_survives_being_spelled_differently_on_disk()
    {
        // The setting says "inbox" and the share may say "Inbox". Comparing
        // those as different folders is how the protection would quietly fail
        // on exactly the machines it matters on.
        await using (var db = _factory.CreateDbContext())
        {
            (await db.Libraries.SingleAsync()).InboxPath = "InBox";
            await db.SaveChangesAsync();
        }

        await NewModel("inbox/UD-Supported", "UD-001-SUP-Wall.stl");

        var result = await Run();

        Assert.True(result.Clean, string.Join("; ", result.Problems));
        Assert.True(Directory.Exists(Path.Combine(_root, "inbox")));
    }

    [Fact]
    public async Task Separate_variant_folders_end_up_in_one_folder()
    {
        await NewModel("inbox/plain", "Wall.stl");
        await NewModel("inbox/sup", "Wall_supported.stl");
        await NewModel("inbox/hol", "Wall_hollowed.stl");

        var result = await Run();

        Assert.True(result.Clean, string.Join("; ", result.Problems));
        Assert.True(Exists("Dungeon Blocks/Wall/Wall.stl"));
        Assert.True(Exists("Dungeon Blocks/Wall/Wall_supported.stl"));
        Assert.True(Exists("Dungeon Blocks/Wall/Wall_hollowed.stl"));

        // Three folders in, one model out.
        await using var db = _factory.CreateDbContext();
        var model = Assert.Single(await db.Models.Include(m => m.Files).ToListAsync());
        Assert.Equal("Dungeon Blocks/Wall", model.RelativePath);
        Assert.Equal(3, model.Files.Count);
    }

    [Fact]
    public async Task The_database_follows_the_files()
    {
        // The failure this guards against is silent and total: LibraryIndexer
        // reconciles on RelativePath, so a path left stale reads as a delete
        // plus an add on the next scan and takes the model's tags with it.
        await NewModel("inbox/pack", "Wall.stl", "Door.stl");

        await Run();

        await using var db = _factory.CreateDbContext();
        foreach (var file in await db.Files.ToListAsync())
        {
            Assert.StartsWith("Dungeon Blocks/", file.RelativePath);
            Assert.True(Exists(file.RelativePath), $"{file.RelativePath} is not on disk");
        }

        foreach (var model in await db.Models.ToListAsync())
        {
            var folder = Path.Combine(_root, model.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(Directory.Exists(folder), $"{model.RelativePath} is not on disk");
        }
    }

    [Fact]
    public async Task A_split_carries_the_packs_designer_and_tags_to_each_mini()
    {
        var pack = await NewModel("inbox/pack", "Wall.stl", "Door.stl");

        await using (var db = _factory.CreateDbContext())
        {
            var model = await db.Models.Include(m => m.Tags).FirstAsync(m => m.Id == pack.Id);
            model.Tags.Add(new Tag { Name = "terrain", NormalizedName = "terrain" });
            await db.SaveChangesAsync();
        }

        var result = await Run();

        Assert.Equal(1, result.ModelsCreated);

        await using var check = _factory.CreateDbContext();
        var minis = await check.Models.Include(m => m.Tags).ToListAsync();

        Assert.Equal(2, minis.Count);
        Assert.All(minis, m => Assert.Equal(1, m.DesignerId));
        Assert.All(minis, m => Assert.Contains(m.Tags, t => t.Name == "terrain"));
    }

    [Fact]
    public async Task A_plain_move_keeps_the_model_and_everything_on_it()
    {
        var model = await NewModel("inbox/Goblin", "Goblin.stl");

        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.Models.FirstAsync(m => m.Id == model.Id);
            row.Notes = "printed at 0.05";
            await db.SaveChangesAsync();
        }

        // No sculpt token, so the folder moves whole and the row should survive.
        var result = await Run("{designer}/{model}");

        Assert.Equal(0, result.ModelsCreated);

        await using var check = _factory.CreateDbContext();
        var after = Assert.Single(await check.Models.ToListAsync());
        Assert.Equal(model.Id, after.Id);
        Assert.Equal("printed at 0.05", after.Notes);
        Assert.Equal("Dungeon Blocks/Goblin", after.RelativePath);
    }

    [Fact]
    public async Task A_merge_keeps_the_tags_of_every_folder_it_swallows()
    {
        var a = await NewModel("inbox/plain", "Wall.stl");
        var b = await NewModel("inbox/sup", "Wall_supported.stl");

        await using (var db = _factory.CreateDbContext())
        {
            (await db.Models.Include(m => m.Tags).FirstAsync(m => m.Id == a.Id))
                .Tags.Add(new Tag { Name = "terrain", NormalizedName = "terrain" });
            (await db.Models.Include(m => m.Tags).FirstAsync(m => m.Id == b.Id))
                .Tags.Add(new Tag { Name = "dungeon", NormalizedName = "dungeon" });
            await db.SaveChangesAsync();
        }

        await Run();

        await using var check = _factory.CreateDbContext();
        var model = Assert.Single(await check.Models.Include(m => m.Tags).ToListAsync());

        Assert.Equal(2, model.Tags.Count);
    }

    [Fact]
    public async Task A_read_only_library_refuses()
    {
        await using (var db = _factory.CreateDbContext())
        {
            (await db.Libraries.FirstAsync()).AllowOrganize = false;
            await db.SaveChangesAsync();
        }

        await NewModel("inbox/pack", "Wall.stl");

        var plan = await _planner.PlanAsync(1, new OrganizeRules());
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _executor.ApplyAsync(1, plan));

        Assert.Contains("does not allow", refused.Message);
        Assert.True(Exists("inbox/pack/Wall.stl"));
    }

    [Fact]
    public async Task An_identical_copy_is_verified_then_dropped()
    {
        // The pack ships the same file in two variant folders. Both are heading
        // for the same name in the same place, and one of them is redundant.
        await NewModel("inbox/one", "Wall.stl");
        await NewModel("inbox/two", "Wall.stl");

        var result = await Run();

        Assert.Equal(1, result.FilesMoved);
        Assert.Equal(1, result.FilesDeleted);
        Assert.True(result.Clean, string.Join("; ", result.Problems));
        Assert.True(Exists("Dungeon Blocks/Wall/Wall.stl"));

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Files.CountAsync());
    }

    [Fact]
    public async Task A_hash_worked_out_once_is_kept()
    {
        // Reading a pair of large files over a share is minutes, so the answer
        // has to survive the run that paid for it.
        await NewModel("inbox/one", "Wall.stl");
        await NewModel("inbox/two", "Wall.stl");

        await Run();

        await using var db = _factory.CreateDbContext();
        var survivor = await db.Files.SingleAsync();

        Assert.False(string.IsNullOrEmpty(survivor.Sha256));
    }

    [Fact]
    public async Task A_stored_hash_is_used_instead_of_reading_again()
    {
        await NewModel("inbox/one", "Wall.stl");
        await NewModel("inbox/two", "Wall.stl");

        // Both already known, and deliberately agreeing with each other rather
        // than with what is on disk. Using them proves the file is not reread.
        await using (var db = _factory.CreateDbContext())
        {
            foreach (var row in await db.Files.ToListAsync()) row.Sha256 = "CAFE";
            await db.SaveChangesAsync();
        }

        var result = await Run();

        Assert.Equal(1, result.FilesDeleted);
        Assert.True(result.Clean, string.Join("; ", result.Problems));
    }

    [Fact]
    public async Task A_different_file_of_the_same_name_is_never_dropped()
    {
        // Same name, same length, different bytes — which is exactly the case a
        // size check alone would delete. The hash is what stops it.
        await NewModel("inbox/one", "Wall.stl");
        await NewModel("inbox/two", "Wall.stl");

        var other = Path.Combine(_root, "inbox", "two", "Wall.stl");
        await File.WriteAllTextAsync(other, new string('x', "Wall.stl".Length));

        var result = await Run();

        Assert.Equal(0, result.FilesDeleted);
        Assert.Single(result.Problems);
        Assert.Contains("not the same file", result.Problems[0]);

        // Both survive, and the one that could not move is where it always was.
        Assert.True(File.Exists(other), "a file that only looked like a copy was deleted");
        Assert.True(Exists("Dungeon Blocks/Wall/Wall.stl"));
    }

    [Fact]
    public async Task A_clash_of_different_lengths_is_reported_not_deleted()
    {
        await NewModel("inbox/one", "Wall.stl");
        await NewModel("inbox/two", "Wall.stl");

        var other = Path.Combine(_root, "inbox", "two", "Wall.stl");
        var content = "a quite different length of content";
        await File.WriteAllTextAsync(other, content);

        // The size the planner reads comes from the index, not the disk.
        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.Files.FirstAsync(f => f.RelativePath == "inbox/two/Wall.stl");
            row.SizeBytes = content.Length;
            await db.SaveChangesAsync();
        }

        var result = await Run();

        Assert.Equal(0, result.FilesDeleted);
        Assert.Contains(result.Problems, p => p.Contains("already claims that name"));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task Colliding_sidecars_are_removed_and_forgotten()
    {
        await NewModel("inbox/plain", "Wall.stl", "datapackage.json");
        await NewModel("inbox/sup", "Wall_supported.stl", "datapackage.json");

        var result = await Run();

        Assert.Equal(2, result.FilesDeleted);
        Assert.True(result.Clean, string.Join("; ", result.Problems));

        await using var db = _factory.CreateDbContext();
        Assert.Empty(await db.Files.Where(f => f.FileName == "datapackage.json").ToListAsync());
        Assert.False(Exists("Dungeon Blocks/Wall/datapackage.json"));
    }

    [Fact]
    public async Task A_file_that_could_not_move_keeps_a_model_that_contains_it()
    {
        // The model walks off to its new folder; a file blocked by a clash stays
        // behind. Left owned by a folder it is not in, the next scan would index
        // it as a brand new model and take none of the tags with it.
        await NewModel("inbox/one", "Wall.stl");
        await NewModel("inbox/two", "Wall.stl");

        var other = Path.Combine(_root, "inbox", "two", "Wall.stl");
        var content = "a quite different length of content";
        await File.WriteAllTextAsync(other, content);

        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.Files.FirstAsync(f => f.RelativePath == "inbox/two/Wall.stl");
            row.SizeBytes = content.Length;
            await db.SaveChangesAsync();
        }

        await Run();

        await using var check = _factory.CreateDbContext();
        var models = await check.Models.Include(m => m.Files).ToListAsync();

        foreach (var model in models)
        {
            foreach (var file in model.Files)
            {
                Assert.StartsWith($"{model.RelativePath}/", file.RelativePath);
            }
        }

        // The stranded one still belongs to something, and to something that
        // knows who made it.
        var stray = models.Single(m => m.Files.Any(f => f.RelativePath == "inbox/two/Wall.stl"));
        Assert.Equal(1, stray.DesignerId);
    }

    [Fact]
    public async Task A_folder_someone_else_left_something_in_is_not_swept_away()
    {
        await NewModel("inbox/pack", "Wall.stl");

        // Not indexed, so the plan knows nothing about it.
        await File.WriteAllTextAsync(Path.Combine(_root, "inbox", "pack", "notes.txt"), "mine");

        await Run();

        Assert.True(Exists("inbox/pack/notes.txt"));
        Assert.True(Directory.Exists(Path.Combine(_root, "inbox", "pack")));
    }

    [Fact]
    public async Task Applying_twice_changes_nothing_the_second_time()
    {
        await NewModel("inbox/pack", "Wall.stl", "Door.stl");
        await Run();

        var again = await Run();

        Assert.Equal(0, again.FilesMoved);
        Assert.True(again.Clean, string.Join("; ", again.Problems));
    }

    [Fact]
    public async Task Only_the_chosen_models_are_moved()
    {
        var wall = await NewModel("inbox/wall", "Wall.stl");
        await NewModel("inbox/door", "Door.stl");

        var plan = await _planner.PlanAsync(1, new OrganizeRules
        {
            FolderTemplate = "{designer}/{sculpt}",
        });

        var result = await _executor.ApplyAsync(1, plan.For(new HashSet<int> { wall.Id }));

        Assert.True(result.Clean, string.Join("; ", result.Problems));
        Assert.Equal(1, result.FilesMoved);

        Assert.True(Exists("Dungeon Blocks/Wall/Wall.stl"));
        Assert.True(Exists("inbox/door/Door.stl"));

        // The one left out keeps its row and its path, so the next scan still
        // recognises it rather than reading a delete plus an add.
        await using var db = _factory.CreateDbContext();
        var door = await db.Models.Include(m => m.Files)
            .SingleAsync(m => m.RelativePath == "inbox/door");
        Assert.Equal("inbox/door/Door.stl", Assert.Single(door.Files).RelativePath);
    }

    [Fact]
    public async Task Choosing_nothing_moves_nothing()
    {
        await NewModel("inbox/wall", "Wall.stl");

        var plan = await _planner.PlanAsync(1, new OrganizeRules
        {
            FolderTemplate = "{designer}/{sculpt}",
        });

        var result = await _executor.ApplyAsync(1, plan.For(new HashSet<int>()));

        Assert.Equal(0, result.FilesMoved);
        Assert.Equal(0, result.ModelsRemoved);
        Assert.True(Exists("inbox/wall/Wall.stl"));
    }

    [Fact]
    public async Task Renames_happen_even_when_the_folder_is_already_right()
    {
        // The plan showed "6 renamed" beside "already there" and then did
        // nothing, because the executor only walked rows whose outcome was
        // Move. A plan that promises and does not deliver is the one failure
        // this page cannot have.
        await NewModel("Dungeon Blocks/Wall", "Wall.stl");

        var plan = await _planner.PlanAsync(1, new OrganizeRules
        {
            FolderTemplate = "{designer}/{sculpt}",
            RenameFiles = true,
            FileTemplate = "{model} - {file}",
        });

        Assert.NotEmpty(plan.Moves.SelectMany(m => m.Renames));

        var result = await _executor.ApplyAsync(1, plan);

        Assert.True(result.Clean, string.Join("; ", result.Problems));
        Assert.True(Exists("Dungeon Blocks/Wall/Wall - Wall.stl"));
        Assert.False(Exists("Dungeon Blocks/Wall/Wall.stl"));
    }

    [Fact]
    public async Task A_case_only_rename_reaches_the_disk_and_not_just_the_database()
    {
        // Skipping this one on OrdinalIgnoreCase left the database saying
        // "wall.stl" while the disk still held "Wall.stl" — invisible on this
        // Windows box and a missing file on the Linux share it ships to.
        await NewModel("Dungeon Blocks/Wall", "Wall.stl");

        var plan = await _planner.PlanAsync(1, new OrganizeRules
        {
            FolderTemplate = "{designer}/{sculpt}",
            RenameFiles = true,
            FileTemplate = "{file}",
            FileCase = NameCase.Kebab,
        });

        await _executor.ApplyAsync(1, plan);

        var onDisk = Path.GetFileName(
            Directory.GetFiles(Path.Combine(_root, "Dungeon Blocks", "Wall"))[0]);

        await using var db = _factory.CreateDbContext();
        var recorded = (await db.Files.SingleAsync()).FileName;

        Assert.Equal("wall.stl", onDisk);
        Assert.Equal(onDisk, recorded);
    }

    [Fact]
    public async Task A_file_already_correctly_named_is_not_counted_as_moved()
    {
        // Two of six renamed is two things done, not six.
        await NewModel("Dungeon Blocks/Wall", "Wall.stl", "Wall_supported.stl");

        var plan = await _planner.PlanAsync(1, new OrganizeRules
        {
            FolderTemplate = "{designer}/{sculpt}",
            RenameFiles = true,
            FileTemplate = "{file}",
        });

        var result = await _executor.ApplyAsync(1, plan);

        Assert.Equal(0, result.FilesMoved);
        Assert.True(result.Clean, string.Join("; ", result.Problems));
    }

    [Fact]
    public async Task A_folder_whose_only_change_is_case_is_renamed_on_disk()
    {
        await NewModel("Dungeon Blocks/wall", "Wall.stl");

        // Say by hand that the sculpt is "Wall", so {sculpt} wants a capital.
        await using (var seed = _factory.CreateDbContext())
        {
            var file = await seed.Files.SingleAsync();
            file.SculptName = "Wall";
            file.SculptKey = VariantClassifier.NormalizeKey("Wall");
            file.VariantSetByUser = true;
            await seed.SaveChangesAsync();
        }

        var plan = await _planner.PlanAsync(1, new OrganizeRules
        {
            FolderTemplate = "{designer}/{sculpt}",
        });

        var result = await _executor.ApplyAsync(1, plan);
        Assert.True(result.Clean, string.Join("; ", result.Problems));

        var onDisk = Directory.GetDirectories(Path.Combine(_root, "Dungeon Blocks"))
            .Select(Path.GetFileName).ToList();

        Assert.Equal(["Wall"], onDisk);

        // What the mismatch would have cost. LibraryIndexer reconciles on
        // RelativePath with an ordinal comparer, so a folder left spelled the
        // old way reads as one model deleted and another added on the next
        // scan -- and the tags go with it.
        await using var db = _factory.CreateDbContext();
        var model = await db.Models.SingleAsync();

        Assert.Equal("Dungeon Blocks/Wall", model.RelativePath);
        Assert.True(
            Directory.Exists(Path.Combine(_root, model.RelativePath.Replace('/', Path.DirectorySeparatorChar))),
            "the database records a folder that is not the one on disk");

        var folders = Directory.GetDirectories(Path.Combine(_root, "Dungeon Blocks"));
        Assert.Single(folders);
    }


    [Fact]
    public async Task A_case_only_file_rename_is_not_refused_as_a_clash_with_itself()
    {
        // The live failure was "The file '.../alco.stl' already exists" for a
        // rename of Alco.stl to alco.stl -- a clash reported against the very
        // file being renamed. Windows renames case in place and hides it; a
        // case-insensitive mount under Linux does not.
        await NewModel("Dungeon Blocks/Alco", "Alco.stl");

        var plan = await _planner.PlanAsync(1, new OrganizeRules
        {
            FolderTemplate = "{designer}/{model}",
            RenameFiles = true,
            FileTemplate = "{file}",
            FileCase = NameCase.Kebab,
        });

        var result = await _executor.ApplyAsync(1, plan);

        Assert.True(result.Clean, string.Join("; ", result.Problems));
        Assert.DoesNotContain(result.Problems, p => p.Contains("already exists"));

        var onDisk = Directory.GetFiles(Path.Combine(_root, "Dungeon Blocks", "Alco"))
            .Select(Path.GetFileName).ToList();
        Assert.Equal(["alco.stl"], onDisk);

        await using var check = _factory.CreateDbContext();
        Assert.Equal("alco.stl", (await check.Files.SingleAsync()).FileName);
    }

    [Fact]
    public async Task Two_files_differing_only_by_case_are_still_a_real_clash()
    {
        // The safety the overwrite rests on: one entry means it is the file
        // itself, two means two files and nothing may be overwritten.
        var folder = Path.Combine(_root, "probe");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "a.stl"), "one");

        var method = typeof(OrganizeExecutor).GetMethod(
            "MoveFile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        // Renaming onto itself is allowed, and keeps the bytes.
        method!.Invoke(null, [Path.Combine(folder, "a.stl"), Path.Combine(folder, "A.stl")]);

        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(folder, "A.stl")));
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task Merging_models_both_keep_their_file_on_disk()
    {
        // Both folders hold the same mini, so {sculpt} brings them together.
        // Under {model} both files want one name, and the loser used to be
        // left behind in a folder the plan said would be emptied.
        await NewModel("inbox/a", "IS-045-SUP-Tunnel-Corner.stl");
        await NewModel("inbox/b", "IS-045-Tunnel-Corner.stl");

        await using (var seed = _factory.CreateDbContext())
        {
            foreach (var model in await seed.Models.Include(m => m.Files).ToListAsync())
            {
                model.Name = "IS 045 Tunnel Corner";
                foreach (var f in model.Files) f.SizeBytes = f.FileName.Length * 10;
            }
            await seed.SaveChangesAsync();
        }

        var plan = await _planner.PlanAsync(1, new OrganizeRules
        {
            FolderTemplate = "{designer}/{sculpt}",
            RenameFiles = true,
            FileTemplate = "{model}",
            FileCase = NameCase.Kebab,
        });

        Assert.Empty(plan.Conflicts);

        var result = await _executor.ApplyAsync(1, plan);
        Assert.True(result.Clean, string.Join("; ", result.Problems));

        var folder = Path.Combine(_root, "Dungeon Blocks", "IS 045 Tunnel Corner");
        var onDisk = Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n).ToList();

        Assert.Equal(2, onDisk.Count);
        Assert.Contains("is-045-tunnel-corner.stl", onDisk);
        Assert.Contains("is-045-tunnel-corner-2.stl", onDisk);

        // And the database says the same, or the next scan undoes it.
        await using var check = _factory.CreateDbContext();
        foreach (var file in await check.Files.ToListAsync())
            Assert.True(Exists(file.RelativePath), $"{file.RelativePath} is not on disk");
    }

    [Fact]
    public async Task A_rename_sweep_can_be_taken_back()
    {
        // The case worth having undo for: a template that turned out wrong,
        // applied across a library, with the originals the only record of what
        // the files were.
        await NewModel("Dungeon Blocks/Wall", "UD-001-HOL-Wall.stl", "UD-001-SUP-Wall.stl");

        var plan = await _planner.PlanAsync(1, new OrganizeRules
        {
            FolderTemplate = "{designer}/{model}",
            RenameFiles = true,
            FileTemplate = "{model}",
            FileCase = NameCase.Kebab,
        });

        var applied = await _executor.ApplyAsync(1, plan);
        Assert.True(applied.Clean, string.Join("; ", applied.Problems));

        var afterRun = Directory.GetFiles(Path.Combine(_root, "Dungeon Blocks", "Wall"))
            .Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(["wall-2.stl", "wall.stl"], afterRun);

        var undo = new OrganizeUndo(_factory, NullLogger<OrganizeUndo>.Instance);
        var last = await undo.LastAsync(1);

        Assert.NotNull(last);
        Assert.True(last!.FullyReversible);

        var result = await undo.UndoAsync(last.Id);
        Assert.True(result.Clean, string.Join("; ", result.Problems));
        Assert.Equal(2, result.FilesRestored);

        var afterUndo = Directory.GetFiles(Path.Combine(_root, "Dungeon Blocks", "Wall"))
            .Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(["UD-001-HOL-Wall.stl", "UD-001-SUP-Wall.stl"], afterUndo);

        // And the catalog agrees, or the next scan would undo the undo.
        await using var check = _factory.CreateDbContext();
        foreach (var file in await check.Files.ToListAsync())
            Assert.True(Exists(file.RelativePath), $"{file.RelativePath} is not on disk");
    }

    [Fact]
    public async Task A_run_already_taken_back_cannot_be_taken_back_again()
    {
        await NewModel("inbox/wall", "Wall.stl");
        var plan = await _planner.PlanAsync(1, new OrganizeRules
        {
            FolderTemplate = "{designer}/{sculpt}",
        });
        await _executor.ApplyAsync(1, plan);

        var undo = new OrganizeUndo(_factory, NullLogger<OrganizeUndo>.Instance);
        var last = await undo.LastAsync(1);
        await undo.UndoAsync(last!.Id);

        Assert.Null(await undo.LastAsync(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => undo.UndoAsync(last.Id));
    }

    [Fact]
    public async Task A_file_touched_since_the_run_is_left_alone()
    {
        // The record describes a library that no longer exists. Acting on it
        // would be guessing on somebody's behalf.
        await NewModel("Dungeon Blocks/Wall", "UD-001-Wall.stl");

        var plan = await _planner.PlanAsync(1, new OrganizeRules
        {
            FolderTemplate = "{designer}/{model}",
            RenameFiles = true, FileTemplate = "{model}", FileCase = NameCase.Kebab,
        });
        await _executor.ApplyAsync(1, plan);

        await using (var meddle = _factory.CreateDbContext())
        {
            var file = await meddle.Files.SingleAsync();
            file.RelativePath = "Dungeon Blocks/Wall/somebody-else-moved-me.stl";
            await meddle.SaveChangesAsync();
        }

        var undo = new OrganizeUndo(_factory, NullLogger<OrganizeUndo>.Instance);
        var result = await undo.UndoAsync((await undo.LastAsync(1))!.Id);

        Assert.Equal(0, result.FilesRestored);
        Assert.Equal(1, result.Skipped);
        Assert.Contains(result.Problems, p => p.Contains("changed since the run"));
    }

    [Fact]
    public async Task A_rescan_does_not_undo_what_organizing_wrote()
    {
        // Organizing destroys the evidence the classifier reads: the words in
        // the name, and the folders standing above the file inside its model.
        // Left unpinned, the next scan reads back a name the app wrote itself
        // and downgrades the row -- which is the whole reason
        // SculptNameRestorer had to exist.
        var model = await NewModel("inbox/UD-Supported", "UD-001-SUP-Wall.stl");
        var before = model.Files[0];
        Assert.Equal("Supported", before.VariantLabel);
        Assert.Equal("UD 001 Wall", before.SculptName);

        var plan = await _planner.PlanAsync(1, new OrganizeRules
        {
            FolderTemplate = "{designer}/{sculpt}",
            RenameFiles = true, FileTemplate = "{sculpt}", FileCase = NameCase.Kebab,
        });
        Assert.True((await _executor.ApplyAsync(1, plan)).Clean);

        // The name now says nothing about supports, and the "Supported" folder
        // it sat in is gone. Reading it again is exactly what a scan does.
        await using var db = _factory.CreateDbContext();
        var entry = await db.Models.Include(m => m.Files).SingleAsync();
        var file = entry.Files[0];

        Assert.Equal("ud-001-wall.stl", file.FileName);
        Assert.True(file.VariantSetByOrganize);

        Assert.False(_classifier.Apply(entry, file));
        Assert.Equal("Supported", file.VariantLabel);
        Assert.Equal("UD 001 Wall", file.SculptName);
    }

    [Fact]
    public async Task Asking_for_detection_back_clears_the_organize_pin()
    {
        // The pin is bookkeeping, not a decision anybody made, so it must never
        // be the reason a person cannot get detection back.
        await NewModel("inbox/UD-Supported", "UD-001-SUP-Wall.stl");
        Assert.True((await Run()).Clean);

        int id;
        await using (var db = _factory.CreateDbContext())
        {
            var file = await db.Files.SingleAsync();
            Assert.True(file.VariantSetByOrganize);
            id = file.Id;
        }

        await new ModelEditor(_factory, new FakeUser()).ResetVariantAsync(id);

        await using var after = _factory.CreateDbContext();
        Assert.False((await after.Files.SingleAsync()).VariantSetByOrganize);
    }

    public void Dispose()
    {
        _conn.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
