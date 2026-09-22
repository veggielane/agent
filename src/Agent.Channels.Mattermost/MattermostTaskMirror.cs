using System.Globalization;
using System.Text;
using Agent.Core.Channels;
using Agent.Core.Tasks;
using Microsoft.Extensions.Options;

namespace Agent.Channels.Mattermost;

/// <summary>
/// Copies terminal task notifications to the operations channel named by <see cref="MattermostOptions.OpsChannelId"/>.
/// Each copy is a top-level post prefixed with the task, its ticket and its requester, because the channel has none of
/// the originating thread's context. The router deduplicates per key, so a repeated notification is posted once.
/// </summary>
public sealed class MattermostTaskMirror : ITaskNotificationMirror
{
    private readonly IMattermostClient _client;
    private readonly IOptionsMonitor<MattermostOptions> _options;

    public MattermostTaskMirror(IMattermostClient client, IOptionsMonitor<MattermostOptions> options)
    {
        _client = client;
        _options = options;
    }

    public Channel Channel => Channel.Mattermost;

    public bool Enabled => !string.IsNullOrWhiteSpace(_options.CurrentValue.OpsChannelId);

    public async Task MirrorAsync(AgentTask task, TaskNotification notification, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var channelId = options.OpsChannelId.Trim();
        if (channelId.Length == 0 || string.IsNullOrWhiteSpace(notification.Markdown))
        {
            return;
        }

        var text = Header(task) + "\n" + notification.Markdown.Trim();
        foreach (var chunk in MattermostMessageSplitter.Split(text, options.MaxPostLength))
        {
            await _client.CreatePostAsync(channelId, chunk, null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One line naming the task, its ticket (linked when the URL is known), its title and who asked for it.</summary>
    public static string Header(AgentTask task)
    {
        var sb = new StringBuilder();
        sb.Append("**Task #").Append(task.Id.ToString(CultureInfo.InvariantCulture)).Append("**");
        if (!string.IsNullOrWhiteSpace(task.SourceRef))
        {
            sb.Append(" · ");
            sb.Append(string.IsNullOrWhiteSpace(task.SourceUrl) ? $"`{task.SourceRef}`" : $"[{task.SourceRef}]({task.SourceUrl})");
        }

        if (!string.IsNullOrWhiteSpace(task.Title))
        {
            sb.Append(" · ").Append(task.Title.Trim());
        }

        if (!string.IsNullOrWhiteSpace(task.RequesterName))
        {
            sb.Append(" · requested by ").Append(task.RequesterName);
        }

        return sb.ToString();
    }
}
