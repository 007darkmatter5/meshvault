using MeshVault.Core.Models;
using MeshVault.Core.Services;
using MeshVault.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Tests;

/// <summary>
/// Paint racks are private, schemes are owned but readable by everyone. That
/// asymmetry is the feature, so most of what matters here is who can see and
/// change what.
/// </summary>
public class PaintStoreTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly IDbContextFactory<MeshVaultDbContext> _factory;
    private readonly PaintStore _alice;
    private readonly PaintStore _bob;

    private sealed class FakeUser(string id) : ICurrentUser
    {
        public string UserId { get; } = id;
    }

    private sealed class Factory(SqliteConnection conn) : IDbContextFactory<MeshVaultDbContext>
    {
        public MeshVaultDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<MeshVaultDbContext>().UseSqlite(conn).Options);
    }

    public PaintStoreTests()
    {
        _conn.Open();
        _factory = new Factory(_conn);

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Libraries.Add(new Library { Name = "L", Path = "/l" });
        db.Models.Add(new ModelEntry
        {
            LibraryId = 1,
            Name = "Dragon",
            RelativePath = "dragon",
            AddedUtc = DateTimeOffset.UtcNow,
            FileModifiedUtc = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        _alice = new PaintStore(_factory, new FakeUser("alice"));
        _bob = new PaintStore(_factory, new FakeUser("bob"));
    }

    private static Paint Pot(string name, string? hex = null, PaintStock stock = PaintStock.Have) =>
        new() { Name = name, Hex = hex, Stock = stock };

    [Fact]
    public async Task A_rack_is_private_to_its_owner()
    {
        await _alice.AddPaintAsync(Pot("Mephiston Red"));

        Assert.Single(await _alice.GetRackAsync());
        Assert.Empty(await _bob.GetRackAsync());
    }

    [Fact]
    public async Task Two_people_can_each_own_the_same_paint()
    {
        // The unique index is per rack, not global.
        await _alice.AddPaintAsync(Pot("Nuln Oil"));
        await _bob.AddPaintAsync(Pot("Nuln Oil"));

        Assert.Single(await _alice.GetRackAsync());
        Assert.Single(await _bob.GetRackAsync());
    }

    [Fact]
    public async Task Adding_a_pot_already_on_the_rack_does_not_duplicate_it()
    {
        await _alice.AddPaintAsync(Pot("Nuln Oil"));
        await _alice.AddPaintAsync(Pot("NULN OIL"));

        Assert.Single(await _alice.GetRackAsync());
    }

    [Fact]
    public async Task One_person_cannot_delete_anothers_paint()
    {
        var paint = await _alice.AddPaintAsync(Pot("Nuln Oil"));

        await _bob.DeletePaintAsync(paint!.Id);

        Assert.Single(await _alice.GetRackAsync());
    }

    [Fact]
    public async Task Stock_can_be_changed_on_its_own()
    {
        var paint = await _alice.AddPaintAsync(Pot("Nuln Oil"));

        await _alice.SetStockAsync(paint!.Id, PaintStock.Out);

        Assert.Equal(PaintStock.Out, (await _alice.GetRackAsync())[0].Stock);
    }

    [Fact]
    public async Task A_scheme_is_visible_to_everyone_but_only_editable_by_its_owner()
    {
        var scheme = await _alice.CreateSchemeAsync(1, "Red Dragon", null, "Alice");

        var asBob = Assert.Single(await _bob.GetSchemesAsync(1));
        Assert.Equal("Red Dragon", asBob.Scheme.Name);
        Assert.False(asBob.IsMine);

        await _bob.UpdateSchemeAsync(scheme!.Id, "Hijacked", null);
        Assert.Equal("Red Dragon", (await _bob.GetSchemesAsync(1))[0].Scheme.Name);
    }

    [Fact]
    public async Task One_model_can_carry_several_schemes()
    {
        // The same miniature painted red and bronze is two recipes, not a
        // disagreement about one.
        await _alice.CreateSchemeAsync(1, "Red Dragon", null, "Alice");
        await _bob.CreateSchemeAsync(1, "Bronze Dragon", null, "Bob");

        Assert.Equal(2, (await _alice.GetSchemesAsync(1)).Count);
    }

    [Fact]
    public async Task Only_the_owner_can_delete_a_scheme()
    {
        var scheme = await _alice.CreateSchemeAsync(1, "Red Dragon", null, "Alice");

        await _bob.DeleteSchemeAsync(scheme!.Id);
        Assert.Single(await _alice.GetSchemesAsync(1));

        await _alice.DeleteSchemeAsync(scheme.Id);
        Assert.Empty(await _alice.GetSchemesAsync(1));
    }

    [Fact]
    public async Task A_step_copies_the_paint_name_so_the_recipe_reads_without_the_pot()
    {
        var paint = await _alice.AddPaintAsync(Pot("Mephiston Red", "#9a1115"));
        var scheme = await _alice.CreateSchemeAsync(1, "Red Dragon", null, "Alice");

        await _alice.AddStepAsync(scheme!.Id, paint!.Id, "", "Basecoat", "scales");

        var step = (await _bob.GetSchemesAsync(1))[0].Scheme.Steps.Single();
        Assert.Equal("Mephiston Red", step.PaintName);
        Assert.Equal("#9a1115", step.Hex);
    }

    [Fact]
    public async Task Throwing_a_pot_away_does_not_unpaint_the_model()
    {
        var paint = await _alice.AddPaintAsync(Pot("Mephiston Red", "#9a1115"));
        var scheme = await _alice.CreateSchemeAsync(1, "Red Dragon", null, "Alice");
        await _alice.AddStepAsync(scheme!.Id, paint!.Id, "", "Basecoat", "scales");

        await _alice.DeletePaintAsync(paint.Id);

        var step = (await _alice.GetSchemesAsync(1))[0].Scheme.Steps.Single();
        Assert.Null(step.PaintId);
        Assert.Equal("Mephiston Red", step.PaintName);
        Assert.Equal("#9a1115", step.Hex);
    }

    [Fact]
    public async Task A_step_can_name_a_paint_nobody_owns()
    {
        var scheme = await _alice.CreateSchemeAsync(1, "Red Dragon", null, "Alice");

        await _alice.AddStepAsync(scheme!.Id, null, "Some Obscure Lacquer", "Glaze", "wings");

        Assert.Equal("Some Obscure Lacquer",
            (await _alice.GetSchemesAsync(1))[0].Scheme.Steps.Single().PaintName);
    }

    [Fact]
    public async Task A_reader_is_told_what_they_would_have_to_buy()
    {
        // The point of a private rack next to a public scheme.
        var red = await _alice.AddPaintAsync(Pot("Mephiston Red"));
        var wash = await _alice.AddPaintAsync(Pot("Nuln Oil"));
        var scheme = await _alice.CreateSchemeAsync(1, "Red Dragon", null, "Alice");
        await _alice.AddStepAsync(scheme!.Id, red!.Id, "", "Basecoat", "scales");
        await _alice.AddStepAsync(scheme.Id, wash!.Id, "", "Wash", "recesses");

        await _bob.AddPaintAsync(Pot("Nuln Oil"));

        var forAlice = Assert.Single(await _alice.GetSchemesAsync(1));
        Assert.True(forAlice.CanPaint);
        Assert.Empty(forAlice.Missing);

        var forBob = Assert.Single(await _bob.GetSchemesAsync(1));
        Assert.False(forBob.CanPaint);
        Assert.Equal(["Mephiston Red"], forBob.Missing);
    }

    [Fact]
    public async Task A_paint_you_have_run_out_of_counts_as_missing()
    {
        var red = await _alice.AddPaintAsync(Pot("Mephiston Red"));
        var scheme = await _alice.CreateSchemeAsync(1, "Red Dragon", null, "Alice");
        await _alice.AddStepAsync(scheme!.Id, red!.Id, "", "Basecoat", "scales");

        await _alice.SetStockAsync(red.Id, PaintStock.Out);

        Assert.Equal(["Mephiston Red"], (await _alice.GetSchemesAsync(1))[0].Missing);
    }

    [Fact]
    public async Task Steps_keep_the_order_they_were_added_in()
    {
        var scheme = await _alice.CreateSchemeAsync(1, "Red Dragon", null, "Alice");
        await _alice.AddStepAsync(scheme!.Id, null, "Basecoat paint", "Basecoat", null);
        await _alice.AddStepAsync(scheme.Id, null, "Wash paint", "Wash", null);
        await _alice.AddStepAsync(scheme.Id, null, "Highlight paint", "Highlight", null);

        var steps = (await _alice.GetSchemesAsync(1))[0].Scheme.Steps;

        Assert.Equal(["Basecoat paint", "Wash paint", "Highlight paint"],
            steps.Select(s => s.PaintName));
    }

    [Fact]
    public async Task Removing_a_step_closes_the_gap_in_the_order()
    {
        var scheme = await _alice.CreateSchemeAsync(1, "Red Dragon", null, "Alice");
        await _alice.AddStepAsync(scheme!.Id, null, "One", null, null);
        var middle = await _alice.AddStepAsync(scheme.Id, null, "Two", null, null);
        await _alice.AddStepAsync(scheme.Id, null, "Three", null, null);

        await _alice.RemoveStepAsync(middle!.Id);

        var steps = (await _alice.GetSchemesAsync(1))[0].Scheme.Steps;
        Assert.Equal([0, 1], steps.Select(s => s.Order));
        Assert.Equal(["One", "Three"], steps.Select(s => s.PaintName));
    }

    [Fact]
    public async Task One_person_cannot_add_a_step_to_anothers_scheme()
    {
        var scheme = await _alice.CreateSchemeAsync(1, "Red Dragon", null, "Alice");

        var step = await _bob.AddStepAsync(scheme!.Id, null, "Sneaky", null, null);

        Assert.Null(step);
        Assert.Empty((await _alice.GetSchemesAsync(1))[0].Scheme.Steps);
    }

    [Fact]
    public async Task A_scheme_needs_a_name()
    {
        Assert.Null(await _alice.CreateSchemeAsync(1, "   ", null, "Alice"));
    }

    [Fact]
    public async Task A_scheme_cannot_be_attached_to_a_model_that_is_not_there()
    {
        Assert.Null(await _alice.CreateSchemeAsync(9999, "Ghost", null, "Alice"));
    }

    public void Dispose() => _conn.Dispose();
}
