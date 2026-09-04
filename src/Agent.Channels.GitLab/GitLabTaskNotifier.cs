using System.Globalization;
using Agent.Core.Channels;
using Agent.Core.Tasks;
using Microsoft.Extensions.Logging;

namespace Agent.Channels.GitLab;

/// <summary>Posts task progress as a note on the issue (or merge request) the task came from.</summary>
public sealed class GitLabTaskNotifier : ITaskNotifier
{
    private readonly IGitLabClient _client;
    private readonly ILogger<GitLabTaskNotifier> _logger;

    public GitLabTaskNotifier(IGitLabClient client, ILogger<GitLabTaskNotifier> logger)
    {
        _client = client;
        _logger = logger;
    }

    public Channel Channel => Channel.GitLab;

    public async Task NotifyAsync(AgentTask task, string markdown, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return;
        }

        var target = ResolveTarget(task);
        if (target is null)
        {
            _logger.LogWarning("Task {Task} has no GitLab issue or merge request to notify", task.DisplayRef);
            return;
        }

        if (target.Type == GitLabNoteableType.MergeRequest)
        {
            await _client.CreateMergeRequestNoteAsync(target.Project, target.Iid, markdown, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _client.CreateIssueNoteAsync(target.Project, target.Iid, markdown, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Task metadata (project_id / iid / target_type) first, then SourceRef, then ConversationId.</summary>
    internal static GitLabReplySender.ReplyTarget? ResolveTarget(AgentTask task)
    {
        var project = Get(task, GitLabEventMapper.ProjectIdKey) ?? Get(task, GitLabEventMapper.ProjectPathKey);
        if (!string.IsNullOrWhiteSpace(project)
            && long.TryParse(Get(task, GitLabEventMapper.IidKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var iid)
            && GitLabNoteableTypeExtensions.TryParse(Get(task, GitLabEventMapper.TargetTypeKey), out var type))
        {
            return new GitLabReplySender.ReplyTarget(project, iid, type);
        }

        foreach (var reference in new[] { task.SourceRef, task.ConversationId })
        {
            if (GitLabRefs.TryParse(reference, out var path, out var parsedIid, out var parsedType))
            {
                return new GitLabReplySender.ReplyTarget(path, parsedIid, parsedType);
            }
        }

        return null;
    }

    private static string? Get(AgentTask task, string key)
        => task.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
