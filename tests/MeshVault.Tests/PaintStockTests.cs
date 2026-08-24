using MeshVault.Core.Models;

namespace MeshVault.Tests;

/// <summary>What each stock state means for "could I paint this today".</summary>
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
}
