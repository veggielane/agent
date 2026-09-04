using System.Globalization;
using Agent.Core.Channels;
using Agent.Core.Events;
using Agent.Core.Replies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Channels.GitLab;

/// <summary>Replies as notes on the issue / merge request (threaded into the discussion when the mention was in one); acknowledges with award emoji.</summary>
public sealed class GitLabReplySender : IReplySender
{
    private readonly IGitLabClient _client;
    private readonly IOptionsMonitor<GitLabOptions> _options;
    private readonly ILogger<GitLabReplySender> _logger;

    public GitLabReplySender(IGitLabClient client, IOptionsMonitor<GitLabOptions> options, ILogger<GitLabReplySender> logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    public Channel Channel => Channel.GitLab;

    public async Task SendAsync(InboundEvent source, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var target = ResolveTarget(source) ?? throw new InvalidOperationException($"Cannot reply on GitLab: event {source.EventId} carries no issue or merge request reference.");
        if (target.Type == GitLabNoteableType.MergeRequest)
        {
            var discussionId = source.Meta(GitLabEventMapper.DiscussionIdKey);
            if (!string.IsNullOrEmpty(discussionId))
            {
                await _client.CreateDiscussionReplyAsync(target.Project, target.Iid, discussionId, text, cancellationToken).ConfigureAwait(false);
                return;
            }

            await _client.CreateMergeRequestNoteAsync(target.Project, target.Iid, text, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _client.CreateIssueNoteAsync(target.Project, target.Iid, text, cancellationToken).ConfigureAwait(false);
    }

    public async Task AcknowledgeAsync(InboundEvent source, AckState state, string? note, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var emoji = state switch
        {
            AckState.Working => options.AckEmoji,
            AckState.Done => options.DoneEmoji,
            AckState.Failed => options.FailedEmoji,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(emoji))
        {
            return;
        }

        var target = ResolveTarget(source);
        if (target is null)
        {
            return;
        }

        try
        {
            if (long.TryParse(source.Meta(GitLabEventMapper.NoteIdKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var noteId))
            {
                await _client.AwardEmojiOnNoteAsync(target.Project, target.Type, target.Iid, noteId, emoji, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _client.AwardEmojiAsync(target.Project, target.Type, target.Iid, emoji, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Award emoji {Emoji} for {State} on {Conversation} failed", emoji, state, source.ConversationId);
        }
    }

    /// <summary>Metadata first (numeric project id, iid, target type), then the <c>path#iid</c> conversation id.</summary>
    internal static ReplyTarget? ResolveTarget(InboundEvent source)
    {
        var project = source.Meta(GitLabEventMapper.ProjectIdKey) ?? source.Meta(GitLabEventMapper.ProjectPathKey);
        if (!string.IsNullOrWhiteSpace(project)
            && long.TryParse(source.Meta(GitLabEventMapper.IidKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var iid)
            && GitLabNoteableTypeExtensions.TryParse(source.Meta(GitLabEventMapper.TargetTypeKey), out var type))
        {
            return new ReplyTarget(project, iid, type);
        }

        return GitLabRefs.TryParse(source.ConversationId, out var path, out var parsedIid, out var parsedType)
            ? new ReplyTarget(path, parsedIid, parsedType)
            : null;
    }

    internal sealed record ReplyTarget(string Project, long Iid, GitLabNoteableType Type);
}
