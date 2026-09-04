using Agent.Core.Authorization;
using Agent.Core.Channels;

namespace Agent.Core.Audit;

public sealed record AuditEntry(
    DateTimeOffset Timestamp,
    Channel Channel,
    string CallerId,
    string? CallerName,
    string Action,
    string Outcome,
    string? Detail,
    IReadOnlyCollection<Role>? Roles);

/// <summary>Receives every authorization decision and every command / tool invocation.</summary>
public interface IAuditSink
{
    ValueTask WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}
