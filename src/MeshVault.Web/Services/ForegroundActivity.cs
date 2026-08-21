namespace MeshVault.Web.Services;

/// <summary>
/// Tracks whether someone is waiting on a request right now, so background
/// workers can stand aside.
/// </summary>
/// <remarks>
/// The library share is the scarce resource: measured at about 1.4 MB/s, it is
/// saturated by a single reader. With the thumbnail worker running flat out, a
/// model the user actually opened could sit behind 30-odd queued reads and look
/// like it had hung. Foreground work now wins.
/// </remarks>
public class ForegroundActivity
{
    private int _active;

    /// <summary>Keeps background work paused for a moment after the last request finishes.</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(3);

    private DateTimeOffset _lastFinished = DateTimeOffset.MinValue;

    public bool IsBusy =>
        Volatile.Read(ref _active) > 0 || DateTimeOffset.UtcNow - _lastFinished < Cooldown;

    /// <summary>Marks a user-facing read as in progress until the result is disposed.</summary>
    public IDisposable Begin() => new Scope(this);

    private sealed class Scope : IDisposable
    {
        private readonly ForegroundActivity _owner;
        private bool _disposed;

        public Scope(ForegroundActivity owner)
        {
            _owner = owner;
            Interlocked.Increment(ref owner._active);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Interlocked.Decrement(ref _owner._active);
            _owner._lastFinished = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Waits while a user is being served. Returns once the coast is clear.</summary>
    public async Task WaitWhileBusyAsync(CancellationToken ct)
    {
        while (IsBusy)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(500, ct);
        }
    }
}
