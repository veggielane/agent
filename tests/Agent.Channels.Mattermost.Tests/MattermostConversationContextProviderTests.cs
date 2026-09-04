using Agent.Core.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Agent.Channels.Mattermost.Tests.Fixtures;

namespace Agent.Channels.Mattermost.Tests;

public sealed class MattermostConversationContextProviderTests
{
    private readonly IMattermostClient _client = Substitute.For<IMattermostClient>();
    private readonly StaticOptionsMonitor<MattermostOptions> _options = Monitor();
    private readonly MattermostConversationContextProvider _provider;

    public MattermostConversationContextProviderTests()
    {
        _client.GetMeAsync(Arg.Any<CancellationToken>()).Returns(BotUser);
        _client.GetUserAsync("u1", Arg.Any<CancellationToken>()).Returns(Alice);
        _client.GetUserAsync("u2", Arg.Any<CancellationToken>()).Returns(Bob);
        var users = new MattermostUserDirectory(_client, new MemoryCache(new MemoryCacheOptions()));
        _provider = new MattermostConversationContextProvider(_client, users, _options, NullLogger<MattermostConversationContextProvider>.Instance);
    }

    [Fact]
    public void Channel_IsMattermost() => Assert.Equal(Channel.Mattermost, _provider.Channel);

    [Fact]
    public async Task GetHistoryAsync_ThreadedPost_ReturnsThreadWithRolesAndAuthors()
    {
        _client.GetThreadAsync("r1", Arg.Any<CancellationToken>()).Returns([
            Post(id: "r1", message: "@agent what is x?", createAt: 1000),
            Post(id: "b1", userId: "bot1", message: "x is 42", rootId: "r1", createAt: 2000),
            Post(id: "p3", userId: "u2", message: "and y?", rootId: "r1", createAt: 3000),
            Post(id: "p4", message: "yes, y too", rootId: "r1", createAt: 4000),
        ]);

        var history = await _provider.GetHistoryAsync(Event(postId: "p4", rootId: "r1", createAt: 4000), 40, TestContext.Current.CancellationToken);

        Assert.Collection(
            history,
            m =>
            {
                Assert.Equal(ChatRole.User, m.Role);
                Assert.Equal("alice", m.AuthorName);
                Assert.Equal("@agent what is x?", m.Text);
            },
            m =>
            {
                Assert.Equal(ChatRole.Assistant, m.Role);
                Assert.Equal("x is 42", m.Text);
            },
            m =>
            {
                Assert.Equal(ChatRole.User, m.Role);
                Assert.Equal("bob", m.AuthorName);
                Assert.Equal("and y?", m.Text);
            });
    }

    [Fact]
    public async Task GetHistoryAsync_Thread_KeepsOnlyTheLastMaxMessages()
    {
        _client.GetThreadAsync("r1", Arg.Any<CancellationToken>()).Returns([
            Post(id: "r1", message: "one", createAt: 1000),
            Post(id: "b1", userId: "bot1", message: "two", rootId: "r1", createAt: 2000),
            Post(id: "p3", message: "three", rootId: "r1", createAt: 3000),
            Post(id: "p4", message: "four", rootId: "r1", createAt: 4000),
        ]);

        var history = await _provider.GetHistoryAsync(Event(postId: "p4", rootId: "r1", createAt: 4000), 2, TestContext.Current.CancellationToken);

        Assert.Equal(["two", "three"], history.Select(m => m.Text).ToArray());
    }

    [Fact]
    public async Task GetHistoryAsync_Thread_SkipsSystemAndEmptyPosts()
    {
        _client.GetThreadAsync("r1", Arg.Any<CancellationToken>()).Returns([
            Post(id: "r1", message: "one", createAt: 1000),
            Post(id: "s1", message: "alice pinned a message", rootId: "r1", createAt: 1500, type: "system_generic"),
            Post(id: "f1", message: "", rootId: "r1", createAt: 1600),
            Post(id: "p4", message: "four", rootId: "r1", createAt: 4000),
        ]);

        var history = await _provider.GetHistoryAsync(Event(postId: "p4", rootId: "r1", createAt: 4000), 40, TestContext.Current.CancellationToken);

        Assert.Equal(["one"], history.Select(m => m.Text).ToArray());
    }

    [Fact]
    public async Task GetHistoryAsync_Thread_ExcludesPostsNewerThanTheEvent()
    {
        _client.GetThreadAsync("r1", Arg.Any<CancellationToken>()).Returns([
            Post(id: "r1", message: "one", createAt: 1000),
            Post(id: "p2", message: "two", rootId: "r1", createAt: 2000),
            Post(id: "p3", message: "typed afterwards", rootId: "r1", createAt: 3000),
        ]);

        var history = await _provider.GetHistoryAsync(Event(postId: "p2", rootId: "r1", createAt: 2000), 40, TestContext.Current.CancellationToken);

        Assert.Equal(["one"], history.Select(m => m.Text).ToArray());
    }

    [Fact]
    public async Task GetHistoryAsync_DirectMessage_UsesRecentChannelPosts()
    {
        _client.GetPostsForChannelAsync("c1", null, 31, Arg.Any<CancellationToken>()).Returns([
            Post(id: "d1", message: "hi bot", createAt: 1000),
            Post(id: "d2", userId: "bot1", message: "hello alice", createAt: 2000),
            Post(id: "d3", message: "next question", createAt: 3000),
        ]);

        var history = await _provider.GetHistoryAsync(Event(postId: "d3", channelType: "D", createAt: 3000), 40, TestContext.Current.CancellationToken);

        Assert.Collection(
            history,
            m =>
            {
                Assert.Equal(ChatRole.User, m.Role);
                Assert.Equal("alice", m.AuthorName);
                Assert.Equal("hi bot", m.Text);
            },
            m =>
            {
                Assert.Equal(ChatRole.Assistant, m.Role);
                Assert.Equal("hello alice", m.Text);
            });
        await _client.DidNotReceiveWithAnyArgs().GetThreadAsync(default!, default);
    }

    [Fact]
    public async Task GetHistoryAsync_DirectMessage_IsLimitedByDmHistoryMessages()
    {
        _options.CurrentValue.DmHistoryMessages = 2;
        _client.GetPostsForChannelAsync("c1", null, 3, Arg.Any<CancellationToken>()).Returns([
            Post(id: "d1", message: "one", createAt: 1000),
            Post(id: "d2", userId: "bot1", message: "two", createAt: 2000),
            Post(id: "d3", message: "three", createAt: 3000),
            Post(id: "d4", message: "four", createAt: 4000),
        ]);

        var history = await _provider.GetHistoryAsync(Event(postId: "d4", channelType: "D", createAt: 4000), 40, TestContext.Current.CancellationToken);

        Assert.Equal(["two", "three"], history.Select(m => m.Text).ToArray());
    }

    [Fact]
    public async Task GetHistoryAsync_DirectMessageInThread_UsesTheThread()
    {
        _client.GetThreadAsync("r1", Arg.Any<CancellationToken>()).Returns([
            Post(id: "r1", message: "root", createAt: 1000),
            Post(id: "p2", message: "reply", rootId: "r1", createAt: 2000),
        ]);

        var history = await _provider.GetHistoryAsync(Event(postId: "p2", rootId: "r1", channelType: "D", createAt: 2000), 40, TestContext.Current.CancellationToken);

        Assert.Equal(["root"], history.Select(m => m.Text).ToArray());
        await _client.DidNotReceiveWithAnyArgs().GetPostsForChannelAsync(default!, default, default, default);
    }

    [Fact]
    public async Task GetHistoryAsync_FreshChannelMention_HasNoHistory()
    {
        var history = await _provider.GetHistoryAsync(Event(postId: "p1", channelType: "O"), 40, TestContext.Current.CancellationToken);

        Assert.Empty(history);
        await _client.DidNotReceiveWithAnyArgs().GetThreadAsync(default!, default);
        await _client.DidNotReceiveWithAnyArgs().GetPostsForChannelAsync(default!, default, default, default);
    }

    [Fact]
    public async Task GetHistoryAsync_UnknownAuthor_FallsBackToUserId()
    {
        _client.GetThreadAsync("r1", Arg.Any<CancellationToken>()).Returns([
            Post(id: "r1", userId: "ghost", message: "boo", createAt: 1000),
            Post(id: "p2", message: "reply", rootId: "r1", createAt: 2000),
        ]);

        var history = await _provider.GetHistoryAsync(Event(postId: "p2", rootId: "r1", createAt: 2000), 40, TestContext.Current.CancellationToken);

        Assert.Equal("ghost", Assert.Single(history).AuthorName);
    }

    [Fact]
    public async Task GetHistoryAsync_CachesUserLookups()
    {
        _client.GetThreadAsync("r1", Arg.Any<CancellationToken>()).Returns([
            Post(id: "r1", message: "one", createAt: 1000),
            Post(id: "p2", message: "two", rootId: "r1", createAt: 2000),
            Post(id: "p3", message: "three", rootId: "r1", createAt: 3000),
            Post(id: "p4", message: "four", rootId: "r1", createAt: 4000),
        ]);

        await _provider.GetHistoryAsync(Event(postId: "p4", rootId: "r1", createAt: 4000), 40, TestContext.Current.CancellationToken);

        await _client.Received(1).GetUserAsync("u1", Arg.Any<CancellationToken>());
    }
}
