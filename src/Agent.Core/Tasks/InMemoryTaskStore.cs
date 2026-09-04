namespace Agent.Core.Tasks;

public sealed class InMemoryTaskStore : ITaskStore
{
    private readonly Dictionary<int, AgentTask> _tasks = [];
    private readonly List<TaskEvent> _events = [];
    private readonly Lock _lock = new();
    private int _nextId;
    private long _nextEventId;

    public Task<AgentTask> AddAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            task.Id = ++_nextId;
            task.Version = 1;
            task.CreatedAt = task.UpdatedAt = DateTimeOffset.UtcNow;
            _tasks[task.Id] = Clone(task);
            return Task.FromResult(task);
        }
    }

    public Task<AgentTask?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_tasks.TryGetValue(id, out var t) ? Clone(t) : null);
        }
    }

    public Task UpdateAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_tasks.TryGetValue(task.Id, out var current))
            {
                throw new KeyNotFoundException($"Task #{task.Id} not found.");
            }

            if (current.Version != task.Version)
            {
                throw new TaskConcurrencyException(task.Id);
            }

            task.Version++;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            _tasks[task.Id] = Clone(task);
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<AgentTask>> ListAsync(TaskQuery query, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IEnumerable<AgentTask> q = _tasks.Values;
            if (query.RequesterId is not null)
            {
                q = q.Where(t => string.Equals(t.RequesterId, query.RequesterId, StringComparison.OrdinalIgnoreCase));
            }

            if (query.Statuses is { Count: > 0 })
            {
                q = q.Where(t => query.Statuses.Contains(t.Status));
            }

            if (query.ActiveOnly)
            {
                q = q.Where(t => t.IsActive);
            }

            IReadOnlyList<AgentTask> result = q.OrderByDescending(t => t.CreatedAt).Take(query.Limit).Select(Clone).ToList();
            return Task.FromResult(result);
        }
    }

    public Task<AgentTask?> ClaimNextQueuedAsync(string workerId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var next = _tasks.Values.Where(t => t.Status == AgentTaskStatus.Queued).OrderBy(t => t.UpdatedAt).ThenBy(t => t.Id).FirstOrDefault();
            if (next is null)
            {
                return Task.FromResult<AgentTask?>(null);
            }

            next.Status = AgentTaskStatus.Preparing;
            next.WorkerId = workerId;
            next.Attempts++;
            next.Version++;
            next.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult<AgentTask?>(Clone(next));
        }
    }

    public Task<AgentTask?> FindActiveBySourceAsync(TaskSource source, string sourceRef, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var t = _tasks.Values
                .Where(t => t.Source == source && string.Equals(t.SourceRef, sourceRef, StringComparison.OrdinalIgnoreCase) && t.IsActive)
                .OrderByDescending(t => t.CreatedAt)
                .FirstOrDefault();
            return Task.FromResult(t is null ? null : Clone(t));
        }
    }

    public Task<AgentTask?> FindByMergeRequestAsync(string projectId, string mergeRequestIid, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var t = _tasks.Values
                .Where(t => string.Equals(t.ProjectId, projectId, StringComparison.OrdinalIgnoreCase) && string.Equals(t.MergeRequestIid, mergeRequestIid, StringComparison.Ordinal))
                .OrderByDescending(t => t.CreatedAt)
                .FirstOrDefault();
            return Task.FromResult(t is null ? null : Clone(t));
        }
    }

    public Task<IReadOnlyList<AgentTask>> ListRunningAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<AgentTask> result = _tasks.Values.Where(t => t.IsRunning).Select(Clone).ToList();
            return Task.FromResult(result);
        }
    }

    public Task AddEventAsync(TaskEvent evt, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _events.Add(evt with { Id = ++_nextEventId });
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<TaskEvent>> GetEventsAsync(int taskId, int max = 50, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<TaskEvent> result = _events.Where(e => e.TaskId == taskId).OrderBy(e => e.Id).TakeLast(max).ToList();
            return Task.FromResult(result);
        }
    }

    private static AgentTask Clone(AgentTask t) => new()
    {
        Id = t.Id,
        Source = t.Source,
        SourceRef = t.SourceRef,
        SourceUrl = t.SourceUrl,
        Title = t.Title,
        RequesterId = t.RequesterId,
        RequesterName = t.RequesterName,
        RequesterUsername = t.RequesterUsername,
        NotifyChannel = t.NotifyChannel,
        ConversationId = t.ConversationId,
        RepoUrl = t.RepoUrl,
        ProjectId = t.ProjectId,
        BaseBranch = t.BaseBranch,
        WorkBranch = t.WorkBranch,
        MergeRequestUrl = t.MergeRequestUrl,
        MergeRequestIid = t.MergeRequestIid,
        Status = t.Status,
        Instruction = t.Instruction,
        PendingInstruction = t.PendingInstruction,
        Summary = t.Summary,
        Error = t.Error,
        Turns = t.Turns,
        TokensUsed = t.TokensUsed,
        Attempts = t.Attempts,
        WorkerId = t.WorkerId,
        CreatedAt = t.CreatedAt,
        UpdatedAt = t.UpdatedAt,
        Metadata = new Dictionary<string, string>(t.Metadata, StringComparer.OrdinalIgnoreCase),
        Version = t.Version,
    };
}
