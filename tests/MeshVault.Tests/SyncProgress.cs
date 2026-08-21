namespace MeshVault.Tests;

/// <summary>
/// Invokes its callback inline.
/// </summary>
/// <remarks>
/// <see cref="Progress{T}"/> posts to the captured synchronization context, or
/// the thread pool when there is none, so reports can still be in flight after
/// the reporting method returns. Tests that assert on the collected reports
/// were intermittently seeing an empty or short list because of it.
/// </remarks>
public sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}
