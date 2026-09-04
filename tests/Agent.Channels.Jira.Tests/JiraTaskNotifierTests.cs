using Agent.Channels.Jira;
using Agent.Core.Channels;
using Agent.Core.Formatting;
using Agent.Core.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Agent.Channels.Jira.Tests;

public sealed class JiraTaskNotifierTests
{
    private readonly IJiraClient _client = Substitute.For<IJiraClient>();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private JiraTaskNotifier CreateNotifier()
        => new(_client, new FormatterRegistry([new JiraWikiFormatter()]), NullLogger<JiraTaskNotifier>.Instance);

    [Fact]
    public async Task NotifyAsync_PostsFormattedCommentOnTheSourceIssue()
    {
        var task = new AgentTask { Id = 3, Source = TaskSource.JiraIssue, SourceRef = "PROJ-1", ConversationId = "PROJ-1", NotifyChannel = Channel.Jira };

        await CreateNotifier().NotifyAsync(task, "Opened **MR** [!5](https://gitlab.corp.local/t/r/-/merge_requests/5)", _ct);

        await _client.Received(1).AddCommentAsync("PROJ-1", "Opened *MR* [!5|https://gitlab.corp.local/t/r/-/merge_requests/5]", _ct);
    }

    [Fact]
    public async Task NotifyAsync_NonJiraSource_UsesConversationId()
    {
        var task = new AgentTask { Id = 4, Source = TaskSource.Mattermost, SourceRef = "mm-post", ConversationId = "OPS-7", NotifyChannel = Channel.Jira };

        await CreateNotifier().NotifyAsync(task, "hello", _ct);

        await _client.Received(1).AddCommentAsync("OPS-7", "hello", _ct);
    }

    [Fact]
    public async Task NotifyAsync_NoIssueOrEmptyText_DoesNothing()
    {
        var notifier = CreateNotifier();

        await notifier.NotifyAsync(new AgentTask { Id = 5, Source = TaskSource.Cli }, "text", _ct);
        await notifier.NotifyAsync(new AgentTask { Id = 6, Source = TaskSource.JiraIssue, SourceRef = "PROJ-1" }, "  ", _ct);

        await _client.DidNotReceiveWithAnyArgs().AddCommentAsync(default!, default!, default);
        Assert.Equal(Channel.Jira, notifier.Channel);
    }
}
