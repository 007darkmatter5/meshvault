using MeshVault.Core.Models;
using MeshVault.Core.Services;
using MeshVault.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshVault.Tests;

/// <summary>
/// Putting back a spelling the app's own renaming took away, out of the
/// organize history it kept anyway.
/// </summary>
public class SculptNameRestoreTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly ServiceProvider _services;
    private readonly VariantRules _rules = new();

    public SculptNameRestoreTests()
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

    private SculptNameRestorer NewRestorer() =>
        new(Factory, _rules, NullLogger<SculptNameRestorer>.Instance);

    /// <summary>
    /// A file as it stands after organizing and rescanning: named in kebab
    /// case, with a heading read back off that name.
    /// </summary>
    private ModelFile Organized(string name, string sculpt, bool setByHand = false) => new()
    {
        RelativePath = $"Dungeon/{name}",
        FileName = name,
        Extension = ".stl",
        Kind = FileKind.Mesh,
        SculptKey = VariantClassifier.NormalizeKey(sculpt),
        SculptName = sculpt,
        VariantSetByUser = setByHand,
    };

    /// <summary>Seeds a model, then records the name each file arrived with.</summary>
    private async Task SeedAsync(ModelFile file, string cameFrom)
    {
        await using var db = await Factory.CreateDbContextAsync();

        var model = new ModelEntry
        {
            LibraryId = 1,
            RelativePath = "Dungeon",
            Name = "Dungeon",
            Files = [file],
        };
        db.Models.Add(model);
        await db.SaveChangesAsync();

        db.OrganizeRuns.Add(new OrganizeRun
        {
            LibraryId = 1,
            RanUtc = DateTimeOffset.UtcNow,
            Steps = [new OrganizeStep { FileId = file.Id, From = cameFrom, To = file.RelativePath }],
        });
        await db.SaveChangesAsync();
    }

    private async Task<string?> SculptNameAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();
        return (await db.Files.AsNoTracking().FirstAsync()).SculptName;
    }

    [Fact]
    public async Task The_capitals_come_back_from_the_name_the_file_arrived_with()
    {
        await SeedAsync(
            Organized("ud-067-hol-hole-trap.stl", "ud 067 hole trap"),
            "Inbox/UD Pack/UD-067-HOL-Hole Trap.stl");

        var result = await NewRestorer().RestoreAsync();

        Assert.Equal(1, result.Restored);
        Assert.Equal("UD 067 Hole Trap", await SculptNameAsync());
    }

    [Fact]
    public async Task A_heading_that_now_says_something_else_is_left_alone()
    {
        // The guard that makes this safe to run on a library it has nothing to
        // fix. Only the capitals are ours to move; a name that reads
        // differently was changed by something this cannot second-guess.
        await SeedAsync(
            Organized("goblin-king.stl", "goblin king"),
            "Inbox/Goblin.stl");

        var result = await NewRestorer().RestoreAsync();

        Assert.Equal(0, result.Restored);
        Assert.Equal(1, result.Considered);
        Assert.Equal("goblin king", await SculptNameAsync());
    }

    [Fact]
    public async Task A_sculpt_set_by_hand_is_not_touched()
    {
        await SeedAsync(
            Organized("ud-067-hol-hole-trap.stl", "Hole Trap", setByHand: true),
            "Inbox/UD-067-HOL-Hole Trap.stl");

        Assert.Equal(0, (await NewRestorer().RestoreAsync()).Restored);
        Assert.Equal("Hole Trap", await SculptNameAsync());
    }

    [Fact]
    public async Task The_oldest_run_wins_when_a_file_was_organized_twice()
    {
        // Only the first run saw the name the creator gave it. A later run
        // moved a file that was already lowercased, and its record would put
        // back exactly what is there now.
        var file = Organized("ud-067-hol-hole-trap.stl", "ud 067 hole trap");
        await SeedAsync(file, "Inbox/UD-067-HOL-Hole Trap.stl");

        await using (var db = await Factory.CreateDbContextAsync())
        {
            db.OrganizeRuns.Add(new OrganizeRun
            {
                LibraryId = 1,
                RanUtc = DateTimeOffset.UtcNow,
                Steps =
                [
                    new OrganizeStep
                    {
                        FileId = file.Id,
                        From = "Packs/ud-067-hol-hole-trap.stl",
                        To = file.RelativePath,
                    },
                ],
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, (await NewRestorer().RestoreAsync()).Restored);
        Assert.Equal("UD 067 Hole Trap", await SculptNameAsync());
    }

    [Fact]
    public async Task Running_it_twice_changes_nothing_the_second_time()
    {
        await SeedAsync(
            Organized("ud-067-hol-hole-trap.stl", "ud 067 hole trap"),
            "Inbox/UD-067-HOL-Hole Trap.stl");

        Assert.Equal(1, (await NewRestorer().RestoreAsync()).Restored);
        Assert.Equal(0, (await NewRestorer().RestoreAsync()).Restored);
        Assert.Equal("UD 067 Hole Trap", await SculptNameAsync());
    }

    [Fact]
    public async Task A_library_that_was_never_organized_has_nothing_to_restore()
    {
        await using (var db = await Factory.CreateDbContextAsync())
        {
            db.Models.Add(new ModelEntry
            {
                LibraryId = 1,
                RelativePath = "Dungeon",
                Name = "Dungeon",
                Files = [Organized("Hole Trap.stl", "Hole Trap")],
            });
            await db.SaveChangesAsync();
        }

        var result = await NewRestorer().RestoreAsync();

        Assert.Equal(0, result.Restored);
        Assert.Equal(0, result.Considered);
    }

    public void Dispose()
    {
        _services.Dispose();
        _conn.Dispose();
        GC.SuppressFinalize(this);
    }
}
