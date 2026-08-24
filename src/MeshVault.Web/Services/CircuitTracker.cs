using Microsoft.AspNetCore.Components.Server.Circuits;

namespace MeshVault.Web.Services;

/// <summary>
/// Counts live Blazor circuits.
/// </summary>
/// <remarks>
/// Every button, dialog and filter in the app runs over a circuit. When one
/// cannot be established — a reverse proxy that will not pass WebSockets is the
/// usual cause — pages still render from their prerendered HTML and simply stop
/// responding, which is indistinguishable from a broken feature. A count of
/// zero connections against a server that is plainly serving pages says the
/// problem is the transport rather than the page.
/// </remarks>
public class CircuitTracker(ILogger<CircuitTracker> log) : CircuitHandler
{
    private int _open;

    public int Open => Volatile.Read(ref _open);
    public int EverOpened { get; private set; }
    public DateTimeOffset? LastOpenedUtc { get; private set; }
    public DateTimeOffset? LastClosedUtc { get; private set; }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken ct)
    {
        Interlocked.Increment(ref _open);
        EverOpened++;
        LastOpenedUtc = DateTimeOffset.UtcNow;
        log.LogDebug("Circuit {Id} opened ({Open} open)", circuit.Id, Open);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken ct)
    {
        Interlocked.Decrement(ref _open);
        LastClosedUtc = DateTimeOffset.UtcNow;
        log.LogDebug("Circuit {Id} closed ({Open} open)", circuit.Id, Open);
        return Task.CompletedTask;
    }
}
