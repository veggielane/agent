using Agent.Core.Channels;
using Agent.Core.Events;
using static Agent.Channels.Mattermost.Tests.Fixtures;

namespace Agent.Channels.Mattermost.Tests;

public sealed class MattermostEventMapperTests
{
    private readonly FakeTimeProvider _time = new();

    private MattermostEventMapper Mapper(Action<MattermostOptions>? configure = null)
        => new(Monitor(configure), _time);

    private Task<InboundEvent?> MapAsync(
        MattermostEventMapper mapper,
        MattermostPostedEvent posted,
        MattermostThreadLookup? thread = null,
        MattermostUserLookup? users = null)
        => mapper.MapAsync(posted, Bot, users ?? Users(Alice, Bob, OtherBot, BotUser), thread ?? NoThread(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task MapAsync_DirectMessageWithoutMention_ReturnsPrivateEvent()
    {
        var post = Post(message: "what is the deploy status?", createAt: 1725400000000);

        var evt = await MapAsync(Mapper(), Posted(post, "D"));

        Assert.NotNull(evt);
        Assert.Equal(Channel.Mattermost, evt.Channel);
        Assert.True(evt.IsPrivate);
        Assert.Equal("p1", evt.EventId);
        Assert.Equal("c1", evt.ConversationId);
        Assert.Equal("what is the deploy status?", evt.Text);
        Assert.Equal(InboundKind.Message, evt.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1725400000000), evt.Timestamp);
        Assert.Equal("u1", evt.Caller.ChannelUserId);
        Assert.Equal("alice", evt.Caller.Username);
        Assert.Equal("alice@corp.local", evt.Caller.Email);
        Assert.Equal("p1", evt.Meta("post_id"));
        Assert.Equal("c1", evt.Meta("channel_id"));
        Assert.Equal(string.Empty, evt.Meta("root_id"));
        Assert.Equal("D", evt.Meta("channel_type"));
        Assert.Equal("u1", evt.Meta("user_id"));
    }

    [Fact]
    public async Task MapAsync_DirectMessageInThread_UsesRootAsConversation()
    {
        var evt = await MapAsync(Mapper(), Posted(Post(rootId: "r1"), "D"));

        Assert.NotNull(evt);
        Assert.Equal("r1", evt.ConversationId);
        Assert.Equal("r1", evt.Meta("root_id"));
    }

    [Fact]
    public async Task MapAsync_DirectMessage_WhenDisabled_ReturnsNull()
    {
        var evt = await MapAsync(Mapper(o => o.RespondToDirectMessages = false), Posted(Post(), "D"));

        Assert.Null(evt);
    }

    [Fact]
    public async Task MapAsync_ChannelPostWithoutMention_ReturnsNull()
    {
        var evt = await MapAsync(Mapper(), Posted(Post(message: "just chatting"), "O"));

        Assert.Null(evt);
    }

    [Fact]
    public async Task MapAsync_ChannelPostWithMention_StripsMentionAndUsesPostIdAsConversation()
    {
        var post = Post(message: "@agent: what is up?");

        var evt = await MapAsync(Mapper(), Posted(post, "O", "bot1"));

        Assert.NotNull(evt);
        Assert.False(evt.IsPrivate);
        Assert.Equal("what is up?", evt.Text);
        Assert.Equal("p1", evt.ConversationId);
        Assert.Equal(string.Empty, evt.Meta("root_id"));
        Assert.Equal("O", evt.Meta("channel_type"));
    }

    [Fact]
    public async Task MapAsync_ChannelReplyWithMention_UsesRootIdAsConversation()
    {
        var post = Post(message: "@agent please summarise", rootId: "r1");

        var evt = await MapAsync(Mapper(), Posted(post, "P", "bot1"));

        Assert.NotNull(evt);
        Assert.Equal("r1", evt.ConversationId);
        Assert.Equal("r1", evt.Meta("root_id"));
        Assert.Equal("please summarise", evt.Text);
    }

    [Fact]
    public async Task MapAsync_MentionOnlyInText_IsDetectedAndStripped()
    {
        var post = Post(message: "hey @agent can you help?");

        var evt = await MapAsync(Mapper(), Posted(post, "O"));

        Assert.NotNull(evt);
        Assert.Equal("hey can you help?", evt.Text);
    }

    [Fact]
    public async Task MapAsync_SimilarUsernameOrEmail_IsNotAMention()
    {
        var evt = await MapAsync(Mapper(), Posted(Post(message: "ping @agent2 or mail@agent.com"), "O"));

        Assert.Null(evt);
    }

    [Fact]
    public async Task MapAsync_RespondToMentionsDisabled_IgnoresMention()
    {
        var evt = await MapAsync(Mapper(o => o.RespondToMentions = false), Posted(Post(message: "@agent hi"), "O", "bot1"));

        Assert.Null(evt);
    }

    [Fact]
    public async Task MapAsync_ThreadContinuation_InsideWindow_ReturnsEvent()
    {
        var botReplyAt = _time.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds();
        var thread = Thread(
            Post(id: "r1", message: "@agent start", createAt: 1000),
            Post(id: "b1", userId: "bot1", message: "sure", rootId: "r1", createAt: botReplyAt),
            Post(id: "p1", message: "and then?", rootId: "r1", createAt: _time.UtcNow.ToUnixTimeMilliseconds()));

        var evt = await MapAsync(Mapper(), Posted(Post(message: "and then?", rootId: "r1"), "O"), thread);

        Assert.NotNull(evt);
        Assert.Equal("r1", evt.ConversationId);
        Assert.Equal("and then?", evt.Text);
    }

    [Fact]
    public async Task MapAsync_ThreadContinuation_OutsideWindow_ReturnsNull()
    {
        var botReplyAt = _time.UtcNow.AddMinutes(-121).ToUnixTimeMilliseconds();
        var thread = Thread(
            Post(id: "r1", message: "@agent start", createAt: 1000),
            Post(id: "b1", userId: "bot1", message: "sure", rootId: "r1", createAt: botReplyAt));

        var evt = await MapAsync(Mapper(), Posted(Post(message: "and then?", rootId: "r1"), "O"), thread);

        Assert.Null(evt);
    }

    [Fact]
    public async Task MapAsync_ThreadContinuation_WindowIsConfigurable()
    {
        var botReplyAt = _time.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds();
        var thread = Thread(Post(id: "b1", userId: "bot1", message: "sure", rootId: "r1", createAt: botReplyAt));

        var evt = await MapAsync(Mapper(o => o.ThreadFollowMinutes = 10), Posted(Post(message: "and then?", rootId: "r1"), "O"), thread);

        Assert.Null(evt);
    }

    [Fact]
    public async Task MapAsync_ThreadWithoutBotPost_ReturnsNull()
    {
        var thread = Thread(
            Post(id: "r1", message: "hello all", createAt: 1000),
            Post(id: "x1", userId: "u2", message: "hi", rootId: "r1", createAt: 2000));

        var evt = await MapAsync(Mapper(), Posted(Post(message: "anyone?", rootId: "r1"), "O"), thread);

        Assert.Null(evt);
    }

    [Fact]
    public async Task MapAsync_ThreadFollowDisabled_DoesNotLookUpThread()
    {
        var evt = await MapAsync(Mapper(o => o.ThreadFollowMinutes = 0), Posted(Post(message: "and then?", rootId: "r1"), "O"), NoThread());

        Assert.Null(evt);
    }

    [Fact]
    public async Task MapAsync_OwnPost_ReturnsNull()
    {
        var evt = await MapAsync(Mapper(), Posted(Post(userId: "bot1", message: "@agent hi"), "D", "bot1"));

        Assert.Null(evt);
    }

    [Fact]
    public async Task MapAsync_PostByAnotherBot_ReturnsNull()
    {
        var evt = await MapAsync(Mapper(), Posted(Post(userId: "b2", message: "build failed"), "D"));

        Assert.Null(evt);
    }

    [Fact]
    public async Task MapAsync_SystemPost_ReturnsNull()
    {
        var evt = await MapAsync(Mapper(), Posted(Post(message: "alice joined the channel", type: "system_join_channel"), "D"));

        Assert.Null(evt);
    }

    [Fact]
    public async Task MapAsync_AllowList_BlocksUnlistedChannel()
    {
        var evt = await MapAsync(Mapper(o => o.ChannelAllowList = ["other-channel", "c9"]), Posted(Post(message: "@agent hi"), "O", "bot1"));

        Assert.Null(evt);
    }

    [Fact]
    public async Task MapAsync_AllowList_AllowsChannelById()
    {
        var evt = await MapAsync(Mapper(o => o.ChannelAllowList = ["c1"]), Posted(Post(message: "@agent hi"), "O", "bot1"));

        Assert.NotNull(evt);
    }

    [Fact]
    public async Task MapAsync_AllowList_AllowsChannelByName()
    {
        var evt = await MapAsync(Mapper(o => o.ChannelAllowList = ["Town-Square"]), Posted(Post(message: "@agent hi"), "O", "bot1"));

        Assert.NotNull(evt);
    }

    [Fact]
    public async Task MapAsync_AllowList_DoesNotApplyToDirectMessages()
    {
        var evt = await MapAsync(Mapper(o => o.ChannelAllowList = ["c9"]), Posted(Post(), "D"));

        Assert.NotNull(evt);
    }

    [Fact]
    public async Task MapAsync_GroupMessageWithoutMention_ReturnsNullByDefault()
    {
        var evt = await MapAsync(Mapper(), Posted(Post(message: "hello group"), "G"));

        Assert.Null(evt);
    }

    [Fact]
    public async Task MapAsync_GroupMessageWithMention_ReturnsEvent()
    {
        var evt = await MapAsync(Mapper(), Posted(Post(message: "@agent hello group"), "G", "bot1"));

        Assert.NotNull(evt);
        Assert.False(evt.IsPrivate);
        Assert.Equal("hello group", evt.Text);
    }

    [Fact]
    public async Task MapAsync_GroupMessage_WhenMentionNotRequired_ReturnsEvent()
    {
        var evt = await MapAsync(Mapper(o => o.GroupMessagesRequireMention = false), Posted(Post(message: "hello group"), "G"));

        Assert.NotNull(evt);
        Assert.Equal("hello group", evt.Text);
        Assert.Equal("p1", evt.ConversationId);
    }

    [Fact]
    public async Task MapAsync_UnknownUser_FallsBackToSenderName()
    {
        var evt = await MapAsync(Mapper(), Posted(Post(userId: "ghost"), "D"), users: Users());

        Assert.NotNull(evt);
        Assert.Equal("ghost", evt.Caller.ChannelUserId);
        Assert.Equal("alice", evt.Caller.Username);
        Assert.Null(evt.Caller.Email);
    }

    [Fact]
    public async Task MapAsync_ChannelPostWithoutMention_DoesNotLookUpUser()
    {
        var lookups = 0;
        MattermostUserLookup counting = (id, _) =>
        {
            lookups++;
            return Task.FromResult<MattermostUser?>(Alice);
        };

        await MapAsync(Mapper(), Posted(Post(message: "unrelated"), "O"), users: counting);

        Assert.Equal(0, lookups);
    }

    [Theory]
    [InlineData("@agent hello", "hello")]
    [InlineData("@agent: hello", "hello")]
    [InlineData("@agent, hello", "hello")]
    [InlineData("  @Agent   hello  ", "hello")]
    [InlineData("hello @agent", "hello")]
    [InlineData("hello @agent world", "hello world")]
    [InlineData("hello", "hello")]
    [InlineData("@agent", "")]
    [InlineData("see @agent2 and mail@agent.com", "see @agent2 and mail@agent.com")]
    public void StripMention_RemovesBotMentionOnly(string input, string expected)
    {
        Assert.Equal(expected, MattermostEventMapper.StripMention(input, "agent"));
    }

    [Fact]
    public void IsMentioned_UsesMentionListOrText()
    {
        Assert.True(MattermostEventMapper.IsMentioned(Posted(Post(message: "hi"), "O", "bot1"), Bot));
        Assert.True(MattermostEventMapper.IsMentioned(Posted(Post(message: "hi @AGENT"), "O"), Bot));
        Assert.False(MattermostEventMapper.IsMentioned(Posted(Post(message: "hi"), "O", "someone-else"), Bot));
    }
}
