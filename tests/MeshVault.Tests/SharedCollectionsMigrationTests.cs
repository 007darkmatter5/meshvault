using MeshVault.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MeshVault.Tests;

/// <summary>
/// The one migration in the app that merges rows rather than adding a column.
/// </summary>
/// <remarks>
/// Every other test here builds its schema with EnsureCreated, which skips
/// migrations entirely — so nothing would notice this one being wrong until it
/// ran against somebody's real library, once, irreversibly.
///
/// A file on disk rather than an in-memory connection, because the SQLite
/// provider rebuilds a table to drop a column and the rebuild is part of what
/// is being tested.
/// </remarks>
public class SharedCollectionsMigrationTests : IDisposable
{
    /// <summary>The schema as it stood before collections became shared.</summary>
    private const string Before = "20260904130838_PinVariantsOnOrganize";

    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"mv-mig-{Guid.NewGuid():N}.db");

    private MeshVaultDbContext NewDb() => new(
        new DbContextOptionsBuilder<MeshVaultDbContext>()
            .UseSqlite($"Data Source={_path}").Options);

    /// <summary>Runs migrations up to and including <paramref name="target"/>.</summary>
    private async Task MigrateToAsync(string? target = null)
    {
        await using var db = NewDb();
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    /// <summary>
    /// The shape the old schema allowed and the new one does not: two accounts
    /// each with a collection of the same name, holding overlapping models.
    /// </summary>
    private async Task SeedAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_path}");
        await connection.OpenAsync();

        async Task Run(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        // Only the columns that were NOT NULL from the start. Everything added
        // since carries a default, so naming them would just be a list to keep
        // in step with migrations this test does not care about.
        await Run("""
            INSERT INTO Libraries (Id, Name, Path, Enabled, AllowOrganize)
            VALUES (1, 'L', '/l', 1, 1);
            """);

        await Run("""
            INSERT INTO Models (Id, LibraryId, Name, RelativePath,
                                TotalBytes, AddedUtc, FileModifiedUtc)
            VALUES (1, 1, 'Wall', 'wall', 0, '2026-01-01', '2026-01-01'),
                   (2, 1, 'Door', 'door', 0, '2026-01-01', '2026-01-01');
            """);

        // "To Print" twice over, once per account, plus one collection that is
        // nobody's duplicate. Alice's is the lower id, so it is the survivor.
        await Run("""
            INSERT INTO Collections (Id, Name, NormalizedName, Description, OwnerId, CreatedUtc)
            VALUES (1, 'To Print', 'to print', NULL,        'alice', '2026-01-01'),
                   (2, 'To Print', 'to print', 'Bob''s notes', 'bob', '2026-01-01'),
                   (3, 'Terrain',  'terrain',  NULL,        'bob',   '2026-01-01');
            """);

        // Wall is in both copies of "To Print" — so the union must not try to
        // insert a link the survivor already has — and also in Terrain, which
        // gives it two collections and something to star.
        await Run("""
            INSERT INTO CollectionModelEntry (CollectionsId, ModelsId)
            VALUES (1, 1), (2, 1), (2, 2), (3, 1);
            """);
    }

    [Fact]
    public async Task Collections_of_the_same_name_merge_without_losing_a_model()
    {
        await MigrateToAsync(Before);
        await SeedAsync();
        await MigrateToAsync();

        await using var db = NewDb();

        // One "To Print" left, and it is Alice's row.
        var collections = await db.Collections.Include(c => c.Models)
            .OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(["To Print", "Terrain"], collections.Select(c => c.Name));

        // Bob's Door came across rather than going down with his duplicate,
        // and Wall was in both copies without producing a duplicate link.
        var toPrint = collections.Single(c => c.Name == "To Print");
        Assert.Equal([1, 2], toPrint.Models.Select(m => m.Id).Order());

        // The survivor had no description of its own, so it keeps the words
        // somebody actually wrote.
        Assert.Equal("Bob's notes", toPrint.Description);
    }

    [Fact]
    public async Task The_migration_stars_what_was_already_naming_each_folder()
    {
        await MigrateToAsync(Before);
        await SeedAsync();
        await MigrateToAsync();

        await using var db = NewDb();
        var models = await db.Models.Include(m => m.Collections).ToListAsync();

        // Wall ends up in two collections. {collection} used to resolve to the
        // first alphabetically — "Terrain" before "To Print" — so that is what
        // was naming its folder, and starring it is what keeps the folder still.
        var wall = models.Single(m => m.Name == "Wall");
        Assert.Equal("Terrain", wall.PrimaryCollection?.Name);

        // Door is in one collection, which is implicitly primary. Storing a
        // star for it would be a second value to keep in step for no gain.
        var door = models.Single(m => m.Name == "Door");
        Assert.Null(door.PrimaryCollectionId);
        Assert.Equal("To Print", door.PrimaryCollection?.Name);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }
}
