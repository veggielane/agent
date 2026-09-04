using Agent.Core.Channels;
using Agent.Core.Events;
using Agent.Core.Replies;
using Microsoft.Extensions.Logging;

namespace Agent.Channels.Jira;

/// <summary>Replies are comments on the issue. Jira has no reactions, so acknowledgements are a no-op.</summary>
public sealed class JiraReplySender : IReplySender
{
    private readonly IJiraClient _client;
    private readonly ILogger<JiraReplySender> _logger;

    public JiraReplySender(IJiraClient client, ILogger<JiraReplySender> logger)
    {
        _client = client;
        _logger = logger;
    }

    public Channel Channel => Channel.Jira;

    public async Task SendAsync(InboundEvent source, string text, CancellationToken cancellationToken)
    {
        var key = source.Meta("issue") ?? source.ConversationId;
        if (string.IsNullOrWhiteSpace(key))
        {
            _logger.LogWarning("Cannot reply to {EventId}: no issue key", source.EventId);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogDebug("Empty reply for {Issue} skipped", key);
            return;
        }

        await _client.AddCommentAsync(key, text, cancellationToken).ConfigureAwait(false);
    }

    public Task AcknowledgeAsync(InboundEvent source, AckState state, string? note, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
