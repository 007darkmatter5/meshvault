using MeshVault.Core.Models;
using MeshVault.Core.Services;
using MeshVault.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshVault.Tests;

public class LibraryIndexerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mv-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _conn = new("Filename=:memory:");

    public LibraryIndexerTests()
    {
        _conn.Open();
        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Libraries.Add(new Library { Name = "Test", Path = _root });
        db.SaveChanges();
    }

    private MeshVaultDbContext NewDb() => new(
        new DbContextOptionsBuilder<MeshVaultDbContext>().UseSqlite(_conn).Options);

    private LibraryIndexer NewIndexer(MeshVaultDbContext db) =>
        new(db, new FolderScanner(), NullLogger<LibraryIndexer>.Instance);

    private void File_(string relative, string content = "x")
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
    }

    private async Task<IndexResult> Index()
    {
        using var db = NewDb();
        return await NewIndexer(db).IndexAsync(1);
    }

    [Fact]
    public async Task First_index_adds_models_and_files()
    {
        File_("Dragon/dragon.stl");
        File_("Boat/benchy.3mf");

        var result = await Index();

        Assert.Equal(2, result.Added);
        using var db = NewDb();
        Assert.Equal(2, await db.Models.CountAsync());
        Assert.Equal(2, await db.Files.CountAsync());
    }

    [Fact]
    public async Task Rescan_with_no_changes_is_a_no_op()
    {
        File_("Dragon/dragon.stl");
        await Index();

        var result = await Index();

        Assert.Equal(new IndexResult(0, 0, 0), result);
    }

    [Fact]
    public async Task Rescan_preserves_tags_notes_and_favorites()
    {
        File_("Dragon/dragon.stl");
        await Index();

        using (var db = NewDb())
        {
            var model = await db.Models.FirstAsync();
            model.Tags.Add(new Tag { Name = "Minis", NormalizedName = "minis" });
            model.Notes = "Print at 0.12mm";
            model.Designer = new Designer { Name = "Loubie", NormalizedName = "loubie" };
            model.SourceUrl = "https://makerworld.com/models/1";
            model.SourceSite = "MakerWorld";
            model.License = "CC BY-NC 4.0";
            model.Favorites.Add(new ModelFavorite { UserId = Users.LocalUserId });
            model.Collections.Add(new Collection { Name = "To Print", OwnerId = Users.LocalUserId });
            await db.SaveChangesAsync();
        }

        // A new file arrives in the same folder, forcing an update of the entry.
        File_("Dragon/dragon_supported.stl");
        var result = await Index();

        Assert.Equal(1, result.Updated);
        using (var db = NewDb())
        {
            var model = await db.Models
                .Include(m => m.Tags).Include(m => m.Files).Include(m => m.Designer)
                .Include(m => m.Favorites).Include(m => m.Collections)
                .FirstAsync();

            Assert.Equal("Print at 0.12mm", model.Notes);
            Assert.Equal("Minis", Assert.Single(model.Tags).Name);
            Assert.Equal("Loubie", model.Designer?.Name);
            Assert.Equal("https://makerworld.com/models/1", model.SourceUrl);
            Assert.Equal("MakerWorld", model.SourceSite);
            Assert.Equal("CC BY-NC 4.0", model.License);
            Assert.Single(model.Favorites);
            Assert.Equal("To Print", Assert.Single(model.Collections).Name);
            Assert.Equal(2, model.Files.Count);
        }
    }

    [Fact]
    public async Task Deleted_folder_is_removed_with_its_files()
    {
        File_("Dragon/dragon.stl");
        File_("Boat/benchy.3mf");
        await Index();

        Directory.Delete(Path.Combine(_root, "Boat"), recursive: true);
        var result = await Index();

        Assert.Equal(1, result.Removed);
        using var db = NewDb();
        Assert.Equal("Dragon", (await db.Models.SingleAsync()).Name);
        Assert.Equal(1, await db.Files.CountAsync());
    }

    [Fact]
    public async Task Edited_file_invalidates_derived_data()
    {
        File_("Dragon/dragon.stl");
        await Index();

        using (var db = NewDb())
        {
            var file = await db.Files.FirstAsync();
            file.Sha256 = "cached";
            file.TriangleCount = 42;
            file.ThumbnailState = ThumbnailState.Ready;
            await db.SaveChangesAsync();
        }

        File_("Dragon/dragon.stl", "much longer content than before");
        await Index();

        using (var db = NewDb())
        {
            var file = await db.Files.FirstAsync();
            Assert.Null(file.Sha256);
            Assert.Null(file.TriangleCount);
            Assert.Equal(ThumbnailState.Pending, file.ThumbnailState);
        }
    }

    [Fact]
    public async Task Non_mesh_files_are_not_queued_for_thumbnails()
    {
        File_("Dragon/dragon.stl");
        File_("Dragon/readme.md");
        await Index();

        using var db = NewDb();
        Assert.Equal(ThumbnailState.Pending,
            (await db.Files.FirstAsync(f => f.Extension == ".stl")).ThumbnailState);
        Assert.Equal(ThumbnailState.NotApplicable,
            (await db.Files.FirstAsync(f => f.Extension == ".md")).ThumbnailState);
    }

    public void Dispose()
    {
        _conn.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
