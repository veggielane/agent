using System.Globalization;
using Agent.Core.Channels;
using Agent.Core.Conversations;
using Agent.Core.Events;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Channels.GitLab;

/// <summary>
/// History for an issue or merge request: the description as the first user message, then the human notes
/// (system notes and the triggering note excluded); the bot's own notes become assistant messages.
/// </summary>
public sealed class GitLabConversationContextProvider : IConversationContextProvider
{
    private readonly IGitLabClient _client;
    private readonly IOptionsMonitor<GitLabOptions> _options;
    private readonly ILogger<GitLabConversationContextProvider> _logger;
    private string? _resolvedBotUsername;

    public GitLabConversationContextProvider(IGitLabClient client, IOptionsMonitor<GitLabOptions> options, ILogger<GitLabConversationContextProvider> logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    public Channel Channel => Channel.GitLab;

    public async Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(InboundEvent evt, int maxMessages, CancellationToken cancellationToken)
    {
        if (maxMessages <= 0)
        {
            return [];
        }

        var target = GitLabReplySender.ResolveTarget(evt);
        if (target is null)
        {
            return [];
        }

        var options = _options.CurrentValue;
        var limit = Math.Min(maxMessages, Math.Max(1, options.HistoryNotes));
        var botUsername = await GetBotUsernameAsync(options, cancellationToken).ConfigureAwait(false);
        long.TryParse(evt.Meta(GitLabEventMapper.NoteIdKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var triggeringNoteId);

        ChatMessage? opening;
        IReadOnlyList<GitLabNote> notes;
        if (target.Type == GitLabNoteableType.MergeRequest)
        {
            var mr = await _client.GetMergeRequestAsync(target.Project, target.Iid, cancellationToken).ConfigureAwait(false);
            opening = mr is null ? null : Opening(mr.Title, mr.Description, mr.Author, botUsername);
            notes = await _client.GetMergeRequestNotesAsync(target.Project, target.Iid, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var issue = await _client.GetIssueAsync(target.Project, target.Iid, cancellationToken).ConfigureAwait(false);
            opening = issue is null ? null : Opening(issue.Title, issue.Description, issue.Author, botUsername);
            notes = await _client.GetIssueNotesAsync(target.Project, target.Iid, cancellationToken).ConfigureAwait(false);
        }

        var messages = notes
            .Where(n => !n.System && !string.IsNullOrWhiteSpace(n.Body) && (triggeringNoteId == 0 || n.Id != triggeringNoteId))
            .OrderBy(n => n.CreatedAt)
            .Select(n => ToMessage(n.Body, n.Author, botUsername))
            .ToList();

        var result = new List<ChatMessage>(limit);
        if (opening is not null)
        {
            result.Add(opening);
        }

        var room = limit - result.Count;
        if (room > 0)
        {
            result.AddRange(messages.Count <= room ? messages : messages.Skip(messages.Count - room));
        }

        return result;
    }

    private static ChatMessage Opening(string title, string? description, GitLabUserRef? author, string? botUsername)
    {
        var text = string.IsNullOrWhiteSpace(description) ? title.Trim() : $"{title.Trim()}\n\n{description.Trim()}";
        return ToMessage(text, author, botUsername);
    }

    private static ChatMessage ToMessage(string text, GitLabUserRef? author, string? botUsername)
    {
        var isBot = !string.IsNullOrEmpty(botUsername) && string.Equals(author?.Username, botUsername, StringComparison.OrdinalIgnoreCase);
        return new ChatMessage(isBot ? ChatRole.Assistant : ChatRole.User, text.Trim())
        {
            AuthorName = isBot ? null : author?.Username ?? author?.Name,
        };
    }

    private async Task<string?> GetBotUsernameAsync(GitLabOptions options, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.BotUsername))
        {
            return options.BotUsername;
        }

        if (_resolvedBotUsername is not null)
        {
            return _resolvedBotUsername;
        }

        try
        {
            var me = await _client.GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
            _resolvedBotUsername = me.Username;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve the bot's GitLab username");
        }

        return _resolvedBotUsername;
    }
}
