using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Agent.Persistence.Hosting;

public sealed record RetentionResult(int TaskEvents, int Audit, int ProcessedEvents)
{
    public int Total => TaskEvents + Audit + ProcessedEvents;
}

/// <summary>Deletes rows older than a cutoff from the append-only tables, in batches so locks stay short.</summary>
public sealed class RetentionPurger
{
    private readonly IDbContextFactory<AgentDbContext> _factory;
    private readonly ILogger<RetentionPurger> _logger;

    public RetentionPurger(IDbContextFactory<AgentDbContext> factory, ILogger<RetentionPurger> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<RetentionResult> PurgeAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken = default)
    {
        var cutoffUtc = cutoff.UtcDateTime;
        batchSize = Math.Max(1, batchSize);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var events = await DeleteInBatchesAsync(db.TaskEvents.Where(e => e.At < cutoffUtc), batchSize, cancellationToken).ConfigureAwait(false);
        var audit = await DeleteInBatchesAsync(db.Audit.Where(a => a.Timestamp < cutoffUtc), batchSize, cancellationToken).ConfigureAwait(false);
        var processed = await DeleteInBatchesAsync(db.ProcessedEvents.Where(p => p.ProcessedAt < cutoffUtc), batchSize, cancellationToken).ConfigureAwait(false);

        var result = new RetentionResult(events, audit, processed);
        _logger.LogInformation(
            "Retention: purged {Total} rows older than {Cutoff:u} (task events {TaskEvents}, audit {Audit}, processed events {ProcessedEvents})",
            result.Total,
            cutoff,
            events,
            audit,
            processed);
        return result;
    }

    private static async Task<int> DeleteInBatchesAsync<T>(IQueryable<T> query, int batchSize, CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = await query.Take(batchSize).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            total += deleted;
            if (deleted < batchSize)
            {
                return total;
            }
        }
    }
}
