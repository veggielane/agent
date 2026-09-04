using Agent.Core.Channels;
using Agent.Core.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Agent.Channels.Mattermost.Tests.Fixtures;

namespace Agent.Channels.Mattermost.Tests;

public sealed class MattermostTaskNotifierTests
{
    private readonly IMattermostClient _client = Substitute.For<IMattermostClient>();
    private readonly StaticOptionsMonitor<MattermostOptions> _options = Monitor();
    private readonly MattermostTaskNotifier _notifier;

    public MattermostTaskNotifierTests()
    {
        _client.CreatePostAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Post(id: "n1", userId: "bot1"));
        _notifier = new MattermostTaskNotifier(_client, _options, NullLogger<MattermostTaskNotifier>.Instance);
    }

    private static AgentTask Task_(string channelId = "c1", string postId = "p1", string rootId = "")
        => new()
        {
            Id = 7,
            Source = TaskSource.Mattermost,
            SourceRef = "chat-7",
            NotifyChannel = Channel.Mattermost,
            ConversationId = string.IsNullOrEmpty(rootId) ? postId : rootId,
            Metadata =
            {
                [MattermostEventMapper.ChannelIdKey] = channelId,
                [MattermostEventMapper.PostIdKey] = postId,
                [MattermostEventMapper.RootIdKey] = rootId,
            },
        };

    [Fact]
    public void Channel_IsMattermost() => Assert.Equal(Channel.Mattermost, _notifier.Channel);

    [Fact]
    public async Task NotifyAsync_UsesRootIdWhenPresent()
    {
        await _notifier.NotifyAsync(Task_(rootId: "r1"), "Working on it", TestContext.Current.CancellationToken);

        await _client.Received(1).CreatePostAsync("c1", "Working on it", "r1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyAsync_FallsBackToPostIdAsRoot()
    {
        await _notifier.NotifyAsync(Task_(postId: "p9"), "MR opened: https://gitlab/mr/1", TestContext.Current.CancellationToken);

        await _client.Received(1).CreatePostAsync("c1", "MR opened: https://gitlab/mr/1", "p9", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyAsync_WithoutChannelId_DropsTheNotification()
    {
        var task = Task_();
        task.Metadata.Remove(MattermostEventMapper.ChannelIdKey);

        await _notifier.NotifyAsync(task, "lost", TestContext.Current.CancellationToken);

        await _client.DidNotReceiveWithAnyArgs().CreatePostAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task NotifyAsync_LongMarkdown_IsSplitIntoThreadPosts()
    {
        _options.CurrentValue.MaxPostLength = 64;
        var markdown = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"step {i:D2}: did a thing"));
        var posted = new List<string>();
        _ = _client.CreatePostAsync("c1", Arg.Do<string>(posted.Add), "p1", Arg.Any<CancellationToken>());

        await _notifier.NotifyAsync(Task_(), markdown, TestContext.Current.CancellationToken);

        Assert.True(posted.Count > 1);
        Assert.All(posted, p => Assert.True(p.Length <= 64));
        Assert.Equal(markdown, string.Join('\n', posted));
    }

    [Fact]
    public async Task NotifyAsync_EmptyMarkdown_PostsNothing()
    {
        await _notifier.NotifyAsync(Task_(), " ", TestContext.Current.CancellationToken);

        await _client.DidNotReceiveWithAnyArgs().CreatePostAsync(default!, default!, default, default);
    }
}
