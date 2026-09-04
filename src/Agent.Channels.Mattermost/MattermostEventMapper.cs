using System.Text.RegularExpressions;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agent.Channels.Mattermost;

/// <summary>The bot account as seen by the mapper.</summary>
public sealed record MattermostBotIdentity(string UserId, string Username);

public delegate Task<MattermostUser?> MattermostUserLookup(string userId, CancellationToken cancellationToken);

public delegate Task<IReadOnlyList<MattermostPost>> MattermostThreadLookup(string rootId, CancellationToken cancellationToken);

/// <summary>
/// Decides whether a Mattermost post is addressed to the agent and turns it into an <see cref="InboundEvent"/>.
/// Pure apart from the two lookups it is handed, so the rules are testable without a server.
/// </summary>
public sealed class MattermostEventMapper
{
    public const string PostIdKey = "post_id";
    public const string ChannelIdKey = "channel_id";
    public const string RootIdKey = "root_id";
    public const string ChannelTypeKey = "channel_type";
    public const string UserIdKey = "user_id";
    public const string ChannelNameKey = "channel_name";

    private readonly IOptionsMonitor<MattermostOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MattermostEventMapper> _logger;

    public MattermostEventMapper(
        IOptionsMonitor<MattermostOptions> options,
        TimeProvider? timeProvider = null,
        ILogger<MattermostEventMapper>? logger = null)
    {
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<MattermostEventMapper>.Instance;
    }

    /// <summary>Returns the event for the post, or null when the agent should stay silent.</summary>
    public async Task<InboundEvent?> MapAsync(
        MattermostPostedEvent posted,
        MattermostBotIdentity bot,
        MattermostUserLookup getUser,
        MattermostThreadLookup getThread,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var post = posted.Post;

        if (string.Equals(post.UserId, bot.UserId, StringComparison.Ordinal))
        {
            return Skip(post, "own post");
        }

        if (post.IsSystemPost)
        {
            return Skip(post, "system post");
        }

        var isDirect = string.Equals(posted.ChannelType, MattermostChannelTypes.Direct, StringComparison.Ordinal);
        if (!isDirect && !IsAllowed(options, post.ChannelId, posted.ChannelName))
        {
            return Skip(post, "channel not in allow list");
        }

        bool addressed;
        if (isDirect)
        {
            addressed = options.RespondToDirectMessages;
        }
        else
        {
            var isGroup = string.Equals(posted.ChannelType, MattermostChannelTypes.Group, StringComparison.Ordinal);
            addressed = (options.RespondToMentions && IsMentioned(posted, bot))
                        || (isGroup && !options.GroupMessagesRequireMention);
            if (!addressed && post.IsReply)
            {
                addressed = await IsThreadContinuationAsync(post, bot, options, getThread, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!addressed)
        {
            return Skip(post, "not addressed to the bot");
        }

        var user = await getUser(post.UserId, cancellationToken).ConfigureAwait(false);
        if (user is { IsBot: true })
        {
            return Skip(post, "posted by a bot");
        }

        var caller = user is null
            ? new CallerIdentity(Channel.Mattermost, post.UserId, NormalizeSender(posted.SenderName))
            : new CallerIdentity(Channel.Mattermost, user.Id, user.Username, string.IsNullOrWhiteSpace(user.Email) ? null : user.Email);

        var conversationId = post.IsReply
            ? post.RootId
            : isDirect ? post.ChannelId : post.Id;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PostIdKey] = post.Id,
            [ChannelIdKey] = post.ChannelId,
            [RootIdKey] = post.RootId ?? string.Empty,
            [ChannelTypeKey] = posted.ChannelType,
            [UserIdKey] = post.UserId,
        };
        if (!string.IsNullOrEmpty(posted.ChannelName))
        {
            metadata[ChannelNameKey] = posted.ChannelName;
        }

        return new InboundEvent
        {
            Channel = Channel.Mattermost,
            EventId = post.Id,
            Caller = caller,
            ConversationId = conversationId,
            Text = StripMention(post.Message ?? string.Empty, bot.Username),
            Kind = InboundKind.Message,
            Timestamp = post.CreatedAt,
            IsPrivate = isDirect,
            Metadata = metadata,
        };
    }

    /// <summary>True when the post names the bot, either via the server's mention list or literally in the text.</summary>
    public static bool IsMentioned(MattermostPostedEvent posted, MattermostBotIdentity bot)
    {
        if (posted.Mentions.Contains(bot.UserId, StringComparer.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrEmpty(bot.Username)
               && MentionPattern(bot.Username).IsMatch(posted.Post.Message ?? string.Empty);
    }

    /// <summary>Removes a leading <c>@bot</c> (with an optional colon or comma) and inline occurrences, then trims.</summary>
    public static string StripMention(string text, string botUsername)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(botUsername))
        {
            return text.Trim();
        }

        var mention = MentionCore(botUsername);
        var withoutLeading = Regex.Replace(text, @"^\s*" + mention + @"[:,]?\s*", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var withoutInline = Regex.Replace(withoutLeading, @"[ \t]?" + mention, string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return withoutInline.Trim();
    }

    private static Regex MentionPattern(string botUsername)
        => new(MentionCore(botUsername), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string MentionCore(string botUsername)
        => @"(?<![\w.\-@])@" + Regex.Escape(botUsername) + @"(?![\w.\-])";

    private static bool IsAllowed(MattermostOptions options, string channelId, string? channelName)
    {
        if (options.ChannelAllowList is null || options.ChannelAllowList.Length == 0)
        {
            return true;
        }

        foreach (var entry in options.ChannelAllowList)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var candidate = entry.Trim();
            if (string.Equals(candidate, channelId, StringComparison.OrdinalIgnoreCase)
                || (channelName is not null && string.Equals(candidate, channelName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> IsThreadContinuationAsync(
        MattermostPost post,
        MattermostBotIdentity bot,
        MattermostOptions options,
        MattermostThreadLookup getThread,
        CancellationToken cancellationToken)
    {
        if (!post.IsReply || options.ThreadFollowMinutes <= 0)
        {
            return false;
        }

        IReadOnlyList<MattermostPost> thread;
        try
        {
            thread = await getThread(post.RootId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not load thread {RootId} to check for continuation", post.RootId);
            return false;
        }

        var cutoff = _time.GetUtcNow() - TimeSpan.FromMinutes(options.ThreadFollowMinutes);
        return thread.Any(p =>
            string.Equals(p.UserId, bot.UserId, StringComparison.Ordinal)
            && !string.Equals(p.Id, post.Id, StringComparison.Ordinal)
            && p.CreatedAt >= cutoff);
    }

    private static string? NormalizeSender(string? senderName)
    {
        if (string.IsNullOrWhiteSpace(senderName))
        {
            return null;
        }

        return senderName.Trim().TrimStart('@');
    }

    private InboundEvent? Skip(MattermostPost post, string reason)
    {
        _logger.LogTrace("Ignoring Mattermost post {PostId}: {Reason}", post.Id, reason);
        return null;
    }
}
