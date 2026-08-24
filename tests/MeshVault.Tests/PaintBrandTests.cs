using MeshVault.Core.Models;
using MeshVault.Core.Services;
using MeshVault.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Tests;

/// <summary>
/// Brands and the ranges under them, curated per instance rather than seeded.
/// The point of tying a range to a brand is that choosing Citadel offers
/// Citadel's ranges and nobody else's.
/// </summary>
public class PaintBrandTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly IDbContextFactory<MeshVaultDbContext> _factory;
    private readonly PaintStore _store;

    private sealed class FakeUser : ICurrentUser
    {
        public string UserId => "alice";
    }

    private sealed class Factory(SqliteConnection conn) : IDbContextFactory<MeshVaultDbContext>
    {
        public MeshVaultDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<MeshVaultDbContext>().UseSqlite(conn).Options);
    }

    public PaintBrandTests()
    {
        _conn.Open();
        _factory = new Factory(_conn);

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        _store = new PaintStore(_factory, new FakeUser());
    }

    [Fact]
    public async Task A_new_instance_knows_no_brands_at_all()
    {
        // Nothing is seeded. A built-in list is wrong the week it ships, and
        // most people own two or three makes rather than fourteen.
        Assert.Empty(await _store.GetBrandsAsync());
    }

    [Fact]
    public async Task A_brand_can_be_added_and_read_back()
    {
        await _store.AddBrandAsync("Citadel");

        Assert.Equal("Citadel", Assert.Single(await _store.GetBrandsAsync()).Name);
    }

    [Fact]
    public async Task The_same_brand_is_not_added_twice()
    {
        await _store.AddBrandAsync("Citadel");
        await _store.AddBrandAsync("  citadel  ");

        Assert.Single(await _store.GetBrandsAsync());
    }

    [Fact]
    public async Task A_brand_needs_a_name()
    {
        Assert.Null(await _store.AddBrandAsync("   "));
        Assert.Empty(await _store.GetBrandsAsync());
    }

    [Fact]
    public async Task A_brand_can_be_renamed()
    {
        var brand = await _store.AddBrandAsync("Citdel");

        await _store.RenameBrandAsync(brand!.Id, "Citadel");

        Assert.Equal("Citadel", Assert.Single(await _store.GetBrandsAsync()).Name);
    }

    [Fact]
    public async Task Ranges_belong_to_their_brand()
    {
        var citadel = await _store.AddBrandAsync("Citadel");
        var vallejo = await _store.AddBrandAsync("Vallejo");
        await _store.AddRangeAsync(citadel!.Id, "Base");
        await _store.AddRangeAsync(citadel.Id, "Contrast");
        await _store.AddRangeAsync(vallejo!.Id, "Model Color");

        Assert.Equal(["Base", "Contrast"], await _store.RangesForAsync("Citadel"));
        Assert.Equal(["Model Color"], await _store.RangesForAsync("Vallejo"));
    }

    [Fact]
    public async Task The_brand_is_matched_whatever_the_casing_or_spacing()
    {
        var citadel = await _store.AddBrandAsync("Citadel");
        await _store.AddRangeAsync(citadel!.Id, "Base");

        Assert.Equal(["Base"], await _store.RangesForAsync("  citadel  "));
    }

    [Fact]
    public async Task Two_brands_may_each_have_a_range_of_the_same_name()
    {
        // Several makers sell something called "Air".
        var citadel = await _store.AddBrandAsync("Citadel");
        var vallejo = await _store.AddBrandAsync("Vallejo");

        await _store.AddRangeAsync(citadel!.Id, "Air");
        await _store.AddRangeAsync(vallejo!.Id, "Air");

        Assert.Equal(["Air"], await _store.RangesForAsync("Citadel"));
        Assert.Equal(["Air"], await _store.RangesForAsync("Vallejo"));
    }

    [Fact]
    public async Task One_brand_cannot_hold_the_same_range_twice()
    {
        var citadel = await _store.AddBrandAsync("Citadel");

        await _store.AddRangeAsync(citadel!.Id, "Base");
        await _store.AddRangeAsync(citadel.Id, "BASE");

        Assert.Single(await _store.RangesForAsync("Citadel"));
    }

    [Fact]
    public async Task An_unknown_brand_offers_no_ranges_rather_than_everyones()
    {
        var citadel = await _store.AddBrandAsync("Citadel");
        await _store.AddRangeAsync(citadel!.Id, "Contrast");

        // Offering Citadel's ranges for a brand that is not Citadel is worse
        // than offering none.
        Assert.Empty(await _store.RangesForAsync("Some Cottage Brand"));
    }

    [Fact]
    public async Task No_brand_chosen_offers_no_ranges()
    {
        var citadel = await _store.AddBrandAsync("Citadel");
        await _store.AddRangeAsync(citadel!.Id, "Contrast");

        Assert.Empty(await _store.RangesForAsync(null));
        Assert.Empty(await _store.RangesForAsync(""));
    }

    [Fact]
    public async Task A_range_cannot_be_added_to_a_brand_that_is_not_there()
    {
        Assert.Null(await _store.AddRangeAsync(9999, "Base"));
    }

    [Fact]
    public async Task Deleting_a_brand_takes_its_ranges_with_it()
    {
        var citadel = await _store.AddBrandAsync("Citadel");
        await _store.AddRangeAsync(citadel!.Id, "Base");

        await _store.DeleteBrandAsync(citadel.Id);

        Assert.Empty(await _store.GetBrandsAsync());
        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.PaintRanges.CountAsync());
    }

    [Fact]
    public async Task Deleting_a_brand_leaves_paints_recorded_against_it_alone()
    {
        // This list is a set of suggestions, not the record of what is on
        // anyone's shelf.
        var citadel = await _store.AddBrandAsync("Citadel");
        await _store.AddRangeAsync(citadel!.Id, "Base");
        await _store.AddPaintAsync(new Paint { Name = "Mephiston Red", Brand = "Citadel", Range = "Base" });

        await _store.DeleteBrandAsync(citadel.Id);

        var paint = Assert.Single(await _store.GetRackAsync());
        Assert.Equal("Citadel", paint.Brand);
        Assert.Equal("Base", paint.Range);
    }

    [Fact]
    public async Task A_single_range_can_be_removed()
    {
        var citadel = await _store.AddBrandAsync("Citadel");
        await _store.AddRangeAsync(citadel!.Id, "Base");
        var contrast = await _store.AddRangeAsync(citadel.Id, "Contrast");

        await _store.DeleteRangeAsync(contrast!.Id);

        Assert.Equal(["Base"], await _store.RangesForAsync("Citadel"));
    }

    [Fact]
    public async Task Brand_suggestions_narrow_as_you_type()
    {
        await _store.AddBrandAsync("Citadel");
        await _store.AddBrandAsync("Vallejo");

        Assert.Equal(["Citadel"], await _store.SuggestBrandsAsync("cit"));
        Assert.Equal(2, (await _store.SuggestBrandsAsync("")).Count);
    }

    public void Dispose() => _conn.Dispose();
}
