using System.Diagnostics;
using MeshVault.Web.Services;

namespace MeshVault.Tests;

/// <summary>
/// Background work must yield to a user waiting on a request. The library share
/// is saturated by one reader, so a model someone opened could otherwise sit
/// behind a queue of thumbnail reads and look like it had hung.
/// </summary>
public class ForegroundActivityTests
{
    [Fact]
    public void Idle_by_default()
    {
        Assert.False(new ForegroundActivity().IsBusy);
    }

    [Fact]
    public void Busy_while_a_request_is_in_flight()
    {
        var activity = new ForegroundActivity();

        using (activity.Begin())
        {
            Assert.True(activity.IsBusy);
        }

        // Still inside the cooldown immediately after.
        Assert.True(activity.IsBusy);
    }

    [Fact]
    public void Concurrent_requests_are_counted()
    {
        var activity = new ForegroundActivity();

        var first = activity.Begin();
        var second = activity.Begin();

        first.Dispose();
        Assert.True(activity.IsBusy);

        second.Dispose();
        Assert.True(activity.IsBusy); // cooldown
    }

    [Fact]
    public void Disposing_twice_does_not_unbalance_the_count()
    {
        var activity = new ForegroundActivity();

        var scope = activity.Begin();
        scope.Dispose();
        scope.Dispose();

        // A double dispose must not drive the counter negative, which would
        // leave IsBusy stuck false while a real request was running.
        using (activity.Begin())
        {
            Assert.True(activity.IsBusy);
        }
    }

    [Fact]
    public async Task Waiting_returns_promptly_when_nothing_is_in_flight()
    {
        var activity = new ForegroundActivity();

        var elapsed = Stopwatch.StartNew();
        await activity.WaitWhileBusyAsync(CancellationToken.None);
        elapsed.Stop();

        Assert.True(elapsed.ElapsedMilliseconds < 1000, $"waited {elapsed.ElapsedMilliseconds}ms while idle");
    }

    [Fact]
    public async Task Waiting_can_be_cancelled_rather_than_blocking_shutdown()
    {
        var activity = new ForegroundActivity();
        using var scope = activity.Begin();
        using var cts = new CancellationTokenSource(200);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => activity.WaitWhileBusyAsync(cts.Token));
    }
}
