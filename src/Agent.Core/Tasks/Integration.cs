using Agent.Core.Channels;
using Agent.Core.Events;
using Microsoft.Extensions.Logging;

namespace Agent.Core.Tasks;

/// <summary>Posts task progress back to the ticket / thread the task came from. One per channel.</summary>
public interface ITaskNotifier
{
    Channel Channel { get; }

    Task NotifyAsync(AgentTask task, string markdown, CancellationToken cancellationToken);
}

/// <summary>Routes a notification to the notifier for the task's channel.</summary>
public interface ITaskNotifierRouter
{
    Task NotifyAsync(AgentTask task, string markdown, CancellationToken cancellationToken);
}

public sealed class TaskNotifierRouter : ITaskNotifierRouter
{
    private readonly IEnumerable<ITaskNotifier> _notifiers;
    private readonly ILogger<TaskNotifierRouter> _logger;

    public TaskNotifierRouter(IEnumerable<ITaskNotifier> notifiers, ILogger<TaskNotifierRouter> logger)
    {
        _notifiers = notifiers;
        _logger = logger;
    }

    public async Task NotifyAsync(AgentTask task, string markdown, CancellationToken cancellationToken)
    {
        var notifier = _notifiers.FirstOrDefault(n => n.Channel == task.NotifyChannel);
        if (notifier is null)
        {
            _logger.LogInformation("No notifier for {Channel}; task {Task}: {Message}", task.NotifyChannel, task.DisplayRef, markdown);
            return;
        }

        try
        {
            await notifier.NotifyAsync(task, markdown, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to notify {Channel} for task {Task}", task.NotifyChannel, task.DisplayRef);
        }
    }
}

public sealed record MergeRequestSpec(
    string ProjectId,
    string SourceBranch,
    string TargetBranch,
    string Title,
    string Description,
    bool Draft,
    string? ReviewerUsername,
    IReadOnlyList<string>? Labels = null);

public sealed record MergeRequestInfo(string Iid, string Url, string State, string SourceBranch);

/// <summary>Opens and updates merge requests. Implemented by the GitLab channel.</summary>
public interface IMergeRequestPublisher
{
    /// <summary>Creates the MR for the branch, or updates title/description if one is already open.</summary>
    Task<MergeRequestInfo> EnsureMergeRequestAsync(MergeRequestSpec spec, CancellationToken cancellationToken);

    Task<MergeRequestInfo?> GetMergeRequestAsync(string projectId, string iid, CancellationToken cancellationToken);

    Task CommentAsync(string projectId, string iid, string markdown, CancellationToken cancellationToken);
}

/// <summary>Supplies HTTPS credentials for clone/push. Implemented by the GitLab channel; never exposed to the coding loop.</summary>
public interface IRepositoryCredentialProvider
{
    /// <summary>Returns (username, token) for the repo, or null when no credentials apply.</summary>
    (string Username, string Token)? GetCredentials(string repoUrl);
}

public sealed record RepoTarget(string Url, string? ProjectId, string? DefaultBranch);

/// <summary>Finds the repository a ticket refers to. Used for Jira and chat-originated tasks.</summary>
public interface IRepositoryResolver
{
    Task<RepoTarget?> ResolveAsync(InboundEvent evt, CancellationToken cancellationToken);
}
