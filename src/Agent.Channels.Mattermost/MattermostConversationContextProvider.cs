using Agent.Core.Channels;
using Agent.Core.Conversations;
using Agent.Core.Events;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Channels.Mattermost;

/// <summary>
/// History for a Mattermost event: the thread when the post is a reply, the recent DM channel posts otherwise.
/// A fresh channel mention has no history.
/// </summary>
public sealed class MattermostConversationContextProvider : IConversationContextProvider
{
    private readonly IMattermostClient _client;
    private readonly IMattermostUserDirectory _users;
    private readonly IOptionsMonitor<MattermostOptions> _options;
    private readonly ILogger<MattermostConversationContextProvider> _logger;

    public MattermostConversationContextProvider(
        IMattermostClient client,
        IMattermostUserDirectory users,
        IOptionsMonitor<MattermostOptions> options,
        ILogger<MattermostConversationContextProvider> logger)
    {
        _client = client;
        _users = users;
        _options = options;
        _logger = logger;
    }

    public Channel Channel => Channel.Mattermost;

    public async Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(InboundEvent evt, int maxMessages, CancellationToken cancellationToken)
    {
        if (maxMessages <= 0)
        {
            return [];
        }

        var postId = evt.Meta(MattermostEventMapper.PostIdKey);
        var rootId = evt.Meta(MattermostEventMapper.RootIdKey);
        var channelId = evt.Meta(MattermostEventMapper.ChannelIdKey);
        var isDirect = string.Equals(evt.Meta(MattermostEventMapper.ChannelTypeKey), MattermostChannelTypes.Direct, StringComparison.Ordinal);

        IReadOnlyList<MattermostPost> posts;
        var limit = maxMessages;
        if (!string.IsNullOrEmpty(rootId))
        {
            posts = await _client.GetThreadAsync(rootId, cancellationToken).ConfigureAwait(false);
        }
        else if (isDirect && !string.IsNullOrEmpty(channelId))
        {
            var dmLimit = _options.CurrentValue.DmHistoryMessages;
            if (dmLimit <= 0)
            {
                return [];
            }

            limit = Math.Min(maxMessages, dmLimit);
            posts = await _client.GetPostsForChannelAsync(channelId, null, dmLimit + 1, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return [];
        }

        var me = await _users.GetMeAsync(cancellationToken).ConfigureAwait(false);
        var selected = posts
            .Where(p => !string.Equals(p.Id, postId, StringComparison.Ordinal))
            .Where(p => !p.IsSystemPost && !string.IsNullOrWhiteSpace(p.Message))
            .Where(p => p.CreatedAt <= evt.Timestamp)
            .OrderBy(p => p.CreateAt)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .TakeLast(limit);

        var history = new List<ChatMessage>();
        foreach (var post in selected)
        {
            if (string.Equals(post.UserId, me.Id, StringComparison.Ordinal))
            {
                history.Add(new ChatMessage(ChatRole.Assistant, post.Message));
                continue;
            }

            var author = await ResolveAuthorAsync(post.UserId, cancellationToken).ConfigureAwait(false);
            history.Add(new ChatMessage(ChatRole.User, post.Message) { AuthorName = author });
        }

        return history;
    }

    private async Task<string> ResolveAuthorAsync(string userId, CancellationToken cancellationToken)
    {
        try
        {
            var user = await _users.GetUserAsync(userId, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(user?.Username) ? userId : user.Username;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not resolve Mattermost user {UserId}; using the id", userId);
            return userId;
        }
    }
}
