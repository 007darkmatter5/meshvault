using MeshVault.Core.Models;

namespace MeshVault.Tests;

/// <summary>
/// What each stock state means for "could I paint this today", and the brand
/// suggestions behind the rack's dropdowns.
/// </summary>
public class PaintStockTests
{
    [Theory]
    [InlineData(PaintStock.Have, true)]
    [InlineData(PaintStock.Low, true)]
    [InlineData(PaintStock.Out, false)]
    [InlineData(PaintStock.Want, false)]
    public void Only_what_is_really_there_counts_as_on_the_shelf(PaintStock stock, bool expected)
    {
        // Running low is still paint. Wanting a bottle is a plan, not a bottle.
        Assert.Equal(expected, stock.IsOnTheShelf());
    }

    [Fact]
    public void The_stored_numbers_never_move()
    {
        // These are already in people's databases. Renumbering would silently
        // turn every "have" into something else.
        Assert.Equal(0, (int)PaintStock.Have);
        Assert.Equal(1, (int)PaintStock.Low);
        Assert.Equal(2, (int)PaintStock.Out);
        Assert.Equal(3, (int)PaintStock.Want);
    }

    [Fact]
    public void A_known_brand_offers_its_own_ranges()
    {
        var ranges = PaintBrands.RangesFor("Citadel").ToList();

        Assert.Contains("Contrast", ranges);
        Assert.DoesNotContain("Speedpaint", ranges);
    }

    [Fact]
    public void The_brand_is_matched_whatever_the_casing_or_spacing()
    {
        Assert.Contains("Contrast", PaintBrands.RangesFor("  citadel  "));
    }

    [Fact]
    public void An_unknown_brand_offers_everything_rather_than_nothing()
    {
        // Somebody always owns a brand this list has not heard of, and an empty
        // dropdown reads as broken.
        var ranges = PaintBrands.RangesFor("Some Cottage Brand").ToList();

        Assert.NotEmpty(ranges);
        Assert.Contains("Speedpaint", ranges);
    }

    [Fact]
    public void No_brand_chosen_yet_still_suggests_ranges()
    {
        Assert.NotEmpty(PaintBrands.RangesFor(null));
        Assert.NotEmpty(PaintBrands.RangesFor(""));
    }

    [Fact]
    public void Ranges_offered_for_an_unknown_brand_are_not_duplicated()
    {
        // Several brands have a range called "Base" or "Air".
        var ranges = PaintBrands.RangesFor("Unknown").ToList();

        Assert.Equal(ranges.Count, ranges.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void The_brand_list_is_not_empty_and_is_sorted()
    {
        var brands = PaintBrands.Brands.ToList();

        Assert.NotEmpty(brands);
        Assert.Equal(brands.OrderBy(b => b, StringComparer.OrdinalIgnoreCase), brands);
    }
}
