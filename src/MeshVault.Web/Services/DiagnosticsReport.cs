using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using MeshVault.Core.Imaging;
using MeshVault.Core.Meshes;
using MeshVault.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeshVault.Web.Services;

public record PathCheck(string Path, bool Exists, bool Readable, bool Writable, string? Problem);

public record LibraryCheck(string Name, string Path, bool AllowOrganize, bool Exists, bool Readable,
    int Models, DateTimeOffset? LastScannedUtc, string? Problem);

/// <summary>
/// Whether the browser's copy of Blazor is actually being served: the file on
/// disk, and the route that would answer for it.
/// </summary>
/// <remarks>
/// These are separate failures with one symptom. A missing file means the image
/// was built wrong; a missing route means the app is serving from somewhere it
/// did not expect, and answers a 404 for a file that is sitting right there.
/// Either way nothing on any page responds, and neither is visible from the
/// browser, which only ever sees the 404.
/// </remarks>
public record ScriptDelivery(
    string WebRoot, bool WebRootExists, bool FileOnDisk, long FileBytes,
    int FrameworkRoutes, int StaticRoutes, IReadOnlyList<string> BlazorRoutes);

public record DiagnosticsSnapshot(
    string Version,
    string Environment,
    string Framework,
    string OperatingSystem,
    bool InContainer,
    TimeSpan Uptime,
    DateTimeOffset TakenUtc,
    string ContentRoot,
    ScriptDelivery Scripts,
    PathCheck DataPath,
    long DatabaseBytes,
    int ThumbnailFiles,
    int GeometryFiles,
    int ThumbnailRenderVersion,
    int GeometryFormatVersion,
    IReadOnlyList<LibraryCheck> Libraries,
    int Models, int Files, int Designers, int Collections, int Tags, int Users,
    ThumbnailProgress Thumbnails,
    bool ThumbnailsPaused,
    int OpenCircuits, int CircuitsEverOpened,
    DateTimeOffset? LastCircuitOpenedUtc, DateTimeOffset? LastCircuitClosedUtc,
    IReadOnlyList<LoggedEvent> RecentEvents);

/// <summary>
/// Gathers everything worth knowing when this instance is misbehaving, in one
/// pass, so an operator can paste a single report into a bug report instead of
/// being asked a dozen questions one at a time.
/// </summary>
public class DiagnosticsReport(
    IDbContextFactory<MeshVaultDbContext> factory,
    IOptions<MeshVaultOptions> options,
    IWebHostEnvironment environment,
    EndpointDataSource endpoints,
    ThumbnailService thumbnails,
    CircuitTracker circuits,
    RecentEvents events)
{
    public async Task<DiagnosticsSnapshot> TakeAsync(CancellationToken ct = default)
    {
        var dataPath = System.IO.Path.GetFullPath(options.Value.DataPath);
        await using var db = await factory.CreateDbContextAsync(ct);

        var libraries = new List<LibraryCheck>();
        foreach (var library in await db.Libraries.AsNoTracking().OrderBy(l => l.Name).ToListAsync(ct))
        {
            var (exists, readable, problem) = Probe(library.Path);
            libraries.Add(new LibraryCheck(
                library.Name, library.Path, library.AllowOrganize, exists, readable,
                await db.Models.CountAsync(m => m.LibraryId == library.Id, ct),
                library.LastScannedUtc, problem));
        }

        return new DiagnosticsSnapshot(
            Version: Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "unknown",
            Environment: environment.EnvironmentName,
            Framework: RuntimeInformation.FrameworkDescription,
            OperatingSystem: RuntimeInformation.OSDescription,
            InContainer: System.Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true",
            Uptime: DateTimeOffset.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            TakenUtc: DateTimeOffset.UtcNow,
            ContentRoot: environment.ContentRootPath,
            Scripts: CheckScriptDelivery(),
            DataPath: CheckWritable(dataPath),
            DatabaseBytes: Length(System.IO.Path.Combine(dataPath, "meshvault.db")),
            ThumbnailFiles: CountFiles(System.IO.Path.Combine(dataPath, "thumbnails")),
            GeometryFiles: CountFiles(System.IO.Path.Combine(dataPath, "geometry")),
            ThumbnailRenderVersion: ThumbnailStore.RenderVersion,
            GeometryFormatVersion: GeometryCache.FormatVersion,
            Libraries: libraries,
            Models: await db.Models.CountAsync(ct),
            Files: await db.Files.CountAsync(ct),
            Designers: await db.Designers.CountAsync(ct),
            Collections: await db.Collections.CountAsync(ct),
            Tags: await db.Tags.CountAsync(ct),
            Users: await db.Users.CountAsync(ct),
            Thumbnails: thumbnails.Progress,
            ThumbnailsPaused: thumbnails.IsPaused,
            OpenCircuits: circuits.Open,
            CircuitsEverOpened: circuits.EverOpened,
            LastCircuitOpenedUtc: circuits.LastOpenedUtc,
            LastCircuitClosedUtc: circuits.LastClosedUtc,
            RecentEvents: events.Snapshot());
    }

    /// <summary>
    /// Renders the snapshot as plain text for copying. A bug report that already
    /// answers "what version, what mounts, what errors" needs no round trip.
    /// </summary>
    public static string ToText(DiagnosticsSnapshot s)
    {
        var text = new StringBuilder();
        text.AppendLine("MeshVault diagnostics");
        text.AppendLine($"Taken           {s.TakenUtc:u}");
        text.AppendLine($"Version         {s.Version}");
        text.AppendLine($"Environment     {s.Environment}{(s.InContainer ? " (container)" : "")}");
        text.AppendLine($"Runtime         {s.Framework}");
        text.AppendLine($"OS              {s.OperatingSystem}");
        text.AppendLine($"Uptime          {s.Uptime:d'd 'hh':'mm':'ss}");
        text.AppendLine();
        text.AppendLine($"Content root    {s.ContentRoot}");
        text.AppendLine($"Web root        {s.Scripts.WebRoot} (exists {s.Scripts.WebRootExists})");
        text.AppendLine($"blazor.web.js   on disk {s.Scripts.FileOnDisk}, {s.Scripts.FileBytes:N0} bytes");
        text.AppendLine($"                {s.Scripts.FrameworkRoutes} _framework route(s) of "
            + $"{s.Scripts.StaticRoutes} mapped");
        foreach (var route in s.Scripts.BlazorRoutes)
            text.AppendLine("                  /" + route.TrimStart('/'));
        if (s.Scripts.BlazorRoutes.Count == 0)
            text.AppendLine("                  no route mentions blazor - nothing will serve the script");
        text.AppendLine();
        text.AppendLine($"Data path       {s.DataPath.Path}");
        text.AppendLine($"                exists {s.DataPath.Exists}, readable {s.DataPath.Readable}, "
            + $"writable {s.DataPath.Writable}"
            + (s.DataPath.Problem is null ? "" : $" - {s.DataPath.Problem}"));
        text.AppendLine($"Database        {s.DatabaseBytes:N0} bytes");
        text.AppendLine($"Thumbnails      {s.ThumbnailFiles:N0} files (render version {s.ThumbnailRenderVersion})");
        text.AppendLine($"Geometry cache  {s.GeometryFiles:N0} files (format version {s.GeometryFormatVersion})");
        text.AppendLine();
        text.AppendLine($"Libraries       {s.Libraries.Count}");
        foreach (var library in s.Libraries)
        {
            text.AppendLine($"  {library.Name} - {library.Path}");
            text.AppendLine($"    exists {library.Exists}, readable {library.Readable}, "
                + $"{library.Models:N0} models, organize {library.AllowOrganize}, "
                + $"last scan {library.LastScannedUtc?.ToString("u") ?? "never"}"
                + (library.Problem is null ? "" : $" - {library.Problem}"));
        }
        text.AppendLine();
        text.AppendLine($"Catalog         {s.Models:N0} models, {s.Files:N0} files, {s.Designers:N0} designers, "
            + $"{s.Collections:N0} collections, {s.Tags:N0} tags, {s.Users:N0} users");
        text.AppendLine($"Previews        {s.Thumbnails.Done:N0} done, {s.Thumbnails.Failed:N0} failed, "
            + $"{s.Thumbnails.Remaining:N0} to go{(s.ThumbnailsPaused ? ", paused" : "")}");
        text.AppendLine($"Circuits        {s.OpenCircuits} open, {s.CircuitsEverOpened} since start, "
            + $"last opened {s.LastCircuitOpenedUtc?.ToString("u") ?? "never"}");
        text.AppendLine();

        text.AppendLine($"Recent warnings and errors ({s.RecentEvents.Count})");
        if (s.RecentEvents.Count == 0) text.AppendLine("  none");
        foreach (var entry in s.RecentEvents.Take(40))
            text.AppendLine($"  {entry.When:u} {entry.Level} {entry.Category}: {entry.Message}");

        return text.ToString();
    }

    /// <summary>
    /// Asks the two questions a 404 on blazor.web.js cannot distinguish between:
    /// is the file there, and is anything mapped to serve it?
    /// </summary>
    private ScriptDelivery CheckScriptDelivery()
    {
        var webRoot = environment.WebRootPath ?? "";
        var script = System.IO.Path.Combine(webRoot, "_framework", "blazor.web.js");

        var routes = endpoints.Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? "")
            .ToList();

        return new ScriptDelivery(
            WebRoot: string.IsNullOrEmpty(webRoot) ? "(not set)" : webRoot,
            WebRootExists: !string.IsNullOrEmpty(webRoot) && Directory.Exists(webRoot),
            FileOnDisk: File.Exists(script),
            FileBytes: Length(script),
            FrameworkRoutes: routes.Count(r => r.StartsWith("_framework", StringComparison.OrdinalIgnoreCase)),
            StaticRoutes: routes.Count,
            // The script itself and the hub it connects to, and nothing else.
            // A bare "blazor" match also catches every MudBlazor asset, which
            // buries the four lines that matter.
            BlazorRoutes: routes
                .Where(r => r.Contains("blazor.web", StringComparison.OrdinalIgnoreCase)
                    || r.TrimStart('/').StartsWith("_blazor", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList());
    }

    private static (bool Exists, bool Readable, string? Problem) Probe(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return (false, false, "Not found from inside the container - check the mount.");

            // Enumerating one entry proves the mount is readable, and is far
            // cheaper than walking a library on a slow network share.
            _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToList();
            return (true, true, null);
        }
        catch (Exception ex)
        {
            return (Directory.Exists(path), false, ex.Message);
        }
    }

    private static PathCheck CheckWritable(string path)
    {
        var (exists, readable, problem) = Probe(path);
        if (!exists || !readable) return new PathCheck(path, exists, readable, false, problem);

        var probe = System.IO.Path.Combine(path, $".write-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return new PathCheck(path, true, true, true, null);
        }
        catch (Exception ex)
        {
            // A read-only /data is fatal, and otherwise only surfaces much later
            // as an obscure SQLite error.
            return new PathCheck(path, true, true, false, ex.Message);
        }
    }

    private static long Length(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int CountFiles(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count()
                : 0;
        }
        catch
        {
            return 0;
        }
    }
}
