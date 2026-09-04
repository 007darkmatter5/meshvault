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
        new(db, new FolderScanner(), new VariantClassifier(), NullLogger<LibraryIndexer>.Instance);

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

    private async Task<IndexResult> IndexFolder(string subPath)
    {
        using var db = NewDb();
        return await NewIndexer(db).IndexFolderAsync(1, subPath);
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
    public async Task A_scan_puts_a_derived_name_back_in_step_with_its_folder()
    {
        // The name was read off the folder only when the row was inserted, so
        // it stopped tracking the moment anything else wrote it -- and nothing
        // put it right. Organizing did exactly that: two cuts of a mini merging
        // into "otto bismark" left the merged row still called "Otto Bismark
        // supported", and no amount of rescanning corrected it.
        File_("Dragon/dragon.stl");
        await Index();

        using (var db = NewDb())
        {
            var model = await db.Models.SingleAsync();
            model.Name = "Something Else";
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, (await Index()).Updated);

        using (var db = NewDb())
            Assert.Equal("Dragon", (await db.Models.SingleAsync()).Name);
    }

    [Fact]
    public async Task A_name_somebody_typed_survives_a_scan()
    {
        // The other half of the bargain, and the reason the flag exists.
        File_("Dragon/dragon.stl");
        await Index();

        using (var db = NewDb())
        {
            var model = await db.Models.SingleAsync();
            model.Name = "Spring Dragon";
            model.NameSetByUser = true;
            await db.SaveChangesAsync();
        }

        await Index();

        using (var db = NewDb())
            Assert.Equal("Spring Dragon", (await db.Models.SingleAsync()).Name);
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
            model.Collections.Add(new Collection { Name = "To Print", NormalizedName = "to print" });
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

    [Fact]
    public async Task Indexing_gathers_a_folder_of_exports_into_sculpts()
    {
        // The shape a bought pack actually arrives in: one folder, every mini
        // shipped twice, which used to read as four unrelated files.
        File_("Dungeon/Tavern_supported.stl");
        File_("Dungeon/Tavern_unsupported.stl");
        File_("Dungeon/Goblin_supported.stl");
        File_("Dungeon/Goblin_unsupported.stl");
        File_("Dungeon/readme.md");

        await using var db = NewDb();
        await NewIndexer(db).IndexAsync(1);

        var files = await db.Files.ToListAsync();
        var sculpts = files.Where(f => f.SculptKey is not null)
            .Select(f => f.SculptKey).Distinct().ToList();

        Assert.Equal(2, sculpts.Count);
        Assert.Contains("tavern", sculpts);
        Assert.Contains("goblin", sculpts);

        // The readme is not an export of anything.
        Assert.Null(files.Single(f => f.Extension == ".md").SculptKey);

        var tavern = files.Where(f => f.SculptKey == "tavern").ToList();
        Assert.Equal(
            ["Supported", "Unsupported"],
            tavern.Select(f => f.VariantLabel).Order().ToList());
    }

    [Fact]
    public async Task A_rescan_leaves_sculpt_grouping_alone()
    {
        File_("Dungeon/Tavern_supported.stl");
        File_("Dungeon/Tavern_unsupported.stl");

        await using (var first = NewDb())
            await NewIndexer(first).IndexAsync(1);

        await using var db = NewDb();
        await NewIndexer(db).IndexAsync(1);

        var files = await db.Files.ToListAsync();
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.Equal("tavern", f.SculptKey));
    }

    // Scanning one folder ---------------------------------------------------

    [Fact]
    public async Task An_inbox_scan_never_removes_the_rest_of_the_library()
    {
        // The one that matters. A full scan deletes every model it did not
        // see, which is how a folder deleted on the share is noticed. Applied
        // to a scan that only looked at the inbox, that reasoning would empty
        // the library in one click and without asking.
        File_("inbox/Goblin/goblin.stl");
        File_("Dragon/dragon.stl");
        File_("Boat/benchy.3mf");
        await Index();

        var result = await IndexFolder("inbox");

        Assert.Equal(0, result.Removed);
        using var db = NewDb();
        Assert.Equal(3, await db.Models.CountAsync());
    }

    [Fact]
    public async Task An_inbox_scan_finds_what_was_dropped_in_it()
    {
        File_("Dragon/dragon.stl");
        await Index();

        File_("inbox/Goblin/goblin.stl");
        var result = await IndexFolder("inbox");

        Assert.Equal(1, result.Added);
        using var db = NewDb();

        // Relative to the library root, not to the inbox. Reconciliation is
        // keyed on this, so an inbox-relative "Goblin" would stand beside the
        // real row for ever after.
        Assert.NotNull(await db.Models.FirstOrDefaultAsync(m => m.RelativePath == "inbox/Goblin"));
        Assert.Equal(2, await db.Models.CountAsync());
    }

    [Fact]
    public async Task Something_deleted_from_the_inbox_is_still_noticed()
    {
        File_("inbox/Goblin/goblin.stl");
        File_("Dragon/dragon.stl");
        await Index();

        Directory.Delete(Path.Combine(_root, "inbox", "Goblin"), recursive: true);
        var result = await IndexFolder("inbox");

        Assert.Equal(1, result.Removed);
        using var db = NewDb();
        Assert.Equal("Dragon", (await db.Models.SingleAsync()).RelativePath);
    }

    [Fact]
    public async Task An_inbox_scan_does_not_claim_the_library_was_scanned()
    {
        // LastScannedUtc gates the startup scan. Stamping it after looking at
        // one folder would skip the real rescan for the next interval.
        File_("inbox/Goblin/goblin.stl");
        await Index();

        DateTimeOffset? after;
        using (var db = NewDb())
        {
            after = (await db.Libraries.SingleAsync()).LastScannedUtc;
            (await db.Libraries.SingleAsync()).LastScannedUtc = DateTimeOffset.UnixEpoch;
            await db.SaveChangesAsync();
        }

        Assert.NotNull(after);
        await IndexFolder("inbox");

        using var check = NewDb();
        Assert.Equal(DateTimeOffset.UnixEpoch, (await check.Libraries.SingleAsync()).LastScannedUtc);
    }

    [Fact]
    public async Task Tags_on_a_model_in_the_inbox_survive_an_inbox_scan()
    {
        File_("inbox/Goblin/goblin.stl");
        await Index();

        using (var db = NewDb())
        {
            var model = await db.Models.SingleAsync();
            model.Notes = "kept";
            model.Tags.Add(new Tag { Name = "orc", NormalizedName = "orc" });
            await db.SaveChangesAsync();
        }

        await IndexFolder("inbox");

        using var check = NewDb();
        var reloaded = await check.Models.Include(m => m.Tags).SingleAsync();
        Assert.Equal("kept", reloaded.Notes);
        Assert.Single(reloaded.Tags);
    }

    [Fact]
    public void A_folder_outside_the_library_is_refused()
    {
        File_("Dragon/dragon.stl");

        Assert.Throws<ArgumentException>(() =>
            new FolderScanner().Scan(_root, "../elsewhere").ToList());

        // Resolved before it is checked, so no arrangement of ".." gets out.
        Assert.Throws<ArgumentException>(() =>
            new FolderScanner().Scan(_root, "Dragon/../../elsewhere").ToList());
    }

    public void Dispose()
    {
        _conn.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
