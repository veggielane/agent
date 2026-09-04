using Agent.Core.Audit;
using Agent.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Agent.Persistence.Stores;

/// <summary>Writes every audit entry to the <c>Audit</c> table and to the log. Persistence failures are logged, never thrown.</summary>
public sealed class EfAuditSink : IAuditSink
{
    private readonly IDbContextFactory<AgentDbContext> _factory;
    private readonly ILogger<EfAuditSink> _logger;

    public EfAuditSink(IDbContextFactory<AgentDbContext> factory, ILogger<EfAuditSink> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async ValueTask WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        var roles = entry.Roles is null ? null : string.Join(",", entry.Roles.OrderBy(r => r));

        _logger.LogInformation(
            "audit {Outcome} {Action} channel={Channel} caller={CallerId} ({CallerName}) roles=[{Roles}] {Detail}",
            entry.Outcome,
            entry.Action,
            entry.Channel,
            entry.CallerId,
            entry.CallerName,
            roles ?? string.Empty,
            entry.Detail);

        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.Audit.Add(new AuditEntity
            {
                Timestamp = TaskMapper.ToUtc(entry.Timestamp),
                Channel = entry.Channel.ToString(),
                CallerId = entry.CallerId,
                CallerName = entry.CallerName,
                Action = entry.Action,
                Outcome = entry.Outcome,
                Detail = entry.Detail,
                Roles = roles,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist audit entry {Action} for {CallerId}", entry.Action, entry.CallerId);
        }
    }
}
