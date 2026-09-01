using System.IO.Compression;
using MeshVault.Core.Models;
using MeshVault.Core.Services;
using MeshVault.Data;
using MeshVault.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshVault.Tests;

/// <summary>
/// What a download covers, and what actually lands in the zip.
/// </summary>
/// <remarks>
/// The archive is streamed straight to the response, so by the time anything is
/// wrong the status code has long gone and the browser is holding a file. What
/// can be checked before that has to be checked here instead.
/// </remarks>
public class DownloadTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "meshvault-download-" + Guid.NewGuid().ToString("N"));

    private readonly IDbContextFactory<MeshVaultDbContext> _factory;

    private sealed class FakeUser(string id) : ICurrentUser
    {
        public string UserId => id;
    }

    private sealed class Factory(SqliteConnection conn) : IDbContextFactory<MeshVaultDbContext>
    {
        public MeshVaultDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<MeshVaultDbContext>().UseSqlite(conn).Options);
    }

    public DownloadTests()
    {
        _conn.Open();
        _factory = new Factory(_conn);

        Directory.CreateDirectory(_root);

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Libraries.Add(new Library { Name = "L", Path = _root });
        db.SaveChanges();
    }

    private DownloadCatalog CatalogFor(string userId) => new(_factory, new FakeUser(userId));

    /// <summary>Writes a real file under the library root and returns its row.</summary>
    private ModelFile PlaceFile(string relativePath, string content = "solid", int rank = 0,
        string? sculptKey = null, string? sculptName = null)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);

        return new ModelFile
        {
            RelativePath = relativePath,
            FileName = Path.GetFileName(relativePath),
            Extension = Path.GetExtension(relativePath),
            Kind = FileKind.Mesh,
            SizeBytes = content.Length,
            ModifiedUtc = DateTimeOffset.UtcNow,
            VariantRank = rank,
            SculptKey = sculptKey,
            SculptName = sculptName,
        };
    }

    private ModelEntry AddModel(string relativePath, string name, ModelFile[] files, int libraryId) =>
        AddModelCore(relativePath, name, files, libraryId);

    private ModelEntry AddModel(string relativePath, string name, params ModelFile[] files) =>
        AddModelCore(relativePath, name, files, 1);

    private ModelEntry AddModelCore(string relativePath, string name, ModelFile[] files, int libraryId)
    {
        using var db = _factory.CreateDbContext();

        var model = new ModelEntry
        {
            LibraryId = libraryId,
            Name = name,
            RelativePath = relativePath,
            AddedUtc = DateTimeOffset.UtcNow,
            FileModifiedUtc = DateTimeOffset.UtcNow,
            TotalBytes = files.Sum(f => f.SizeBytes),
            Files = [.. files],
        };

        db.Models.Add(model);
        db.SaveChanges();
        return model;
    }

    [Fact]
    public async Task A_models_files_keep_their_layout_beneath_its_folder()
    {
        var model = AddModel("packs/orcs", "Orc Pack",
            PlaceFile("packs/orcs/readme.txt"),
            PlaceFile("packs/orcs/supported/grunt.stl"));

        var set = await CatalogFor("alice").GetModelAsync(model.Id);

        Assert.NotNull(set);
        Assert.Equal(
            ["readme.txt", "supported/grunt.stl"],
            set.Items.Select(i => i.EntryPath).Order());
    }

    /// <summary>
    /// The detail page shows a grouped model's folders as one thing. A Download
    /// button there that fetched only the folder you happened to arrive at would
    /// hand back less than the page is showing.
    /// </summary>
    [Fact]
    public async Task A_grouped_model_downloads_every_folder_the_page_shows()
    {
        var supported = AddModel("otto-supported", "Otto Supported",
            PlaceFile("otto-supported/otto.stl"));
        var plain = AddModel("otto-plain", "Otto Plain",
            PlaceFile("otto-plain/otto.stl"));

        Group([supported.Id, plain.Id], "otto", primary: supported.Id);

        var set = await CatalogFor("alice").GetModelAsync(supported.Id);

        Assert.NotNull(set);
        Assert.Equal(
            ["Otto Plain/otto.stl", "Otto Supported/otto.stl"],
            set.Items.Select(i => i.EntryPath).Order());
    }

    [Fact]
    public async Task A_sculpt_download_takes_only_that_sculpts_exports()
    {
        var model = AddModel("pack", "Pack",
            PlaceFile("pack/otto.stl", sculptKey: "otto", sculptName: "Otto"),
            PlaceFile("pack/otto-supported.stl", rank: 5, sculptKey: "otto", sculptName: "Otto"),
            PlaceFile("pack/greta.stl", sculptKey: "greta", sculptName: "Greta"));

        var set = await CatalogFor("alice").GetSculptAsync(model.Id, "otto");

        Assert.NotNull(set);
        Assert.Equal("Otto", set.Name);
        Assert.Equal(["otto-supported.stl", "otto.stl"], set.Items.Select(i => i.EntryPath).Order());
    }

    /// <summary>
    /// The key is stored lowercased, so naming the archive from it would hand
    /// back "ud 067 hole trap" for a sculpt everyone calls UD 067 Hole Trap.
    /// </summary>
    [Fact]
    public async Task A_sculpt_archive_is_named_with_the_spelling_the_file_carries()
    {
        var model = AddModel("pack", "Pack",
            PlaceFile("pack/ud-067.stl", sculptKey: "ud 067 hole trap", sculptName: "UD 067 Hole Trap"));

        var set = await CatalogFor("alice").GetSculptAsync(model.Id, "ud 067 hole trap");

        Assert.Equal("UD 067 Hole Trap", set?.Name);
    }

    [Fact]
    public async Task Collections_belonging_to_someone_else_are_not_downloadable()
    {
        var model = AddModel("m", "M", PlaceFile("m/a.stl"));
        var collectionId = AddCollection("Bobs list", "bob", model.Id);

        Assert.Null(await CatalogFor("alice").GetCollectionAsync(collectionId));
        Assert.Null(await CatalogFor("alice").GetCollectionSizeAsync(collectionId));
        Assert.NotNull(await CatalogFor("bob").GetCollectionAsync(collectionId));
    }

    /// <summary>
    /// Browse lists only a group's primary, so that is the row that gets added
    /// to a collection. Downloading only that folder would quietly drop the
    /// other exports the card stands for.
    /// </summary>
    [Fact]
    public async Task A_collection_brings_the_whole_group_of_a_model_it_holds()
    {
        var supported = AddModel("otto-supported", "Otto Supported",
            PlaceFile("otto-supported/otto.stl"));
        var plain = AddModel("otto-plain", "Otto Plain",
            PlaceFile("otto-plain/otto.stl"));

        Group([supported.Id, plain.Id], "otto", primary: supported.Id);

        var collectionId = AddCollection("To print", "alice", supported.Id);

        var set = await CatalogFor("alice").GetCollectionAsync(collectionId);

        Assert.NotNull(set);
        Assert.Equal(2, set.Items.Count);
    }

    /// <summary>
    /// The dialog promises a size before anything is streamed. It counts from
    /// the index rather than from disk, so it has to expand groups the same way
    /// the download does or it will under-promise.
    /// </summary>
    [Fact]
    public async Task The_promised_size_matches_what_the_download_sends()
    {
        var supported = AddModel("otto-supported", "Otto Supported",
            PlaceFile("otto-supported/otto.stl", content: "aaaa"));
        var plain = AddModel("otto-plain", "Otto Plain",
            PlaceFile("otto-plain/otto.stl", content: "bbbbbb"));

        Group([supported.Id, plain.Id], "otto", primary: supported.Id);
        var collectionId = AddCollection("To print", "alice", supported.Id);

        var catalog = CatalogFor("alice");
        var size = await catalog.GetCollectionSizeAsync(collectionId);
        var set = await catalog.GetCollectionAsync(collectionId);

        Assert.NotNull(size);
        Assert.NotNull(set);
        Assert.Equal(2, size.Models);
        Assert.Equal(set.Items.Count, size.Files);
        Assert.Equal(set.TotalBytes, size.TotalBytes);
    }

    /// <summary>
    /// Zip allows two entries with one name, and most tools extract them over
    /// each other — quietly handing back fewer files than were asked for.
    /// </summary>
    [Fact]
    public async Task Two_models_holding_the_same_name_do_not_land_on_each_other()
    {
        var first = AddModel("a/otto", "Otto", PlaceFile("a/otto/otto.stl"));
        var second = AddModel("b/otto", "Otto", PlaceFile("b/otto/otto.stl"));

        var collectionId = AddCollection("Both", "alice", first.Id, second.Id);

        var set = await CatalogFor("alice").GetCollectionAsync(collectionId);

        Assert.NotNull(set);
        Assert.Equal(2, set.Items.Select(i => i.EntryPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// Numbering a duplicate splits the name on the extension, and a folder is
    /// allowed a dot in it. Splitting on the last dot anywhere would turn
    /// "Otto v1.2/otto.stl" into a stem of "Otto v1" and drop the copy into a
    /// folder nobody named.
    /// </summary>
    [Fact]
    public async Task A_dot_in_a_folder_name_is_not_mistaken_for_an_extension()
    {
        var first = AddModel("a", "Otto v1.2", PlaceFile("a/otto.stl"));
        var second = AddModel("b", "Otto v1.2", PlaceFile("b/otto.stl"));

        var collectionId = AddCollection("Both", "alice", first.Id, second.Id);

        var set = await CatalogFor("alice").GetCollectionAsync(collectionId);

        Assert.NotNull(set);
        Assert.Equal(
            ["Otto v1.2/otto (2).stl", "Otto v1.2/otto.stl"],
            set.Items.Select(i => i.EntryPath).Order());
    }

    /// <summary>
    /// Group keys are only unique within a library, and expansion now asks for
    /// every key at once rather than one query per group. Two libraries using
    /// the same key must not pull each other's folders in.
    /// </summary>
    [Fact]
    public async Task A_group_key_reused_in_another_library_is_not_pulled_in()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.Libraries.Add(new Library { Name = "Other", Path = _root + "-other" });
            db.SaveChanges();
        }

        var mine = AddModel("mine", "Mine", PlaceFile("mine/otto.stl"));
        var alsoMine = AddModel("also-mine", "Also Mine", PlaceFile("also-mine/otto.stl"));
        var theirs = AddModel("theirs", "Theirs", [PlaceFile("theirs/otto.stl")], libraryId: 2);

        Group([mine.Id, alsoMine.Id], "otto", primary: mine.Id);
        Group([theirs.Id], "otto", primary: theirs.Id);

        var set = await CatalogFor("alice").GetModelAsync(mine.Id);

        Assert.NotNull(set);
        Assert.Equal(2, set.Items.Count);
        Assert.DoesNotContain(set.Items, i => i.EntryPath.StartsWith("Theirs/"));
    }

    /// <summary>
    /// The one place the app hands raw file bytes to a browser. A single bad row
    /// must not become a read of anything the process can open.
    /// </summary>
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("models/../../outside.stl")]
    [InlineData("")]
    public void A_path_climbing_out_of_the_library_resolves_to_nothing(string relativePath)
    {
        Assert.Null(DownloadCatalog.ResolveWithin(_root, relativePath));
    }

    [Fact]
    public void An_ordinary_path_resolves_inside_the_library()
    {
        var resolved = DownloadCatalog.ResolveWithin(_root, "packs/orcs/grunt.stl");

        Assert.NotNull(resolved);
        Assert.StartsWith(Path.GetFullPath(_root), resolved);
    }

    /// <summary>
    /// Reads the archive back rather than checking that bytes came out. A zip
    /// with the wrong entry names, or one truncated by a synchronous write
    /// Kestrel refuses, is still a plausible-looking pile of bytes.
    /// </summary>
    [Fact]
    public async Task The_archive_holds_every_file_at_the_path_it_was_given()
    {
        var model = AddModel("packs/orcs", "Orc Pack",
            PlaceFile("packs/orcs/readme.txt", content: "print at 0.05"),
            PlaceFile("packs/orcs/supported/grunt.stl", content: "solid grunt"));

        var set = await CatalogFor("alice").GetModelAsync(model.Id);
        Assert.NotNull(set);

        using var buffer = new MemoryStream();
        var written = await ArchiveWriter.WriteAsync(buffer, set, NullLogger.Instance);

        Assert.Equal(2, written);

        buffer.Position = 0;
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);

        Assert.Equal(
            ["readme.txt", "supported/grunt.stl"],
            zip.Entries.Select(e => e.FullName).Order());

        using var reader = new StreamReader(zip.GetEntry("supported/grunt.stl")!.Open());
        Assert.Equal("solid grunt", await reader.ReadToEndAsync());
    }

    /// <summary>
    /// A file indexed but since moved. Skipping it hands back an archive short
    /// of a file; throwing hands back a truncated one, which looks the same and
    /// says less.
    /// </summary>
    [Fact]
    public async Task A_file_that_has_gone_missing_is_left_out_rather_than_taking_the_archive_down()
    {
        var model = AddModel("m", "M",
            PlaceFile("m/here.stl", content: "here"),
            PlaceFile("m/gone.stl", content: "gone"));

        File.Delete(Path.Combine(_root, "m", "gone.stl"));

        var set = await CatalogFor("alice").GetModelAsync(model.Id);
        Assert.NotNull(set);

        using var buffer = new MemoryStream();
        var written = await ArchiveWriter.WriteAsync(buffer, set, NullLogger.Instance);

        Assert.Equal(1, written);

        buffer.Position = 0;
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        Assert.Equal(["here.stl"], zip.Entries.Select(e => e.FullName));
    }

    private void Group(int[] modelIds, string groupKey, int primary)
    {
        using var db = _factory.CreateDbContext();

        foreach (var model in db.Models.Where(m => modelIds.Contains(m.Id)))
        {
            model.GroupKey = groupKey;
            model.GroupName = "Otto";
            model.GroupPrimary = model.Id == primary;
        }

        db.SaveChanges();
    }

    private int AddCollection(string name, string ownerId, params int[] modelIds)
    {
        using var db = _factory.CreateDbContext();

        var collection = new Collection
        {
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            OwnerId = ownerId,
            CreatedUtc = DateTimeOffset.UtcNow,
            Models = [.. db.Models.Where(m => modelIds.Contains(m.Id))],
        };

        db.Collections.Add(collection);
        db.SaveChanges();
        return collection.Id;
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
