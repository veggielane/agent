using Agent.Core.Channels;
using Agent.Core.Formatting;
using Agent.Core.Tasks;
using Microsoft.Extensions.Logging;

namespace Agent.Channels.Jira;

/// <summary>Posts task progress as a comment on the originating issue.</summary>
public sealed class JiraTaskNotifier : ITaskNotifier
{
    private readonly IJiraClient _client;
    private readonly IFormatterRegistry _formatters;
    private readonly ILogger<JiraTaskNotifier> _logger;

    public JiraTaskNotifier(IJiraClient client, IFormatterRegistry formatters, ILogger<JiraTaskNotifier> logger)
    {
        _client = client;
        _formatters = formatters;
        _logger = logger;
    }

    public Channel Channel => Channel.Jira;

    public async Task NotifyAsync(AgentTask task, string markdown, CancellationToken cancellationToken)
    {
        var key = task.Source == TaskSource.JiraIssue && !string.IsNullOrWhiteSpace(task.SourceRef)
            ? task.SourceRef
            : task.ConversationId;

        if (string.IsNullOrWhiteSpace(key))
        {
            _logger.LogWarning("Task {Task} has no Jira issue to notify", task.DisplayRef);
            return;
        }

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return;
        }

        await _client.AddCommentAsync(key, _formatters.Format(Channel.Jira, markdown), cancellationToken).ConfigureAwait(false);
    }
}
