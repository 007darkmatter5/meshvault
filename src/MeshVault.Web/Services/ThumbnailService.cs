using System.Collections.Concurrent;
using MeshVault.Core.Imaging;
using MeshVault.Core.Meshes;
using MeshVault.Core.Models;
using MeshVault.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeshVault.Web.Services;

public record ThumbnailProgress(int Done, int Failed, int Remaining, string? Current);

/// <summary>
/// Renders thumbnails for mesh files in the background.
/// </summary>
/// <remarks>
/// Deliberately low concurrency. Measured on a real library held on a mapped
/// SMB drive, throughput was bandwidth-bound at about 1.4 MB/s and going from
/// one reader to eight bought only 1.25x, so extra parallelism mostly adds
/// memory pressure. Smallest files are rendered first, which fills the visible
/// grid quickly instead of stalling on a 130 MB model.
/// </remarks>
public class ThumbnailService(
    IServiceScopeFactory scopeFactory,
    ThumbnailStore store,
    GeometryCache geometry,
    ForegroundActivity foreground,
    IOptions<MeshVaultOptions> options,
    ILogger<ThumbnailService> log) : BackgroundService
{
    private const int Concurrency = 3;
    private const int BatchSize = 32;

    /// <summary>Must match the viewer endpoint, so a cached payload is the one it would build.</summary>
    public const int ViewerTriangleBudget = 250_000;

    private int _done;
    private int _failed;
    private int _remaining;
    private string? _current;

    /// <summary>Set when new work appears, so the loop can idle instead of polling hard.</summary>
    private readonly SemaphoreSlim _wakeup = new(0);
    private readonly ConcurrentDictionary<int, byte> _inFlight = new();

    public event Action? Changed;

    public ThumbnailProgress Progress => new(_done, _failed, _remaining, _current);

    public bool IsWorking => _remaining > 0 && !IsPaused;

    /// <summary>
    /// When paused the sweep stops claiming the library share, so previews can
    /// be curated by hand without competing with it. Persisted, so it survives
    /// a restart rather than quietly resuming.
    /// </summary>
    public bool IsPaused { get; private set; }

    public async Task SetPausedAsync(bool paused, CancellationToken ct = default)
    {
        if (IsPaused == paused) return;
        IsPaused = paused;

        using (var scope = scopeFactory.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
            await settings.SetBoolAsync(SettingKeys.PreviewBuildingPaused, paused, ct);
        }

        // Resuming should start work immediately rather than after the poll.
        if (!paused) Nudge();
        Notify();
    }

    private async Task LoadPausedStateAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
            IsPaused = await settings.GetBoolAsync(SettingKeys.PreviewBuildingPaused, false, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not read the preview pause setting; assuming running");
        }
    }

    /// <summary>Called after a scan so newly discovered files are picked up promptly.</summary>
    public void Nudge()
    {
        if (_wakeup.CurrentCount == 0) _wakeup.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Let startup scanning get going first.
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
        catch (OperationCanceledException) { return; }

        await LoadPausedStateAsync(ct);
        Notify();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (IsPaused)
                {
                    _current = null;
                    // Woken by SetPausedAsync; the timeout is only a safety net.
                    await _wakeup.WaitAsync(TimeSpan.FromMinutes(5), ct);
                    continue;
                }

                var processed = await ProcessBatchAsync(ct);
                if (processed > 0) continue;

                // Thumbnails are done; fill in any viewer payloads that are
                // missing, which happens after a payload format change retires
                // the old cache. Without this the first view of each model pays
                // a slow read off the library share.
                if (await BackfillGeometryAsync(ct) > 0) continue;

                _current = null;
                Notify();

                // Nothing to do: wait for a nudge, with a slow fallback poll.
                await _wakeup.WaitAsync(TimeSpan.FromMinutes(5), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                log.LogError(ex, "Thumbnail worker hit an unexpected error; backing off");
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        List<PendingFile> batch;

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeshVaultDbContext>();

            _remaining = await db.Files.CountAsync(f => f.ThumbnailState == ThumbnailState.Pending, ct);
            if (_remaining == 0) return 0;

            batch = await db.Files
                .Where(f => f.ThumbnailState == ThumbnailState.Pending)
                .OrderBy(f => f.SizeBytes)
                .Take(BatchSize)
                .Select(f => new PendingFile(
                    f.Id,
                    f.ModelEntryId,
                    f.FileName,
                    f.Extension,
                    f.SizeBytes,
                    f.ModelEntry!.Library!.Path,
                    f.RelativePath))
                .ToListAsync(ct);
        }

        Notify();

        using var gate = new SemaphoreSlim(Concurrency);
        await Task.WhenAll(batch.Select(async file =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (!_inFlight.TryAdd(file.FileId, 0)) return;

                // A batch is 32 files and each can take a minute, so pausing has to
                // take effect within the batch rather than after it.
                if (IsPaused) return;

                // Stand aside while someone is waiting on a model of their own.
                await foreground.WaitWhileBusyAsync(ct);
                await RenderOneAsync(file, ct);
            }
            finally
            {
                _inFlight.TryRemove(file.FileId, out _);
                gate.Release();
            }
        }));

        return batch.Count;
    }

    /// <summary>
    /// Builds viewer payloads for files whose thumbnail already exists but whose
    /// geometry does not. Runs only when the thumbnail queue is empty, so it
    /// never delays the visible grid.
    /// </summary>
    private async Task<int> BackfillGeometryAsync(CancellationToken ct)
    {
        List<PendingFile> batch;

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeshVaultDbContext>();

            var candidates = await db.Files
                .Where(f => f.ThumbnailState == ThumbnailState.Ready)
                .OrderBy(f => f.SizeBytes)
                .Select(f => new PendingFile(
                    f.Id, f.ModelEntryId, f.FileName, f.Extension, f.SizeBytes,
                    f.ModelEntry!.Library!.Path, f.RelativePath))
                .ToListAsync(ct);

            // Filesystem check, so it cannot be done in the query.
            batch = candidates.Where(f => !geometry.Has(f.FileId)).Take(BatchSize).ToList();
        }

        if (batch.Count == 0) return 0;

        foreach (var file in batch)
        {
            ct.ThrowIfCancellationRequested();
            if (IsPaused) break;
            await foreground.WaitWhileBusyAsync(ct);
            try
            {
                _current = $"{file.FileName} (preview)";
                Notify();

                var fullPath = Path.Combine(
                    file.LibraryPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                using var staged = await StagedMeshFile.CreateAsync(
                    fullPath, Path.Combine(options.Value.DataPath, "staging"), ct);

                var payload = await Task.Run(
                    () => MeshPayload.Build(MeshLoader.Open(staged.Path), ViewerTriangleBudget, ct), ct);
                await geometry.WriteAsync(file.FileId, payload, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Write an empty marker rather than retrying this file forever.
                log.LogWarning(ex, "Could not build viewer geometry for {File}", file.FileName);
                try { await geometry.WriteAsync(file.FileId, MeshPayload.EmptyPayload(), ct); }
                catch (Exception) { }
            }
        }

        return batch.Count;
    }

    private async Task RenderOneAsync(PendingFile file, CancellationToken ct)
    {
        var state = ThumbnailState.Failed;
        try
        {
            _current = file.FileName;
            Notify();

            var fullPath = Path.Combine(
                file.LibraryPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            // One network read; every later pass comes off local disk.
            using var staged = await StagedMeshFile.CreateAsync(
                fullPath, Path.Combine(options.Value.DataPath, "staging"), ct);

            var png = await Task.Run(() => MeshRasterizer.RenderPng(
                MeshLoader.Open(staged.Path),
                new RenderOptions { Width = 400, Height = 300 }, ct), ct);

            await store.SaveFileThumbnailAsync(file.FileId, png, ct);

            // The file is already staged locally, so building the viewer payload
            // now costs a local read instead of another trip over the share.
            if (!geometry.Has(file.FileId))
            {
                var payload = await Task.Run(
                    () => MeshPayload.Build(MeshLoader.Open(staged.Path), ViewerTriangleBudget, ct), ct);
                await geometry.WriteAsync(file.FileId, payload, ct);
            }
            state = ThumbnailState.Ready;
            Interlocked.Increment(ref _done);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A single unreadable model must not stall the queue.
            log.LogWarning(ex, "Could not render a thumbnail for {File}", file.FileName);
            Interlocked.Increment(ref _failed);
        }

        await RecordResultAsync(file, state, ct);
        Notify();
    }

    private async Task RecordResultAsync(PendingFile file, ThumbnailState state, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeshVaultDbContext>();

            await db.Files.Where(f => f.Id == file.FileId)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.ThumbnailState, state), ct);

            // First successful render becomes the model's card image.
            if (state == ThumbnailState.Ready)
            {
                await db.Models
                    .Where(m => m.Id == file.ModelEntryId && m.ThumbnailFileId == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.ThumbnailFileId, file.FileId), ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not record the thumbnail result for file {FileId}", file.FileId);
        }
    }

    private void Notify()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch (Exception ex) { log.LogWarning(ex, "A thumbnail subscriber threw; continuing"); }
        }
    }

    private record PendingFile(
        int FileId, int ModelEntryId, string FileName, string Extension,
        long SizeBytes, string LibraryPath, string RelativePath);
}
