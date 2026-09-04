using Microsoft.Extensions.Logging;

namespace Agent.Core.Audit;

/// <summary>Default sink: structured log line per entry. Persistence replaces it with a database-backed sink.</summary>
public sealed class LoggingAuditSink : IAuditSink
{
    private readonly ILogger<LoggingAuditSink> _logger;

    public LoggingAuditSink(ILogger<LoggingAuditSink> logger) => _logger = logger;

    public ValueTask WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "audit {Outcome} {Action} channel={Channel} caller={CallerId} ({CallerName}) roles=[{Roles}] {Detail}",
            entry.Outcome,
            entry.Action,
            entry.Channel,
            entry.CallerId,
            entry.CallerName,
            entry.Roles is null ? string.Empty : string.Join(",", entry.Roles),
            entry.Detail);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Keeps entries in memory. Used by tests and the in-process CLI.</summary>
public sealed class InMemoryAuditSink : IAuditSink
{
    private readonly List<AuditEntry> _entries = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<AuditEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToArray();
            }
        }
    }

    public ValueTask WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _entries.Add(entry);
        }

        return ValueTask.CompletedTask;
    }
}
