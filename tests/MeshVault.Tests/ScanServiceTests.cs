using MeshVault.Core.Models;
using MeshVault.Core.Services;
using MeshVault.Data;
using MeshVault.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshVault.Tests;

public class ScanServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mv-scan-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly ServiceProvider _services;

    public ScanServiceTests()
    {
        Directory.CreateDirectory(_root);
        _conn.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<MeshVaultDbContext>(o => o.UseSqlite(_conn));
        services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<MeshVaultDbContext>>().CreateDbContext());
        services.AddSingleton<FolderScanner>();
        services.AddSingleton<VariantClassifier>();
        services.AddScoped<LibraryIndexer>();
        _services = services.BuildServiceProvider();

        using var db = _services.GetRequiredService<IDbContextFactory<MeshVaultDbContext>>().CreateDbContext();
        db.Database.EnsureCreated();
        db.Libraries.Add(new Library { Name = "Test", Path = _root });
        db.SaveChanges();
    }

    private ScanService NewService() => new(
        _services.GetRequiredService<IServiceScopeFactory>(),
        NullLogger<ScanService>.Instance);

    private void File_(string relative)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, "x");
    }

    /// <summary>
    /// Generous on purpose: these wait on real background work, and a short
    /// deadline turns a busy machine into a spurious failure. A passing run
    /// still finishes in milliseconds.
    /// </summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Waits for a scan to settle without polling IsRunning, which is the very
    /// thing under test.
    /// </summary>
    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 60_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return false;
    }

    /// <summary>
    /// Regression guard: the completion event used to be raised before the
    /// running flag was cleared, so the UI redrew as "Scanning..." and never
    /// heard about it again.
    /// </summary>
    [Fact]
    public async Task Completion_event_sees_the_library_as_no_longer_running()
    {
        File_("Dragon/dragon.stl");
        var scans = NewService();

        var runningWhenNotified = new List<bool>();
        scans.Changed += () => runningWhenNotified.Add(scans.IsRunning(1));

        Assert.True(scans.TryStart(1));
        Assert.True(await WaitFor(() => runningWhenNotified.Count >= 2));

        // First event is the start (running), the last is completion (finished).
        Assert.True(runningWhenNotified[0]);
        Assert.False(runningWhenNotified[^1]);
    }

    [Fact]
    public async Task Status_after_completion_reports_finished_with_a_result()
    {
        File_("Dragon/dragon.stl");
        File_("Boat/benchy.3mf");
        var scans = NewService();

        var done = new TaskCompletionSource();
        scans.Changed += () => { if (!scans.IsRunning(1)) done.TrySetResult(); };

        scans.TryStart(1);
        await done.Task.WaitAsync(TestTimeout);

        var status = scans.GetStatus(1);
        Assert.NotNull(status);
        Assert.False(status.Running);
        Assert.False(scans.IsRunning(1));
        Assert.Equal(2, status.Result?.Added);
    }

    [Fact]
    public async Task A_failing_scan_still_clears_the_running_flag()
    {
        var scans = NewService();

        // Library 99 does not exist, so the indexer throws.
        var done = new TaskCompletionSource();
        scans.Changed += () => { if (!scans.IsRunning(99)) done.TrySetResult(); };

        scans.TryStart(99);
        await done.Task.WaitAsync(TestTimeout);

        Assert.False(scans.IsRunning(99));
        var status = scans.GetStatus(99);
        Assert.NotNull(status);
        Assert.False(status.Running);
        Assert.Contains("Failed", status.Message);
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_stop_other_subscribers()
    {
        File_("Dragon/dragon.stl");
        var scans = NewService();

        var secondWasCalled = false;
        scans.Changed += () => throw new InvalidOperationException("circuit gone");
        scans.Changed += () => secondWasCalled = true;

        scans.TryStart(1);

        Assert.True(await WaitFor(() => secondWasCalled && !scans.IsRunning(1)));
    }

    [Fact]
    public async Task Progress_is_reported_while_a_scan_runs()
    {
        // Enough folders that the scan outlives at least one reporting interval.
        for (var i = 0; i < 400; i++) File_($"Model{i:D4}/part.stl");

        var indexer = _services.CreateScope().ServiceProvider.GetRequiredService<LibraryIndexer>();
        var reports = new List<ScanProgress>();

        var result = await indexer.IndexAsync(1, new SyncProgress<ScanProgress>(reports.Add));

        Assert.Equal(400, result.Added);
        Assert.NotEmpty(reports);
        // Counts only ever move forwards.
        Assert.Equal(reports.Select(r => r.ModelsSeen).OrderBy(x => x), reports.Select(r => r.ModelsSeen));
        Assert.Equal("Saving...", reports[^1].CurrentFolder);
    }

    [Fact]
    public void Starting_a_scan_twice_is_refused_while_it_runs()
    {
        File_("Dragon/dragon.stl");
        var scans = NewService();

        Assert.True(scans.TryStart(1));
        Assert.False(scans.TryStart(1));
    }

    public void Dispose()
    {
        _services.Dispose();
        _conn.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
