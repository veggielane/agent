using Agent.Core.Channels;
using Agent.Core.Events;
using Agent.Core.Replies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Channels.Mattermost;

/// <summary>Posts replies into the thread (or DM) the event came from and mirrors progress as reactions.</summary>
public sealed class MattermostReplySender : IReplySender
{
    private readonly IMattermostClient _client;
    private readonly IMattermostUserDirectory _users;
    private readonly IOptionsMonitor<MattermostOptions> _options;
    private readonly ILogger<MattermostReplySender> _logger;

    public MattermostReplySender(
        IMattermostClient client,
        IMattermostUserDirectory users,
        IOptionsMonitor<MattermostOptions> options,
        ILogger<MattermostReplySender> logger)
    {
        _client = client;
        _users = users;
        _options = options;
        _logger = logger;
    }

    public Channel Channel => Channel.Mattermost;

    /// <summary>
    /// Channel posts are answered in a thread rooted at the triggering post; DMs are answered inline unless the
    /// user already wrote inside a thread.
    /// </summary>
    public static string? ResolveRootId(InboundEvent source)
    {
        var rootId = source.Meta(MattermostEventMapper.RootIdKey);
        if (!string.IsNullOrEmpty(rootId))
        {
            return rootId;
        }

        var isDirect = string.Equals(source.Meta(MattermostEventMapper.ChannelTypeKey), MattermostChannelTypes.Direct, StringComparison.Ordinal);
        return isDirect ? null : source.Meta(MattermostEventMapper.PostIdKey);
    }

    public async Task SendAsync(InboundEvent source, string text, CancellationToken cancellationToken)
    {
        var channelId = source.Meta(MattermostEventMapper.ChannelIdKey);
        if (string.IsNullOrEmpty(channelId))
        {
            throw new InvalidOperationException($"Mattermost event {source.EventId} carries no channel_id; cannot reply.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogDebug("Empty reply for Mattermost post {PostId}; nothing sent", source.EventId);
            return;
        }

        var rootId = ResolveRootId(source);
        foreach (var chunk in MattermostMessageSplitter.Split(text, _options.CurrentValue.MaxPostLength))
        {
            await _client.CreatePostAsync(channelId, chunk, rootId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task AcknowledgeAsync(InboundEvent source, AckState state, string? note, CancellationToken cancellationToken)
    {
        var postId = source.Meta(MattermostEventMapper.PostIdKey);
        if (string.IsNullOrEmpty(postId))
        {
            return;
        }

        var options = _options.CurrentValue;
        MattermostUser me;
        try
        {
            me = await _users.GetMeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Cannot resolve the bot user; skipping {State} reaction", state);
            return;
        }

        switch (state)
        {
            case AckState.Working:
                await TryReactAsync(me.Id, postId, options.AckReaction, add: true, cancellationToken).ConfigureAwait(false);
                break;
            case AckState.Done:
                await TryReactAsync(me.Id, postId, options.AckReaction, add: false, cancellationToken).ConfigureAwait(false);
                await TryReactAsync(me.Id, postId, options.DoneReaction, add: true, cancellationToken).ConfigureAwait(false);
                break;
            case AckState.Failed:
                await TryReactAsync(me.Id, postId, options.AckReaction, add: false, cancellationToken).ConfigureAwait(false);
                await TryReactAsync(me.Id, postId, options.FailedReaction, add: true, cancellationToken).ConfigureAwait(false);
                break;
            default:
                _logger.LogDebug("Unsupported ack state {State} for Mattermost", state);
                break;
        }
    }

    private async Task TryReactAsync(string userId, string postId, string emoji, bool add, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(emoji))
        {
            return;
        }

        try
        {
            if (add)
            {
                await _client.AddReactionAsync(userId, postId, emoji, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _client.RemoveReactionAsync(userId, postId, emoji, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Action} reaction :{Emoji}: on post {PostId} failed", add ? "Adding" : "Removing", emoji, postId);
        }
    }
}
