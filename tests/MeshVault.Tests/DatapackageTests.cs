using MeshVault.Core.Models;
using MeshVault.Core.Services;
using MeshVault.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshVault.Tests;

public class DatapackageReaderTests
{
    private const string Full = """
        {
          "$schema": "https://manyfold.app/profiles/0.0/datapackage.json",
          "name": "gl-inet-comet-pro-rack-mount",
          "title": "GL.iNet Comet Pro (GL-RM10) Rack Mount - official",
          "homepage": "http://localhost:3214/models/dkg9dvb87s3h",
          "keywords": ["containers", "misc"],
          "resources": [
            { "name": "a", "path": "part-a.stl", "mediatype": "model/stl", "up": "+z" },
            { "name": "b", "path": "part-b.stl", "mediatype": "model/stl", "up": "-y" }
          ]
        }
        """;

    [Fact]
    public void Reads_title_keywords_and_homepage()
    {
        var package = DatapackageReader.Parse(Full);

        Assert.Equal("GL.iNet Comet Pro (GL-RM10) Rack Mount - official", package.Title);
        Assert.Equal(["containers", "misc"], package.Keywords);
        Assert.Equal("http://localhost:3214/models/dkg9dvb87s3h", package.Homepage);
    }

    [Fact]
    public void Reads_the_up_axis_per_resource()
    {
        var package = DatapackageReader.Parse(Full);

        Assert.Equal("+z", package.UpAxisByFile["part-a.stl"]);
        Assert.Equal("-y", package.UpAxisByFile["part-b.stl"]);
    }

    [Fact]
    public void Reads_an_author_from_either_shape()
    {
        Assert.Equal("Loubie", DatapackageReader.Parse("""{"author":"Loubie"}""").Author);
        Assert.Equal("Loubie",
            DatapackageReader.Parse("""{"contributors":[{"title":"Loubie"}]}""").Author);
        Assert.Equal("Loubie", DatapackageReader.Parse("""{"contributors":["Loubie"]}""").Author);
    }

    [Fact]
    public void Reads_a_licence_from_either_shape()
    {
        Assert.Equal("CC-BY-4.0", DatapackageReader.Parse("""{"license":"CC-BY-4.0"}""").License);
        Assert.Equal("CC-BY-4.0",
            DatapackageReader.Parse("""{"licenses":[{"name":"CC-BY-4.0"}]}""").License);
    }

    [Fact]
    public void Blank_and_whitespace_values_are_treated_as_absent()
    {
        var package = DatapackageReader.Parse("""{"title":"   ","keywords":["", "  ", "real"]}""");

        Assert.Null(package.Title);
        Assert.Equal(["real"], package.Keywords);
    }

    /// <summary>One bad sidecar must not abort an import across hundreds of models.</summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    [InlineData("{\"keywords\": \"not an array\"}")]
    public void Malformed_files_yield_empty_rather_than_throwing(string json)
    {
        var package = DatapackageReader.Parse(json);

        Assert.Null(package.Title);
        Assert.Empty(package.Keywords);
    }
}

public class DatapackageImporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mv-dp-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly IDbContextFactory<MeshVaultDbContext> _factory;

    private sealed class Factory(SqliteConnection conn) : IDbContextFactory<MeshVaultDbContext>
    {
        public MeshVaultDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<MeshVaultDbContext>().UseSqlite(conn).Options);
    }

    public DatapackageImporterTests()
    {
        Directory.CreateDirectory(_root);
        _conn.Open();
        _factory = new Factory(_conn);

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Libraries.Add(new Library { Name = "L", Path = _root });
        db.SaveChanges();
    }

    private DatapackageImporter NewImporter() =>
        new(_factory, new LocalUser(), NullLogger<DatapackageImporter>.Instance);

    /// <summary>Creates a model folder with an optional sidecar, and the DB row for it.</summary>
    private async Task<int> AddModel(string folder, string? datapackageJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, folder));
        if (datapackageJson is not null)
        {
            await File.WriteAllTextAsync(
                Path.Combine(_root, folder, DatapackageReader.FileName), datapackageJson);
        }

        await using var db = _factory.CreateDbContext();
        var model = new ModelEntry
        {
            LibraryId = 1,
            Name = folder,
            RelativePath = folder,
            AddedUtc = DateTimeOffset.UtcNow,
        };
        db.Models.Add(model);
        await db.SaveChangesAsync();
        return model.Id;
    }

    private async Task<ModelEntry> Reload(int id)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Models.Include(m => m.Tags).Include(m => m.Designer)
            .FirstAsync(m => m.Id == id);
    }

    [Fact]
    public async Task Imports_title_as_the_model_name()
    {
        var id = await AddModel("comet-pro#281", """{"title":"GL.iNet Comet Pro Rack Mount"}""");

        var result = await NewImporter().ImportAsync(1);

        Assert.Equal(1, result.Renamed);
        Assert.Equal("GL.iNet Comet Pro Rack Mount", (await Reload(id)).Name);
    }

    [Fact]
    public async Task Imports_keywords_as_tags_and_reuses_existing_ones()
    {
        var a = await AddModel("a#1", """{"keywords":["containers","misc"]}""");
        var b = await AddModel("b#2", """{"keywords":["Containers","terrain"]}""");

        await NewImporter().ImportAsync(1);

        Assert.Equal(["containers", "misc"],
            (await Reload(a)).Tags.Select(t => t.NormalizedName).OrderBy(x => x));

        await using var db = _factory.CreateDbContext();
        // "Containers" and "containers" must be the same tag, not two.
        Assert.Equal(3, await db.Tags.CountAsync());
    }

    /// <summary>
    /// The point of the NameSetByUser flag: a title someone typed must survive
    /// an import, however many times it runs.
    /// </summary>
    [Fact]
    public async Task A_name_set_by_the_user_is_never_overwritten()
    {
        var id = await AddModel("thing#5", """{"title":"Sidecar Title"}""");
        await new ModelEditor(_factory, new LocalUser()).RenameAsync(id, "My Own Name");

        var result = await NewImporter().ImportAsync(1);

        Assert.Equal(0, result.Renamed);
        Assert.Equal("My Own Name", (await Reload(id)).Name);
    }

    [Fact]
    public async Task Resetting_a_name_lets_the_import_set_it_again()
    {
        var id = await AddModel("thing#5", """{"title":"Sidecar Title"}""");
        var editor = new ModelEditor(_factory, new LocalUser());

        await editor.RenameAsync(id, "My Own Name");
        await editor.ResetNameAsync(id);
        await NewImporter().ImportAsync(1);

        Assert.Equal("Sidecar Title", (await Reload(id)).Name);
    }

    /// <summary>
    /// A Manyfold export records its own instance as the homepage, which is
    /// usually a localhost address and worthless as provenance.
    /// </summary>
    [Theory]
    [InlineData("http://localhost:3214/models/abc")]
    [InlineData("http://127.0.0.1:3214/models/abc")]
    [InlineData("http://192.168.1.50/models/abc")]
    [InlineData("http://nas.local/models/abc")]
    [InlineData("http://manyfold/models/abc")]
    public async Task Local_homepages_are_not_imported_as_a_source(string homepage)
    {
        var id = await AddModel("m#1", $$"""{"homepage":"{{homepage}}"}""");

        var result = await NewImporter().ImportAsync(1);

        Assert.Equal(0, result.SourcesSet);
        Assert.Null((await Reload(id)).SourceUrl);
    }

    [Fact]
    public async Task A_real_external_homepage_is_imported_with_its_site()
    {
        var id = await AddModel("m#1", """{"homepage":"https://makerworld.com/models/42"}""");

        var result = await NewImporter().ImportAsync(1);

        Assert.Equal(1, result.SourcesSet);
        var model = await Reload(id);
        Assert.Equal("https://makerworld.com/models/42", model.SourceUrl);
        Assert.Equal("MakerWorld", model.SourceSite);
    }

    [Fact]
    public async Task Existing_metadata_is_left_alone()
    {
        var id = await AddModel("m#1",
            """{"homepage":"https://makerworld.com/models/42","author":"Sidecar","license":"CC0"}""");

        var editor = new ModelEditor(_factory, new LocalUser());
        await editor.SetSourceUrlAsync(id, "https://printables.com/model/1");
        await editor.SetDesignerAsync(id, "Mine");
        await editor.SetLicenseAsync(id, "CC BY-NC");

        var result = await NewImporter().ImportAsync(1);

        Assert.Equal(0, result.SourcesSet);
        Assert.Equal(0, result.DesignersSet);
        Assert.Equal(0, result.LicensesSet);

        var model = await Reload(id);
        Assert.Equal("Printables", model.SourceSite);
        Assert.Equal("Mine", model.Designer?.Name);
        Assert.Equal("CC BY-NC", model.License);
    }

    [Fact]
    public async Task Running_twice_changes_nothing_the_second_time()
    {
        await AddModel("a#1", """{"title":"A","keywords":["x"],"author":"Loubie","license":"CC0"}""");

        var first = await NewImporter().ImportAsync(1);
        var second = await NewImporter().ImportAsync(1);

        Assert.True(first.Changed > 0);
        Assert.Equal(0, second.Changed);
    }

    [Fact]
    public async Task Models_without_a_sidecar_are_counted_and_untouched()
    {
        var id = await AddModel("bare#1", datapackageJson: null);

        var result = await NewImporter().ImportAsync(1);

        Assert.Equal(1, result.Skipped);
        Assert.Equal("bare#1", (await Reload(id)).Name);
    }

    [Fact]
    public async Task One_malformed_sidecar_does_not_stop_the_others()
    {
        await AddModel("bad#1", "{ this is not json");
        var good = await AddModel("good#2", """{"title":"Good One"}""");

        var result = await NewImporter().ImportAsync(1);

        Assert.Equal(1, result.Renamed);
        Assert.Equal("Good One", (await Reload(good)).Name);
    }

    /// <summary>
    /// Manyfold writes collections as objects carrying a title plus a link back
    /// to its own instance; only the title should survive the import.
    /// </summary>
    [Fact]
    public async Task Imports_collections_by_title()
    {
        var a = await AddModel("a#1", """
            {"collections":[{"title":"The Infinite Spaceship",
              "path":"http://localhost:3214/collections/abc","caption":""}]}
            """);
        var b = await AddModel("b#2", """{"collections":[{"title":"The Infinite Spaceship"}]}""");

        var result = await NewImporter().ImportAsync(1);

        Assert.Equal(2, result.Collected);

        await using var db = _factory.CreateDbContext();
        var collection = await db.Collections.Include(c => c.Models).SingleAsync();
        Assert.Equal("The Infinite Spaceship", collection.Name);
        Assert.Equal(Users.LocalUserId, collection.OwnerId);
        // Both models joined the same collection rather than creating two.
        Assert.Equal(2, collection.Models.Count);
    }

    [Fact]
    public async Task Collection_membership_is_not_duplicated_on_a_second_run()
    {
        await AddModel("a#1", """{"collections":[{"title":"Terrain"}]}""");

        await NewImporter().ImportAsync(1);
        var second = await NewImporter().ImportAsync(1);

        Assert.Equal(0, second.Collected);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Collections.CountAsync());
        Assert.Single((await db.Collections.Include(c => c.Models).SingleAsync()).Models);
    }

    [Fact]
    public async Task Imports_a_contributor_object_as_the_designer()
    {
        var id = await AddModel("a#1", """
            {"contributors":[{"title":"Dungeon Blocks",
              "path":"http://localhost:3214/creators/dungeon-blocks","roles":["creator"]}]}
            """);

        var result = await NewImporter().ImportAsync(1);

        Assert.Equal(1, result.DesignersSet);
        Assert.Equal("Dungeon Blocks", (await Reload(id)).Designer?.Name);
    }

    [Fact]
    public async Task Progress_is_reported()
    {
        for (var i = 0; i < 25; i++)
            await AddModel($"m{i}#1", $$"""{"title":"Model {{i}}"}""");

        var reports = new List<ImportProgress>();
        await NewImporter().ImportAsync(1, new SyncProgress<ImportProgress>(reports.Add));

        Assert.NotEmpty(reports);
        Assert.Equal(25, reports[^1].Total);
    }

    public void Dispose()
    {
        _conn.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
