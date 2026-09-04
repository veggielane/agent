using Agent.Core.Tasks;
using Agent.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

// ToUpper() without a culture is deliberate: it is translated to UPPER() by both providers, which is the
// portable way to get the case-insensitive comparisons InMemoryTaskStore performs with OrdinalIgnoreCase.
#pragma warning disable CA1304, CA1311

namespace Agent.Persistence.Stores;

/// <summary>EF Core implementation of <see cref="ITaskStore"/>; one short-lived context per call.</summary>
public sealed class EfTaskStore : ITaskStore
{
    private const int ClaimAttempts = 3;

    private static readonly AgentTaskStatus[] FinishedStatuses =
    [
        AgentTaskStatus.Done,
        AgentTaskStatus.Closed,
        AgentTaskStatus.Failed,
        AgentTaskStatus.Cancelled,
    ];

    private static readonly AgentTaskStatus[] RunningStatuses =
    [
        AgentTaskStatus.Preparing,
        AgentTaskStatus.Working,
        AgentTaskStatus.Verifying,
        AgentTaskStatus.Publishing,
    ];

    private readonly IDbContextFactory<AgentDbContext> _factory;
    private readonly ILogger<EfTaskStore> _logger;
    private readonly TimeProvider _time;

    public EfTaskStore(IDbContextFactory<AgentDbContext> factory, ILogger<EfTaskStore> logger, TimeProvider timeProvider)
    {
        _factory = factory;
        _logger = logger;
        _time = timeProvider;
    }

    public async Task<AgentTask> AddAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        task.CreatedAt = task.UpdatedAt = now;
        task.Version = 1;

        var entity = TaskMapper.ToEntity(task);
        entity.Id = 0;

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Tasks.Add(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        task.Id = entity.Id;
        return task;
    }

    public async Task<AgentTask?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : TaskMapper.ToModel(entity);
    }

    public async Task UpdateAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.Tasks.FirstOrDefaultAsync(t => t.Id == task.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Task #{task.Id} not found.");

        if (entity.Version != task.Version)
        {
            throw new TaskConcurrencyException(task.Id);
        }

        var now = _time.GetUtcNow();
        TaskMapper.Apply(task, entity);
        entity.UpdatedAt = TaskMapper.ToUtc(now);
        entity.Version = task.Version + 1;

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogDebug(ex, "Concurrent update of task #{TaskId} detected by the database", task.Id);
            throw new TaskConcurrencyException(task.Id);
        }

        task.Version = entity.Version;
        task.UpdatedAt = now;
    }

    public async Task<IReadOnlyList<AgentTask>> ListAsync(TaskQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IQueryable<TaskEntity> q = db.Tasks.AsNoTracking();

        if (query.RequesterId is not null)
        {
            var requester = query.RequesterId.ToUpperInvariant();
            q = q.Where(t => t.RequesterId.ToUpper() == requester);
        }

        if (query.Statuses is { Count: > 0 })
        {
            var statuses = query.Statuses.ToArray();
            q = q.Where(t => statuses.Contains(t.Status));
        }

        if (query.ActiveOnly)
        {
            q = q.Where(t => !FinishedStatuses.Contains(t.Status));
        }

        var entities = await q
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .Take(Math.Max(0, query.Limit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return entities.Select(TaskMapper.ToModel).ToList();
    }

    public async Task<AgentTask?> ClaimNextQueuedAsync(string workerId, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var entity = await db.Tasks
                .Where(t => t.Status == AgentTaskStatus.Queued)
                .OrderBy(t => t.UpdatedAt)
                .ThenBy(t => t.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (entity is null)
            {
                return null;
            }

            entity.Status = AgentTaskStatus.Preparing;
            entity.WorkerId = workerId;
            entity.Attempts++;
            entity.Version++;
            entity.UpdatedAt = TaskMapper.ToUtc(_time.GetUtcNow());

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return TaskMapper.ToModel(entity);
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < ClaimAttempts)
            {
                _logger.LogDebug(ex, "Task #{TaskId} was claimed by another worker; retrying ({Attempt}/{Max})", entity.Id, attempt, ClaimAttempts);
            }
        }
    }

    public async Task<AgentTask?> FindActiveBySourceAsync(TaskSource source, string sourceRef, CancellationToken cancellationToken = default)
    {
        var reference = sourceRef.ToUpperInvariant();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.Tasks.AsNoTracking()
            .Where(t => t.Source == source && t.SourceRef.ToUpper() == reference && !FinishedStatuses.Contains(t.Status))
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return entity is null ? null : TaskMapper.ToModel(entity);
    }

    public async Task<AgentTask?> FindByMergeRequestAsync(string projectId, string mergeRequestIid, CancellationToken cancellationToken = default)
    {
        var project = projectId.ToUpperInvariant();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.Tasks.AsNoTracking()
            .Where(t => t.ProjectId != null && t.ProjectId.ToUpper() == project && t.MergeRequestIid == mergeRequestIid)
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return entity is null ? null : TaskMapper.ToModel(entity);
    }

    public async Task<IReadOnlyList<AgentTask>> ListRunningAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entities = await db.Tasks.AsNoTracking()
            .Where(t => RunningStatuses.Contains(t.Status))
            .OrderBy(t => t.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return entities.Select(TaskMapper.ToModel).ToList();
    }

    public async Task AddEventAsync(TaskEvent evt, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.TaskEvents.Add(TaskMapper.ToEntity(evt));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TaskEvent>> GetEventsAsync(int taskId, int max = 50, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entities = await db.TaskEvents.AsNoTracking()
            .Where(e => e.TaskId == taskId)
            .OrderByDescending(e => e.Id)
            .Take(Math.Max(0, max))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        entities.Reverse();
        return entities.Select(TaskMapper.ToModel).ToList();
    }
}
