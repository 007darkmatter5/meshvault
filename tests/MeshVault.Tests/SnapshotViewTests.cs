using System.Globalization;
using MeshVault.Web.Endpoints;

namespace MeshVault.Tests;

/// <summary>
/// The camera saved alongside a snapshot, so reopening a model shows the angle
/// its card image was taken from. It arrives as query text from the browser and
/// is fed straight to the viewer, so nonsense has to be turned away here.
/// </summary>
public class SnapshotViewTests
{
    [Fact]
    public void A_normal_view_is_kept()
    {
        Assert.Equal((1.8, 1.4, 2.4), MediaEndpoints.ParseView("1.8", "1.4", "2.4"));
    }

    [Fact]
    public void Negative_components_are_kept_because_the_camera_orbits()
    {
        Assert.Equal((-1.8, 0.5, -2.4), MediaEndpoints.ParseView("-1.8", "0.5", "-2.4"));
    }

    [Theory]
    [InlineData(null, "1", "1")]
    [InlineData("1", null, "1")]
    [InlineData("1", "1", null)]
    [InlineData("", "1", "1")]
    public void A_partial_view_is_no_view(string? x, string? y, string? z)
    {
        // Snapshots taken before the viewer recorded its camera send nothing,
        // and those models keep the default framing rather than a broken one.
        Assert.Null(MediaEndpoints.ParseView(x, y, z));
    }

    [Theory]
    [InlineData("not a number")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e400")]
    public void Values_that_are_not_finite_numbers_are_refused(string bad)
    {
        Assert.Null(MediaEndpoints.ParseView(bad, "1", "1"));
    }

    [Fact]
    public void A_camera_at_the_origin_is_refused()
    {
        // There is no direction to look from, and the viewer divides by the
        // length of this vector.
        Assert.Null(MediaEndpoints.ParseView("0", "0", "0"));
    }

    [Fact]
    public void An_absurd_distance_is_refused()
    {
        // A thousand bounding radii away is not a view anyone chose.
        Assert.Null(MediaEndpoints.ParseView("5000", "0", "0"));
    }

    [Fact]
    public void Decimals_are_read_the_same_way_in_every_locale()
    {
        // The viewer writes these with toFixed, which is always invariant. A
        // server in a comma-decimal locale must not read 0.55 as 55.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var view = MediaEndpoints.ParseView("0.55", "0.42", "0.72");

            Assert.NotNull(view);
            Assert.Equal(0.55, view!.Value.X, precision: 6);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
