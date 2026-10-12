using System.Text.RegularExpressions;
using MeshVault.Core.Models;
using MeshVault.Core.Services;
using MeshVault.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Tests;

/// <summary>
/// The chart is typed-in data, so what can go wrong with it is a slip of the
/// hand: a name twice, a family of five, a colour that is not one.
/// </summary>
public class PaintChartTests
{
    private static readonly PaintChart Chart = PaintCharts.WarpaintsFanatic;

    [Fact]
    public void The_chart_is_the_whole_poster()
    {
        // Thirteen rows of two families and a last row of one, six paints each.
        Assert.Equal(27, Chart.Families.Count);
        Assert.All(Chart.Families, f => Assert.Equal(6, f.Paints.Count));
        Assert.Equal(162, Chart.Paints.Count());
    }

    [Fact]
    public void No_paint_is_on_the_chart_twice()
    {
        // A rack holds one bottle per name, so a name twice on the chart would
        // light two hexagons for one bottle.
        var names = Chart.Paints.Select(p => p.NormalizedName).ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(Chart.Families.Count, Chart.Families.Select(f => f.Name).Distinct().Count());
    }

    [Fact]
    public void Every_swatch_is_a_colour_a_browser_will_draw()
    {
        // It goes into a style attribute as written; anything else there is
        // silently no colour at all.
        Assert.All(Chart.Paints, p => Assert.Matches(new Regex("^#[0-9a-f]{6}$"), p.Hex));
    }

    [Fact]
    public void A_label_is_dark_on_a_pale_paint_and_light_on_a_deep_one()
    {
        Assert.True(Find("Matt White").IsLight);
        Assert.True(Find("Ice Yellow").IsLight);
        Assert.False(Find("Matt Black").IsLight);
        Assert.False(Find("Imperial Navy").IsLight);
    }

    [Fact]
    public async Task A_paint_added_from_the_chart_is_found_on_the_rack_under_the_charts_own_key()
    {
        // The page matches a rack to the chart by NormalizedName. The two are
        // normalised in different places, and one drifting from the other
        // would show a bottle somebody owns as missing.
        await using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var factory = new Factory(conn);
        await using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();

        var store = new PaintStore(factory, new FakeUser("alice"));
        foreach (var paint in Chart.Paints)
            await store.AddPaintAsync(new Paint { Name = paint.Name, Hex = paint.Hex });

        var rack = (await store.GetRackAsync()).Select(p => p.NormalizedName).ToHashSet();

        Assert.Equal(162, rack.Count);
        Assert.All(Chart.Paints, p => Assert.Contains(p.NormalizedName, rack));
    }

    private static ChartPaint Find(string name) => Chart.Paints.Single(p => p.Name == name);

    private sealed class FakeUser(string id) : ICurrentUser
    {
        public string UserId { get; } = id;
    }

    private sealed class Factory(SqliteConnection conn) : IDbContextFactory<MeshVaultDbContext>
    {
        public MeshVaultDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<MeshVaultDbContext>().UseSqlite(conn).Options);
    }
}
