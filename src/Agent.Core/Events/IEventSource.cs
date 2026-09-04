namespace Agent.Core.Events;

/// <summary>
/// Marker for hosted services that produce <see cref="InboundEvent"/>s (WebSocket listener, pollers, API).
/// Exists so <c>!status</c> can enumerate them; each exposes its own health line.
/// </summary>
public interface IEventSource
{
    string Name { get; }

    /// <summary>One line: connected / last poll / last error.</summary>
    string Status { get; }
}
