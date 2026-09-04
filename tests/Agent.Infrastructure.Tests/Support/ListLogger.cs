using Microsoft.Extensions.Logging;

namespace Agent.Infrastructure.Tests.Support;

public sealed record LogLine(LogLevel Level, string Message, Exception? Exception);

/// <summary>Captures log lines so tests can assert on them.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public List<LogLine> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Lines.Add(new LogLine(logLevel, formatter(state, exception), exception));
}
