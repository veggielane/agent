namespace Agent.Core.Tasks;

/// <summary>Durable task state. Implemented in memory (tests, CLI local mode) and with EF Core.</summary>
public interface ITaskStore
{
    Task<AgentTask> AddAsync(AgentTask task, CancellationToken cancellationToken = default);

    Task<AgentTask?> GetAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Saves the task, bumping <see cref="AgentTask.Version"/>. Throws <see cref="TaskConcurrencyException"/> on a stale version.</summary>
    Task UpdateAsync(AgentTask task, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentTask>> ListAsync(TaskQuery query, CancellationToken cancellationToken = default);

    /// <summary>Atomically moves the oldest <see cref="AgentTaskStatus.Queued"/> task to <see cref="AgentTaskStatus.Preparing"/> for this worker.</summary>
    Task<AgentTask?> ClaimNextQueuedAsync(string workerId, CancellationToken cancellationToken = default);

    Task<AgentTask?> FindActiveBySourceAsync(TaskSource source, string sourceRef, CancellationToken cancellationToken = default);

    /// <summary>Finds the task owning a merge request, active or not.</summary>
    Task<AgentTask?> FindByMergeRequestAsync(string projectId, string mergeRequestIid, CancellationToken cancellationToken = default);

    /// <summary>Tasks stuck in a running state (worker died). Called on worker start.</summary>
    Task<IReadOnlyList<AgentTask>> ListRunningAsync(CancellationToken cancellationToken = default);

    Task AddEventAsync(TaskEvent evt, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskEvent>> GetEventsAsync(int taskId, int max = 50, CancellationToken cancellationToken = default);
}
