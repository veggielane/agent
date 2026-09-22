using System.Diagnostics;
using Agent.Core.Audit;
using Agent.Core.Authorization;
using Agent.Core.Observability;
using Microsoft.Extensions.Logging;

namespace Agent.Core.Tasks;

public sealed class TaskService : ITaskService
{
    private readonly ITaskStore _store;
    private readonly ITaskQueueSignal _signal;
    private readonly ITaskCancellationRegistry _cancellations;
    private readonly IRepositoryPolicy _repositories;
    private readonly IAuditSink _audit;
    private readonly ILogger<TaskService> _logger;

    public TaskService(
        ITaskStore store,
        ITaskQueueSignal signal,
        ITaskCancellationRegistry cancellations,
        IRepositoryPolicy repositories,
        IAuditSink audit,
        ILogger<TaskService> logger)
    {
        _store = store;
        _signal = signal;
        _cancellations = cancellations;
        _repositories = repositories;
        _audit = audit;
        _logger = logger;
    }

    /// <exception cref="RepositoryNotAllowedException">The repository policy refuses <see cref="TaskRequest.RepoUrl"/>.</exception>
    public async Task<AgentTask> CreateAsync(TaskRequest request, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(request.RepoUrl))
        {
            var decision = _repositories.Check(request.RepoUrl);
            if (!decision.Allowed)
            {
                var reason = decision.Reason ?? "The repository is not allowed.";
                await _audit.WriteAsync(
                    new AuditEntry(DateTimeOffset.UtcNow, request.Requester.Channel, request.Requester.ChannelUserId, request.Requester.DisplayName, "task.create", "denied", $"{request.SourceRef}: {request.RepoUrl}", request.Requester.Roles),
                    cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("Task for {SourceRef} by {Requester} refused: repository {Repo} is not allowed", request.SourceRef, request.Requester.DisplayName, request.RepoUrl);
                throw new RepositoryNotAllowedException(request.RepoUrl, reason);
            }
        }

        var task = new AgentTask
        {
            Source = request.Source,
            SourceRef = request.SourceRef,
            SourceUrl = request.SourceUrl,
            Title = request.Title,
            RequesterId = request.Requester.Key,
            RequesterName = request.Requester.DisplayName,
            RequesterUsername = request.Requester.Username,
            NotifyChannel = request.NotifyChannel,
            ConversationId = request.ConversationId,
            RepoUrl = request.RepoUrl,
            ProjectId = request.ProjectId,
            BaseBranch = request.BaseBranch,
            WorkBranch = request.ExistingBranch,
            MergeRequestUrl = request.MergeRequestUrl,
            MergeRequestIid = request.MergeRequestIid,
            Instruction = request.Instruction,
            Status = string.IsNullOrWhiteSpace(request.RepoUrl) && string.IsNullOrWhiteSpace(request.ProjectId)
                ? AgentTaskStatus.NeedsInput
                : AgentTaskStatus.Queued,
        };

        if (request.Metadata is not null)
        {
            foreach (var (k, v) in request.Metadata)
            {
                task.Metadata[k] = v;
            }
        }

        // The worker rebuilds the requester's identity from this when it needs role-filtered tools.
        task.Metadata["RequesterRoles"] = string.Join(",", request.Requester.Roles.OrderBy(r => r));

        task = await _store.AddAsync(task, cancellationToken).ConfigureAwait(false);
        await _store.AddEventAsync(new TaskEvent(task.Id, DateTimeOffset.UtcNow, "created", $"Created by {task.RequesterName} from {task.Source} {task.SourceRef}"), cancellationToken).ConfigureAwait(false);

        if (task.Status == AgentTaskStatus.NeedsInput)
        {
            await _store.AddEventAsync(new TaskEvent(task.Id, DateTimeOffset.UtcNow, "needs-input", "No repository could be resolved for this task."), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _signal.Signal();
        }

        AgentTelemetry.Tasks.Add(1, new TagList
        {
            { "transition", "created" },
            { "source", task.Source.ToString() },
            { "status", task.Status.ToString() },
        });

        _logger.LogInformation("Task {Task} created ({Status})", task.DisplayRef, task.Status);
        return task;
    }

    public async Task<AgentTask> AddFollowUpAsync(int taskId, string instruction, CallerIdentity by, CancellationToken cancellationToken = default)
    {
        var task = await _store.GetAsync(taskId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Task #{taskId} not found.");

        task.PendingInstruction = string.IsNullOrWhiteSpace(task.PendingInstruction)
            ? instruction
            : task.PendingInstruction + "\n\n" + instruction;

        if (task.Status is AgentTaskStatus.AwaitingReview or AgentTaskStatus.NeedsInput or AgentTaskStatus.Failed or AgentTaskStatus.Interrupted or AgentTaskStatus.Done or AgentTaskStatus.Closed)
        {
            task.Status = AgentTaskStatus.Queued;
        }

        await _store.UpdateAsync(task, cancellationToken).ConfigureAwait(false);
        await _store.AddEventAsync(new TaskEvent(task.Id, DateTimeOffset.UtcNow, "follow-up", $"{by.DisplayName}: {Truncate(instruction, 300)}"), cancellationToken).ConfigureAwait(false);

        if (task.Status == AgentTaskStatus.Queued)
        {
            _signal.Signal();
        }

        return task;
    }

    public async Task<AgentTask?> CancelAsync(int taskId, CallerIdentity by, CancellationToken cancellationToken = default)
    {
        var task = await _store.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task is null || !task.IsActive)
        {
            return task;
        }

        var wasRunning = _cancellations.RequestCancel(taskId);
        task.Status = AgentTaskStatus.Cancelled;
        await _store.UpdateAsync(task, cancellationToken).ConfigureAwait(false);
        await _store.AddEventAsync(new TaskEvent(task.Id, DateTimeOffset.UtcNow, "cancelled", $"Cancelled by {by.DisplayName}{(wasRunning ? " (was running)" : string.Empty)}"), cancellationToken).ConfigureAwait(false);
        return task;
    }

    public async Task<AgentTask?> RetryAsync(int taskId, CallerIdentity by, CancellationToken cancellationToken = default)
    {
        var task = await _store.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            return null;
        }

        if (task.IsRunning)
        {
            return task;
        }

        task.Status = AgentTaskStatus.Queued;
        task.Error = null;
        await _store.UpdateAsync(task, cancellationToken).ConfigureAwait(false);
        await _store.AddEventAsync(new TaskEvent(task.Id, DateTimeOffset.UtcNow, "retry", $"Re-queued by {by.DisplayName}"), cancellationToken).ConfigureAwait(false);
        _signal.Signal();
        return task;
    }

    public Task<AgentTask?> GetAsync(int taskId, CancellationToken cancellationToken = default) => _store.GetAsync(taskId, cancellationToken);

    public Task<IReadOnlyList<AgentTask>> ListAsync(TaskQuery query, CancellationToken cancellationToken = default) => _store.ListAsync(query, cancellationToken);

    public Task<IReadOnlyList<TaskEvent>> GetEventsAsync(int taskId, int max = 50, CancellationToken cancellationToken = default) => _store.GetEventsAsync(taskId, max, cancellationToken);

    public Task RecordEventAsync(int taskId, string type, string message, CancellationToken cancellationToken = default)
        => _store.AddEventAsync(new TaskEvent(taskId, DateTimeOffset.UtcNow, type, message), cancellationToken);

    public async Task<AgentTask?> CloseAsync(int taskId, bool merged, CancellationToken cancellationToken = default)
    {
        var task = await _store.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task is null || !task.IsActive)
        {
            return task;
        }

        task.Status = merged ? AgentTaskStatus.Done : AgentTaskStatus.Closed;
        await _store.UpdateAsync(task, cancellationToken).ConfigureAwait(false);
        await _store.AddEventAsync(new TaskEvent(task.Id, DateTimeOffset.UtcNow, merged ? "merged" : "closed", merged ? "Merge request merged" : "Merge request closed"), cancellationToken).ConfigureAwait(false);

        AgentTelemetry.Tasks.Add(1, new TagList
        {
            { "transition", merged ? "merged" : "closed" },
            { "source", task.Source.ToString() },
            { "status", task.Status.ToString() },
        });
        AgentTelemetry.TaskDuration.Record(
            (DateTimeOffset.UtcNow - task.CreatedAt).TotalSeconds,
            new TagList { { "outcome", task.Status.ToString() }, { "source", task.Source.ToString() } });

        return task;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
