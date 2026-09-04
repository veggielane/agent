using Agent.Core.Channels;
using Agent.Core.Conversations;
using Agent.Core.Events;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Channels.Jira;

/// <summary>
/// History for an issue: the summary and description as the first user message (author = reporter), then the
/// comments in order. The bot's own comments become assistant messages. The triggering comment is excluded.
/// </summary>
public sealed class JiraConversationContextProvider : IConversationContextProvider
{
    private static readonly string[] Fields = ["summary", "description", "comment", "reporter"];

    private readonly IJiraClient _client;
    private readonly IOptionsMonitor<JiraOptions> _options;
    private readonly ILogger<JiraConversationContextProvider> _logger;

    public JiraConversationContextProvider(IJiraClient client, IOptionsMonitor<JiraOptions> options, ILogger<JiraConversationContextProvider> logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    public Channel Channel => Channel.Jira;

    public async Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(InboundEvent evt, int maxMessages, CancellationToken cancellationToken)
    {
        var key = evt.Meta("issue") ?? evt.ConversationId;
        if (string.IsNullOrWhiteSpace(key) || maxMessages <= 0)
        {
            return [];
        }

        JiraIssue? issue;
        try
        {
            issue = await _client.GetIssueAsync(key, Fields, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JiraApiException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Could not load history for {Issue}", key);
            return [];
        }

        if (issue is null)
        {
            return [];
        }

        var options = _options.CurrentValue;
        var mapper = new JiraEventMapper(options);
        var limit = options.HistoryComments > 0 ? Math.Min(maxMessages, options.HistoryComments) : maxMessages;
        var skipId = evt.Meta("comment_id");

        var comments = (issue.Fields.Comment?.Comments ?? [])
            .Where(c => skipId is null || !string.Equals(c.Id, skipId, StringComparison.Ordinal))
            .OrderBy(c => c.Created ?? DateTimeOffset.MinValue)
            .Select(c => ToMessage(c, mapper))
            .ToList();

        var history = new List<ChatMessage>(limit) { DescriptionMessage(issue) };
        var take = Math.Max(0, limit - 1);
        history.AddRange(comments.Skip(Math.Max(0, comments.Count - take)));
        return history;
    }

    private static ChatMessage DescriptionMessage(JiraIssue issue)
    {
        var fields = issue.Fields;
        var text = $"Summary: {fields.Summary?.Trim()}";
        if (!string.IsNullOrWhiteSpace(fields.Description))
        {
            text += "\n\n" + fields.Description.Trim();
        }

        return new ChatMessage(ChatRole.User, text) { AuthorName = AuthorName(fields.Reporter) ?? "reporter" };
    }

    private static ChatMessage ToMessage(JiraComment comment, JiraEventMapper mapper)
    {
        var body = comment.Body?.Trim() ?? string.Empty;
        return mapper.IsBot(comment.Author)
            ? new ChatMessage(ChatRole.Assistant, body)
            : new ChatMessage(ChatRole.User, body) { AuthorName = AuthorName(comment.Author) ?? "user" };
    }

    private static string? AuthorName(JiraUser? user)
        => user is null ? null : user.Login ?? user.DisplayName;
}
