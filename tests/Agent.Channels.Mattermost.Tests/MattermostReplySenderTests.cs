using Agent.Core.Channels;
using Agent.Core.Replies;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using static Agent.Channels.Mattermost.Tests.Fixtures;

namespace Agent.Channels.Mattermost.Tests;

public sealed class MattermostReplySenderTests
{
    private readonly IMattermostClient _client = Substitute.For<IMattermostClient>();
    private readonly IMattermostUserDirectory _users = Substitute.For<IMattermostUserDirectory>();
    private readonly StaticOptionsMonitor<MattermostOptions> _options = Monitor();
    private readonly MattermostReplySender _sender;

    public MattermostReplySenderTests()
    {
        _users.GetMeAsync(Arg.Any<CancellationToken>()).Returns(BotUser);
        _client.CreatePostAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => Post(id: "reply", userId: "bot1", channelId: call.ArgAt<string>(0), message: call.ArgAt<string>(1), rootId: call.ArgAt<string?>(2) ?? string.Empty));
        _sender = new MattermostReplySender(_client, _users, _options, NullLogger<MattermostReplySender>.Instance);
    }

    [Fact]
    public void Channel_IsMattermost() => Assert.Equal(Channel.Mattermost, _sender.Channel);

    [Fact]
    public async Task SendAsync_ChannelPost_RepliesInThreadRootedAtThePost()
    {
        await _sender.SendAsync(Event(postId: "p1", channelType: "O"), "answer", TestContext.Current.CancellationToken);

        await _client.Received(1).CreatePostAsync("c1", "answer", "p1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_ChannelReply_UsesExistingRoot()
    {
        await _sender.SendAsync(Event(postId: "p2", rootId: "r1", channelType: "P"), "answer", TestContext.Current.CancellationToken);

        await _client.Received(1).CreatePostAsync("c1", "answer", "r1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_DirectMessage_PostsWithoutRoot()
    {
        await _sender.SendAsync(Event(postId: "p1", channelType: "D"), "answer", TestContext.Current.CancellationToken);

        await _client.Received(1).CreatePostAsync("c1", "answer", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_DirectMessageInsideThread_KeepsTheThread()
    {
        await _sender.SendAsync(Event(postId: "p2", rootId: "r1", channelType: "D"), "answer", TestContext.Current.CancellationToken);

        await _client.Received(1).CreatePostAsync("c1", "answer", "r1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_LongText_PostsChunksInOrder()
    {
        _options.CurrentValue.MaxPostLength = 80;
        var text = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i:D2} with some padding text"));
        var posted = new List<string>();
        _ = _client.CreatePostAsync("c1", Arg.Do<string>(posted.Add), "p1", Arg.Any<CancellationToken>());

        await _sender.SendAsync(Event(channelType: "O"), text, TestContext.Current.CancellationToken);

        Assert.True(posted.Count > 1);
        Assert.All(posted, p => Assert.True(p.Length <= 80));
        Assert.Equal(text, string.Join('\n', posted));
    }

    [Fact]
    public async Task SendAsync_EmptyText_PostsNothing()
    {
        await _sender.SendAsync(Event(), "   ", TestContext.Current.CancellationToken);

        await _client.DidNotReceiveWithAnyArgs().CreatePostAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task SendAsync_MissingChannelId_Throws()
    {
        var evt = Event() with { Metadata = new Dictionary<string, string>() };

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sender.SendAsync(evt, "answer", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AcknowledgeAsync_Working_AddsAckReaction()
    {
        await _sender.AcknowledgeAsync(Event(), AckState.Working, null, TestContext.Current.CancellationToken);

        await _client.Received(1).AddReactionAsync("bot1", "p1", "eyes", Arg.Any<CancellationToken>());
        await _client.DidNotReceiveWithAnyArgs().RemoveReactionAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task AcknowledgeAsync_Done_SwapsAckForDoneReaction()
    {
        await _sender.AcknowledgeAsync(Event(), AckState.Done, null, TestContext.Current.CancellationToken);

        Received.InOrder(() =>
        {
            _client.RemoveReactionAsync("bot1", "p1", "eyes", Arg.Any<CancellationToken>());
            _client.AddReactionAsync("bot1", "p1", "white_check_mark", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task AcknowledgeAsync_Failed_SwapsAckForFailedReaction()
    {
        await _sender.AcknowledgeAsync(Event(), AckState.Failed, "boom", TestContext.Current.CancellationToken);

        Received.InOrder(() =>
        {
            _client.RemoveReactionAsync("bot1", "p1", "eyes", Arg.Any<CancellationToken>());
            _client.AddReactionAsync("bot1", "p1", "x", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task AcknowledgeAsync_UsesConfiguredEmoji()
    {
        _options.CurrentValue.AckReaction = "hourglass";
        _options.CurrentValue.DoneReaction = "tada";

        await _sender.AcknowledgeAsync(Event(), AckState.Done, null, TestContext.Current.CancellationToken);

        await _client.Received(1).RemoveReactionAsync("bot1", "p1", "hourglass", Arg.Any<CancellationToken>());
        await _client.Received(1).AddReactionAsync("bot1", "p1", "tada", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcknowledgeAsync_ReactionErrors_AreSwallowed()
    {
        _client.RemoveReactionAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs(new MattermostApiException(System.Net.HttpStatusCode.BadRequest, "DELETE", "x", "nope"));
        _client.AddReactionAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs(new HttpRequestException("down"));

        await _sender.AcknowledgeAsync(Event(), AckState.Done, null, TestContext.Current.CancellationToken);

        await _client.Received(1).AddReactionAsync("bot1", "p1", "white_check_mark", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcknowledgeAsync_WhenBotIdentityUnavailable_DoesNothing()
    {
        _users.GetMeAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("down"));

        await _sender.AcknowledgeAsync(Event(), AckState.Working, null, TestContext.Current.CancellationToken);

        await _client.DidNotReceiveWithAnyArgs().AddReactionAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task AcknowledgeAsync_WithoutPostId_DoesNothing()
    {
        var evt = Event() with { Metadata = new Dictionary<string, string>() };

        await _sender.AcknowledgeAsync(evt, AckState.Working, null, TestContext.Current.CancellationToken);

        await _client.DidNotReceiveWithAnyArgs().AddReactionAsync(default!, default!, default!, default);
    }
}
