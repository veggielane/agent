using Agent.Core.Authorization;

namespace Agent.Core.Tasks;

/// <summary>Task lifecycle operations used by channels, commands, the API, and the worker.</summary>
public interface ITaskService
{
    Task<AgentTask> CreateAsync(TaskRequest request, CancellationToken cancellationToken = default);

    /// <summary>Queues a follow-up instruction on an existing task (re-queues it if it is waiting for review).</summary>
    Task<AgentTask> AddFollowUpAsync(int taskId, string instruction, CallerIdentity by, CancellationToken cancellationToken = default);

    Task<AgentTask?> CancelAsync(int taskId, CallerIdentity by, CancellationToken cancellationToken = default);

    Task<AgentTask?> RetryAsync(int taskId, CallerIdentity by, CancellationToken cancellationToken = default);

    Task<AgentTask?> GetAsync(int taskId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentTask>> ListAsync(TaskQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskEvent>> GetEventsAsync(int taskId, int max = 50, CancellationToken cancellationToken = default);

    Task RecordEventAsync(int taskId, string type, string message, CancellationToken cancellationToken = default);

    /// <summary>Marks the task as merged/closed after the MR changed state externally.</summary>
    Task<AgentTask?> CloseAsync(int taskId, bool merged, CancellationToken cancellationToken = default);
}

/// <summary>Wakes the worker when a task is queued instead of waiting for the next poll.</summary>
public interface ITaskQueueSignal
{
    void Signal();

    Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class TaskQueueSignal : ITaskQueueSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Signal()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // already signalled
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => _semaphore.WaitAsync(timeout, cancellationToken);
}

/// <summary>Lets <c>!cancel</c> stop a task that is currently executing in the worker.</summary>
public interface ITaskCancellationRegistry
{
    CancellationToken Register(int taskId, CancellationToken linkedTo);

    bool RequestCancel(int taskId);

    void Unregister(int taskId);

    IReadOnlyCollection<int> RunningTaskIds { get; }
}

public sealed class TaskCancellationRegistry : ITaskCancellationRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, CancellationTokenSource> _sources = new();

    public IReadOnlyCollection<int> RunningTaskIds => _sources.Keys.ToArray();

    public CancellationToken Register(int taskId, CancellationToken linkedTo)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(linkedTo);
        _sources[taskId] = cts;
        return cts.Token;
    }

    public bool RequestCancel(int taskId)
    {
        if (_sources.TryGetValue(taskId, out var cts))
        {
            cts.Cancel();
            return true;
        }

        return false;
    }

    public void Unregister(int taskId)
    {
        if (_sources.TryRemove(taskId, out var cts))
        {
            cts.Dispose();
        }
    }
}
