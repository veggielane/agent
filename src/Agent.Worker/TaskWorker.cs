using System.Collections.Concurrent;
using Agent.Coding;
using Agent.Core.Infrastructure;
using Agent.Core.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Worker;

/// <summary>
/// Claims queued tasks up to <see cref="CodingOptions.MaxConcurrentTasks"/> and runs each through
/// <see cref="ITaskRunner"/>. Tasks left running by a previous process are re-queued on start.
/// </summary>
public sealed class TaskWorker : BackgroundService, IStatusContributor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(20);

    private readonly ITaskStore _store;
    private readonly ITaskQueueSignal _signal;
    private readonly ITaskRunner _runner;
    private readonly IWorkspaceManager _workspaces;
    private readonly IOptionsMonitor<CodingOptions> _options;
    private readonly ILogger<TaskWorker> _logger;
    private readonly ConcurrentDictionary<int, Task> _running = new();
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";
    private int _completed;
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    public TaskWorker(
        ITaskStore store,
        ITaskQueueSignal signal,
        ITaskRunner runner,
        IWorkspaceManager workspaces,
        IOptionsMonitor<CodingOptions> options,
        ILogger<TaskWorker> logger)
    {
        _store = store;
        _signal = signal;
        _runner = runner;
        _workspaces = workspaces;
        _options = options;
        _logger = logger;
    }

    public string Name => "Worker";

    public string WorkerId => _workerId;

    public IReadOnlyCollection<int> RunningTaskIds => _running.Keys.ToArray();

    public int Completed => Volatile.Read(ref _completed);

    public Task<string> GetStatusAsync(CancellationToken cancellationToken)
    {
        var ids = RunningTaskIds.Order().ToArray();
        var running = ids.Length == 0 ? "none" : string.Join(", ", ids.Select(i => "#" + i));
        return Task.FromResult($"worker {_workerId}: running {ids.Length}/{_options.CurrentValue.MaxConcurrentTasks} ({running}), completed {Completed} since start");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ClaimAsync(stoppingToken).ConfigureAwait(false);
                await PurgeIfDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker loop iteration failed");
            }

            try
            {
                await _signal.WaitAsync(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var pending = _running.Values.ToArray();
        if (pending.Length > 0)
        {
            _logger.LogInformation("Waiting up to {Grace} for {Count} running task(s) to wind down", ShutdownGrace, pending.Length);
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(ShutdownGrace, CancellationToken.None)).ConfigureAwait(false);
        }
    }

    private async Task RecoverInterruptedAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentTask> stuck;
        try
        {
            stuck = await _store.ListRunningAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not list running tasks on start");
            return;
        }

        foreach (var task in stuck)
        {
            try
            {
                _logger.LogWarning("Task {Task} was {Status} when the worker stopped; re-queuing", task.DisplayRef, task.Status);
                task.Status = AgentTaskStatus.Interrupted;
                await _store.UpdateAsync(task, cancellationToken).ConfigureAwait(false);
                await _store.AddEventAsync(new TaskEvent(task.Id, DateTimeOffset.UtcNow, "interrupted", "Worker restarted while the task was running; re-queued"), cancellationToken).ConfigureAwait(false);
                task.Status = AgentTaskStatus.Queued;
                await _store.UpdateAsync(task, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not re-queue task {Task}", task.DisplayRef);
            }
        }

        if (stuck.Count > 0)
        {
            _signal.Signal();
        }
    }

    private async Task ClaimAsync(CancellationToken stoppingToken)
    {
        while (_running.Count < Math.Max(1, _options.CurrentValue.MaxConcurrentTasks))
        {
            stoppingToken.ThrowIfCancellationRequested();
            var task = await _store.ClaimNextQueuedAsync(_workerId, stoppingToken).ConfigureAwait(false);
            if (task is null)
            {
                return;
            }

            _logger.LogInformation("Claimed task {Task}", task.DisplayRef);

            // Register before starting so a very fast runner cannot finish before it is tracked.
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _running[task.Id] = completion.Task;
            _ = RunTrackedAsync(task, completion, stoppingToken);
        }
    }

    private async Task RunTrackedAsync(AgentTask task, TaskCompletionSource completion, CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await _runner.RunAsync(task, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Task runner threw for task {Task}", task.DisplayRef);
        }
        finally
        {
            _running.TryRemove(task.Id, out _);
            Interlocked.Increment(ref _completed);
            completion.TrySetResult();
            _signal.Signal();
        }
    }

    private async Task PurgeIfDueAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _lastPurge < PurgeInterval)
        {
            return;
        }

        _lastPurge = DateTimeOffset.UtcNow;
        try
        {
            await _workspaces.PurgeOldAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Workspace purge failed");
        }
    }
}
