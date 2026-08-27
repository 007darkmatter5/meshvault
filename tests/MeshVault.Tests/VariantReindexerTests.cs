using MeshVault.Core.Models;
using MeshVault.Core.Services;
using MeshVault.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshVault.Tests;

public class VariantReindexerTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly ServiceProvider _services;
    private readonly VariantRules _rules = new();

    public VariantReindexerTests()
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

    private VariantReindexer NewReindexer() =>
        new(Factory, _rules, new VariantStore(Factory), new SettingsStore(Factory),
            NullLogger<VariantReindexer>.Instance);

    private ModelFile Mesh(string name, ThumbnailState state = ThumbnailState.Ready, long size = 100) =>
        new()
        {
            RelativePath = $"Dungeon/{name}",
            FileName = name,
            Extension = ".stl",
            Kind = FileKind.Mesh,
            SizeBytes = size,
            ThumbnailState = state,
        };

    private async Task<ModelEntry> SeedAsync(params ModelFile[] files)
    {
        await using var db = await Factory.CreateDbContextAsync();
        var model = new ModelEntry
        {
            LibraryId = 1,
            RelativePath = "Dungeon",
            Name = "Dungeon",
            Files = [.. files],
        };
        db.Models.Add(model);
        await db.SaveChangesAsync();
        return model;
    }

    [Fact]
    public async Task Classifies_files_that_were_indexed_before_variants_existed()
    {
        // Every row starts with no sculpt key, exactly as the upgrade finds them.
        await SeedAsync(Mesh("Tavern_supported.stl"), Mesh("Tavern_unsupported.stl"));

        var result = await NewReindexer().ReclassifyAllAsync();

        Assert.Equal(2, result.FilesReclassified);

        await using var db = await Factory.CreateDbContextAsync();
        var files = await db.Files.ToListAsync();
        Assert.All(files, f => Assert.Equal("tavern", f.SculptKey));
    }

    [Fact]
    public async Task Reclassifying_twice_changes_nothing_the_second_time()
    {
        await SeedAsync(Mesh("Tavern_supported.stl"));

        var reindexer = NewReindexer();
        await reindexer.ReclassifyAllAsync();

        Assert.Equal(0, (await reindexer.ReclassifyAllAsync()).FilesReclassified);
    }

    [Fact]
    public async Task Moves_the_card_image_off_a_supported_export()
    {
        // The card landed on the supported copy because it rendered first. Both
        // are already rendered, so correcting it is only a pointer change.
        var supported = Mesh("Tavern_supported.stl", size: 900);
        var plain = Mesh("Tavern_unsupported.stl", size: 100);
        var model = await SeedAsync(supported, plain);

        await using (var db = await Factory.CreateDbContextAsync())
        {
            var entry = await db.Models.FirstAsync(m => m.Id == model.Id);
            entry.ThumbnailFileId = supported.Id;
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, (await NewReindexer().ReclassifyAllAsync()).CardsRepointed);

        await using var check = await Factory.CreateDbContextAsync();
        Assert.Equal(plain.Id, (await check.Models.FirstAsync()).ThumbnailFileId);
    }

    [Fact]
    public async Task Never_points_a_card_at_a_file_with_no_thumbnail()
    {
        // The plain export is the better picture but has not been rendered;
        // pointing at it would leave the card blank.
        var supported = Mesh("Tavern_supported.stl");
        var plain = Mesh("Tavern_unsupported.stl", state: ThumbnailState.Pending);
        var model = await SeedAsync(supported, plain);

        await using (var db = await Factory.CreateDbContextAsync())
        {
            var entry = await db.Models.FirstAsync(m => m.Id == model.Id);
            entry.ThumbnailFileId = supported.Id;
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, (await NewReindexer().ReclassifyAllAsync()).CardsRepointed);

        await using var check = await Factory.CreateDbContextAsync();
        Assert.Equal(supported.Id, (await check.Models.FirstAsync()).ThumbnailFileId);
    }

    [Fact]
    public async Task Leaves_a_model_with_no_card_image_alone()
    {
        // Nothing has rendered yet; picking a card here would claim an image exists.
        await SeedAsync(Mesh("Tavern.stl", state: ThumbnailState.Pending));

        Assert.Equal(0, (await NewReindexer().ReclassifyAllAsync()).CardsRepointed);

        await using var db = await Factory.CreateDbContextAsync();
        Assert.Null((await db.Models.FirstAsync()).ThumbnailFileId);
    }

    [Fact]
    public async Task An_unchanged_vocabulary_does_not_re_read_the_library()
    {
        await SeedAsync(Mesh("Tavern_supported.stl"));
        var reindexer = NewReindexer();

        Assert.Equal(1, (await reindexer.ApplyAsync()).FilesReclassified);

        // Restarting must not walk the whole library again.
        Assert.False((await reindexer.ApplyAsync()).Any);
    }

    [Fact]
    public async Task A_curated_definition_re_reads_the_library()
    {
        await SeedAsync(Mesh("Tavern_mysupports.stl"));
        var reindexer = NewReindexer();
        await reindexer.ApplyAsync();

        await new VariantStore(Factory).SaveAsync(new VariantDefinition
        {
            Name = "House style", MatchTerms = "mysupports", PreviewRank = 40,
        });

        Assert.Equal(1, (await reindexer.ApplyAsync()).FilesReclassified);

        await using var db = await Factory.CreateDbContextAsync();
        var file = await db.Files.FirstAsync();
        Assert.Equal("tavern", file.SculptKey);
        Assert.Equal("House style", file.VariantLabel);
        Assert.Equal(40, file.VariantRank);
    }

    [Fact]
    public async Task The_starter_vocabulary_is_only_offered_once()
    {
        // Deleting every definition means it: they must not come back on the
        // next restart.
        var store = new VariantStore(Factory);
        Assert.NotEmpty(await store.SeedIfEmptyAsync());

        foreach (var definition in await store.GetAsync())
            await store.DeleteAsync(definition.Id);

        Assert.Empty(await store.SeedIfEmptyAsync());
    }

    [Fact]
    public async Task A_file_set_by_hand_survives_a_vocabulary_change()
    {
        var file = Mesh("UD-003-SUP-Wall Skuls 2.stl");
        await SeedAsync(file);

        var reindexer = NewReindexer();
        await reindexer.ApplyAsync();

        // The correction: this is really Skulls, same sculpt as its siblings.
        await new ModelEditor(Factory, new LocalUser()).SetVariantAsync(
            file.Id, "UD 003 Wall Skulls 2",
            [new VariantDefinition { Name = "Supported", PreviewRank = 30 }]);

        await reindexer.ReclassifyAllAsync();

        await using var db = await Factory.CreateDbContextAsync();
        var saved = await db.Files.FirstAsync();
        Assert.Equal("ud 003 wall skulls 2", saved.SculptKey);
        Assert.Equal("Supported", saved.VariantLabel);
        Assert.True(saved.VariantSetByUser);
    }

    [Fact]
    public async Task Handing_a_file_back_to_detection_lets_the_next_pass_reach_it()
    {
        var file = Mesh("Tavern_supported.stl");
        await SeedAsync(file);

        var editor = new ModelEditor(Factory, new LocalUser());
        await editor.SetVariantAsync(file.Id, "Something Else", []);
        await editor.ResetVariantAsync(file.Id);

        // One model, not the whole library: this runs from a button on a page.
        await NewReindexer().ReclassifyModelAsync(file.ModelEntryId);

        await using var db = await Factory.CreateDbContextAsync();
        var saved = await db.Files.FirstAsync();
        Assert.Equal("tavern", saved.SculptKey);
        Assert.False(saved.VariantSetByUser);
    }

    public void Dispose()
    {
        _services.Dispose();
        _conn.Dispose();
    }
}
