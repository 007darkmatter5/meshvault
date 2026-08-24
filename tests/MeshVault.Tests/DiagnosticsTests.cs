using MeshVault.Web.Services;
using Microsoft.Extensions.Logging;

namespace MeshVault.Tests;

/// <summary>
/// The diagnostics buffer is the only place an operator can read what went
/// wrong without reaching for <c>docker logs</c>, so its two rules — keep the
/// newest, stay bounded — need pinning.
/// </summary>
public class RecentEventsTests
{
    private static ILogger LoggerFor(RecentEvents events, string category = "Test") =>
        new RecentEventsLoggerProvider(events).CreateLogger(category);

    [Fact]
    public void Warnings_and_errors_are_kept()
    {
        var events = new RecentEvents();
        var log = LoggerFor(events);

        log.LogWarning("disk is nearly full");
        log.LogError("could not read the library");

        var kept = events.Snapshot();
        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, e => e.Message.Contains("nearly full"));
        Assert.Contains(kept, e => e.Message.Contains("could not read"));
    }

    [Fact]
    public void Routine_chatter_is_dropped()
    {
        var events = new RecentEvents();
        var log = LoggerFor(events);

        log.LogInformation("scanned 400 models");
        log.LogDebug("opened a circuit");
        log.LogTrace("read a byte");

        Assert.Empty(events.Snapshot());
    }

    [Fact]
    public void The_exception_type_and_message_are_recorded()
    {
        var events = new RecentEvents();

        LoggerFor(events).LogError(new IOException("the mount went away"), "Scan failed");

        var entry = Assert.Single(events.Snapshot());
        Assert.Contains("Scan failed", entry.Message);
        Assert.Contains("IOException", entry.Message);
        Assert.Contains("the mount went away", entry.Message);
    }

    [Fact]
    public void The_newest_entries_come_first()
    {
        var events = new RecentEvents();
        var log = LoggerFor(events);

        log.LogWarning("first");
        log.LogWarning("second");

        Assert.Equal("second", events.Snapshot()[0].Message);
    }

    [Fact]
    public void A_server_up_for_months_cannot_grow_the_buffer_without_limit()
    {
        var events = new RecentEvents();
        var log = LoggerFor(events);

        for (var i = 0; i < RecentEvents.Capacity * 3; i++) log.LogWarning("event {Index}", i);

        var kept = events.Snapshot();
        Assert.Equal(RecentEvents.Capacity, kept.Count);

        // Oldest dropped rather than newest, so the tail shows what just broke.
        Assert.Equal($"event {RecentEvents.Capacity * 3 - 1}", kept[0].Message);
    }

    [Fact]
    public void The_category_is_carried_so_a_report_says_what_logged_it()
    {
        var events = new RecentEvents();

        LoggerFor(events, "MeshVault.Web.Services.ThumbnailService").LogWarning("render failed");

        Assert.Equal("MeshVault.Web.Services.ThumbnailService", events.Snapshot()[0].Category);
    }
}

/// <summary>
/// The text report is what gets pasted into a bug report, so the facts that
/// decide a diagnosis have to survive into it.
/// </summary>
public class DiagnosticsTextTests
{
    private static DiagnosticsSnapshot Sample(
        IReadOnlyList<LibraryCheck>? libraries = null,
        IReadOnlyList<LoggedEvent>? events = null,
        PathCheck? dataPath = null,
        int openCircuits = 1) =>
        new(
            Version: "1.2.3+abcdef",
            Environment: "Production",
            Framework: ".NET 10.0.0",
            OperatingSystem: "Linux 6.6",
            InContainer: true,
            Uptime: TimeSpan.FromHours(5),
            TakenUtc: new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero),
            DataPath: dataPath ?? new PathCheck("/data", true, true, true, null),
            DatabaseBytes: 4096,
            ThumbnailFiles: 12,
            GeometryFiles: 3,
            ThumbnailRenderVersion: 2,
            GeometryFormatVersion: 2,
            Libraries: libraries ?? [],
            Models: 10, Files: 20, Designers: 2, Collections: 1, Tags: 5, Users: 1,
            Thumbnails: new ThumbnailProgress(4, 1, 6, "Benchy"),
            ThumbnailsPaused: false,
            OpenCircuits: openCircuits, CircuitsEverOpened: 7,
            LastCircuitOpenedUtc: new DateTimeOffset(2026, 8, 24, 11, 0, 0, TimeSpan.Zero),
            LastCircuitClosedUtc: null,
            RecentEvents: events ?? []);

    [Fact]
    public void The_version_and_environment_are_reported()
    {
        var text = DiagnosticsReport.ToText(Sample());

        Assert.Contains("1.2.3+abcdef", text);
        Assert.Contains("Production (container)", text);
    }

    [Fact]
    public void An_unreachable_library_says_so_rather_than_reading_as_empty()
    {
        var text = DiagnosticsReport.ToText(Sample(libraries:
        [
            new LibraryCheck("Models", "/models", false, false, false, 0, null, "Not found from inside the container.")
        ]));

        Assert.Contains("/models", text);
        Assert.Contains("exists False", text);
        Assert.Contains("Not found from inside the container.", text);
        Assert.Contains("last scan never", text);
    }

    [Fact]
    public void A_read_only_data_mount_is_called_out()
    {
        var text = DiagnosticsReport.ToText(Sample(
            dataPath: new PathCheck("/data", true, true, false, "Access to the path is denied.")));

        Assert.Contains("writable False", text);
        Assert.Contains("Access to the path is denied.", text);
    }

    [Fact]
    public void Recent_errors_are_included()
    {
        var text = DiagnosticsReport.ToText(Sample(events:
        [
            new LoggedEvent(new DateTimeOffset(2026, 8, 24, 11, 59, 0, TimeSpan.Zero),
                LogLevel.Error, "MeshVault.Web", "everything is on fire")
        ]));

        Assert.Contains("Recent warnings and errors (1)", text);
        Assert.Contains("everything is on fire", text);
    }

    [Fact]
    public void No_errors_reads_as_none_rather_than_a_blank_gap()
    {
        Assert.Contains("none", DiagnosticsReport.ToText(Sample()));
    }

    [Fact]
    public void The_circuit_count_is_reported_because_zero_is_the_tell()
    {
        var text = DiagnosticsReport.ToText(Sample(openCircuits: 0));

        Assert.Contains("0 open, 7 since start", text);
    }
}
