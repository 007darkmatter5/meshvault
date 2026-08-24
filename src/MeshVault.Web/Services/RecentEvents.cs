using System.Collections.Concurrent;
using System.Text;

namespace MeshVault.Web.Services;

public record LoggedEvent(DateTimeOffset When, LogLevel Level, string Category, string Message);

/// <summary>
/// Keeps the last few hundred warnings and errors in memory so the diagnostics
/// page can show them.
/// </summary>
/// <remarks>
/// On a self-hosted box the logs are behind <c>docker logs</c>, which many
/// people never look at and cannot easily paste into a bug report. Everything
/// below Warning is dropped: this is a tail of what went wrong, not a log file.
/// </remarks>
public class RecentEvents
{
    public const int Capacity = 200;

    private readonly ConcurrentQueue<LoggedEvent> _events = new();

    public void Add(LoggedEvent entry)
    {
        _events.Enqueue(entry);

        // Bounded so a server that has been up for months cannot grow this
        // without limit.
        while (_events.Count > Capacity && _events.TryDequeue(out _)) { }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<LoggedEvent> Snapshot() => _events.Reverse().ToList();

    public void Clear()
    {
        while (_events.TryDequeue(out _)) { }
    }
}

public sealed class RecentEventsLoggerProvider(RecentEvents events) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Sink(events, categoryName);

    public void Dispose() { }

    private sealed class Sink(RecentEvents events, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var text = new StringBuilder(formatter(state, exception));
            if (exception is not null)
            {
                // The type and message, not the stack: this is read in a browser
                // and the first line is what identifies the fault.
                text.Append(" — ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
            }

            events.Add(new LoggedEvent(DateTimeOffset.UtcNow, logLevel, category, text.ToString()));
        }
    }
}
