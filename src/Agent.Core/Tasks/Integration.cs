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

/// <summary>
/// A task message that must reach a thread at most once. <see cref="Key"/> is unique within the task
/// ("plan", "published:2"): the router records it before posting, so a retried attempt, a restarted worker
/// or a repeated poll cannot post the same message twice. <see cref="Terminal"/> messages (merge request
/// opened, failed, needs input, CI fix given up) are also copied to every <see cref="ITaskNotificationMirror"/>;
/// progress stays on the originating thread.
/// </summary>
public sealed record TaskNotification(string Key, string Markdown, bool Terminal = false);

/// <summary>
/// Receives a copy of terminal task notifications, typically an operations channel in chat. Channels register
/// one when they have somewhere to post; a mirror that is not configured reports <see cref="Enabled"/> false and
/// is skipped without consuming the idempotency key.
/// </summary>
public interface ITaskNotificationMirror
{
    Channel Channel { get; }

    bool Enabled { get; }

    Task MirrorAsync(AgentTask task, TaskNotification notification, CancellationToken cancellationToken);
}

/// <summary>Routes a notification to the notifier for the task's channel.</summary>
public interface ITaskNotifierRouter
{
    /// <summary>Posts progress to the originating thread. Not deduplicated; use for messages that may repeat.</summary>
    Task NotifyAsync(AgentTask task, string markdown, CancellationToken cancellationToken);

    /// <summary>Posts once per key to the originating thread, and copies terminal notifications to the mirrors.</summary>
    Task NotifyAsync(AgentTask task, TaskNotification notification, CancellationToken cancellationToken);

    /// <summary>Copies a notification to the mirrors only, for events a channel has already posted on its own.</summary>
    Task MirrorAsync(AgentTask task, TaskNotification notification, CancellationToken cancellationToken);
}

public sealed class TaskNotifierRouter : ITaskNotifierRouter
{
    private readonly IEnumerable<ITaskNotifier> _notifiers;
    private readonly IProcessedEventStore _processed;
    private readonly IEnumerable<ITaskNotificationMirror> _mirrors;
    private readonly ILogger<TaskNotifierRouter> _logger;

    public TaskNotifierRouter(
        IEnumerable<ITaskNotifier> notifiers,
        IProcessedEventStore processed,
        IEnumerable<ITaskNotificationMirror> mirrors,
        ILogger<TaskNotifierRouter> logger)
    {
        _notifiers = notifiers;
        _processed = processed;
        _mirrors = mirrors;
        _logger = logger;
    }

    /// <summary>The idempotency key stored for a notification posted to the originating thread.</summary>
    public static string NotifyKey(AgentTask task, TaskNotification notification) => $"task-notify:{task.Id}:{notification.Key}";

    /// <summary>The idempotency key stored for a notification copied to a mirror.</summary>
    public static string MirrorKey(AgentTask task, TaskNotification notification) => $"task-mirror:{task.Id}:{notification.Key}";

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

    public async Task NotifyAsync(AgentTask task, TaskNotification notification, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Key);

        // Claim the key before posting: a duplicate is worse than a lost message, because the task event log
        // still has what was said and a failed post is logged as an error either way.
        if (await _processed.TryMarkProcessedAsync(task.NotifyChannel, NotifyKey(task, notification), cancellationToken).ConfigureAwait(false))
        {
            await NotifyAsync(task, notification.Markdown, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _logger.LogDebug("Task {Task}: notification {Key} was already posted to {Channel}", task.DisplayRef, notification.Key, task.NotifyChannel);
        }

        if (notification.Terminal)
        {
            await MirrorAsync(task, notification, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task MirrorAsync(AgentTask task, TaskNotification notification, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Key);

        foreach (var mirror in _mirrors)
        {
            if (!mirror.Enabled)
            {
                continue;
            }

            try
            {
                if (!await _processed.TryMarkProcessedAsync(mirror.Channel, MirrorKey(task, notification), cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                await mirror.MirrorAsync(task, notification, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to mirror task {Task} notification {Key} to {Channel}", task.DisplayRef, notification.Key, mirror.Channel);
            }
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
