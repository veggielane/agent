using Agent.Core.Channels;
using Agent.Core.Events;
using Agent.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Agent.Persistence.Stores;

/// <summary>EF Core implementation of <see cref="IProcessedEventStore"/>; the composite primary key enforces idempotency.</summary>
public sealed class EfProcessedEventStore : IProcessedEventStore
{
    private readonly IDbContextFactory<AgentDbContext> _factory;
    private readonly ILogger<EfProcessedEventStore> _logger;
    private readonly TimeProvider _time;

    public EfProcessedEventStore(IDbContextFactory<AgentDbContext> factory, ILogger<EfProcessedEventStore> logger, TimeProvider timeProvider)
    {
        _factory = factory;
        _logger = logger;
        _time = timeProvider;
    }

    public async Task<bool> TryMarkProcessedAsync(Channel channel, string eventId, CancellationToken cancellationToken = default)
    {
        var channelName = channel.ToString();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var exists = await db.ProcessedEvents
            .AnyAsync(p => p.Channel == channelName && p.EventId == eventId, cancellationToken)
            .ConfigureAwait(false);
        if (exists)
        {
            return false;
        }

        db.ProcessedEvents.Add(new ProcessedEventEntity
        {
            Channel = channelName,
            EventId = eventId,
            ProcessedAt = TaskMapper.ToUtc(_time.GetUtcNow()),
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex)
        {
            // Lost the race with a concurrent writer: the event is processed either way.
            _logger.LogDebug(ex, "Event {Channel}:{EventId} was marked processed concurrently", channelName, eventId);
            return false;
        }
    }
}
