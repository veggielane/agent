using System.Collections.Concurrent;
using Agent.Core.Channels;

namespace Agent.Core.Events;

/// <summary>Idempotency for polled items and WebSocket posts.</summary>
public interface IProcessedEventStore
{
    /// <summary>Returns false if the event was already processed. Marks it processed otherwise.</summary>
    Task<bool> TryMarkProcessedAsync(Channel channel, string eventId, CancellationToken cancellationToken = default);
}

/// <summary>Poll watermarks and last-seen markers, keyed by an arbitrary string ("jira:PROJ", "gitlab:group/team").</summary>
public interface ICursorStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
}

public sealed class InMemoryProcessedEventStore : IProcessedEventStore
{
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

    public Task<bool> TryMarkProcessedAsync(Channel channel, string eventId, CancellationToken cancellationToken = default)
        => Task.FromResult(_seen.TryAdd($"{channel}:{eventId}", 0));

    public int Count => _seen.Count;
}

public sealed class InMemoryCursorStore : ICursorStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }
}
