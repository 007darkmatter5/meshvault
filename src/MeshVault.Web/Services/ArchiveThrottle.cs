namespace MeshVault.Web.Services;

/// <summary>
/// Caps how many archive downloads read the library at once.
/// </summary>
/// <remarks>
/// The share is the scarce resource — measured at about 1.4 MB/s, and saturated
/// by a single reader. Three people zipping collections at the same time does
/// not get any of them their files sooner; it just makes the library unusable
/// for everyone else while it happens. Two, for the same reason
/// <see cref="ThumbnailService"/> settled on three: a little overlap covers the
/// gaps between reads without turning into contention.
///
/// A waiting request holds nothing but a slot in the queue, and cancels with the
/// browser. Downloads take minutes, so a queued one is a person waiting a while
/// rather than a request that has failed.
/// </remarks>
public sealed class ArchiveThrottle
{
    private readonly SemaphoreSlim _slots = new(2, 2);

    /// <summary>Waits for a slot. Dispose the result to give it back.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _slots.WaitAsync(ct);
        return new Slot(_slots);
    }

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            slots.Release();
        }
    }
}
