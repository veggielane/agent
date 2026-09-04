using Agent.Core.Events;
using Agent.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Agent.Persistence.Stores;

/// <summary>EF Core implementation of <see cref="ICursorStore"/>: one row per key, upserted.</summary>
public sealed class EfCursorStore : ICursorStore
{
    private readonly IDbContextFactory<AgentDbContext> _factory;
    private readonly TimeProvider _time;

    public EfCursorStore(IDbContextFactory<AgentDbContext> factory, TimeProvider timeProvider)
    {
        _factory = factory;
        _time = timeProvider;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.ChannelCursors.AsNoTracking().FirstOrDefaultAsync(c => c.Key == key, cancellationToken).ConfigureAwait(false);
        return entity?.Value;
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        var now = TaskMapper.ToUtc(_time.GetUtcNow());
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.ChannelCursors.FirstOrDefaultAsync(c => c.Key == key, cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            db.ChannelCursors.Add(new ChannelCursorEntity { Key = key, Value = value, UpdatedAt = now });
        }
        else
        {
            existing.Value = value;
            existing.UpdatedAt = now;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException) when (existing is null)
        {
            // Inserted concurrently by another writer: overwrite in place instead.
            await db.ChannelCursors
                .Where(c => c.Key == key)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Value, value).SetProperty(c => c.UpdatedAt, now), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
