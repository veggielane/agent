using Agent.Core.Channels;
using Agent.Core.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Channels.Mattermost;

/// <summary>
/// Posts task progress into the thread the task was requested from. The channel and post ids travel from
/// <c>InboundEvent.Metadata</c> into <c>AgentTask.Metadata</c> when the task is created.
/// </summary>
public sealed class MattermostTaskNotifier : ITaskNotifier
{
    private readonly IMattermostClient _client;
    private readonly IOptionsMonitor<MattermostOptions> _options;
    private readonly ILogger<MattermostTaskNotifier> _logger;

    public MattermostTaskNotifier(IMattermostClient client, IOptionsMonitor<MattermostOptions> options, ILogger<MattermostTaskNotifier> logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    public Channel Channel => Channel.Mattermost;

    public async Task NotifyAsync(AgentTask task, string markdown, CancellationToken cancellationToken)
    {
        if (!task.Metadata.TryGetValue(MattermostEventMapper.ChannelIdKey, out var channelId) || string.IsNullOrEmpty(channelId))
        {
            _logger.LogWarning("Task {Task} has no Mattermost channel_id; notification dropped: {Message}", task.DisplayRef, markdown);
            return;
        }

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return;
        }

        var rootId = task.Metadata.TryGetValue(MattermostEventMapper.RootIdKey, out var root) && !string.IsNullOrEmpty(root)
            ? root
            : task.Metadata.TryGetValue(MattermostEventMapper.PostIdKey, out var post) && !string.IsNullOrEmpty(post)
                ? post
                : null;

        foreach (var chunk in MattermostMessageSplitter.Split(markdown, _options.CurrentValue.MaxPostLength))
        {
            await _client.CreatePostAsync(channelId, chunk, rootId, cancellationToken).ConfigureAwait(false);
        }
    }
}
