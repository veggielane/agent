using Agent.Core.Channels;
using Agent.Core.Tasks;
using NSubstitute;
using static Agent.Channels.Mattermost.Tests.Fixtures;

namespace Agent.Channels.Mattermost.Tests;

public sealed class MattermostTaskMirrorTests
{
    private readonly IMattermostClient _client = Substitute.For<IMattermostClient>();
    private readonly StaticOptionsMonitor<MattermostOptions> _options = Monitor(o => o.OpsChannelId = "ops-channel");
    private readonly MattermostTaskMirror _mirror;

    public MattermostTaskMirrorTests()
    {
        _client.CreatePostAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Post(id: "n1", userId: "bot1"));
        _mirror = new MattermostTaskMirror(_client, _options);
    }

    private static AgentTask Task_() => new()
    {
        Id = 12,
        Source = TaskSource.GitLabIssue,
        SourceRef = "team/repo#5",
        SourceUrl = "https://gitlab.test/team/repo/-/issues/5",
        Title = "Add caching",
        RequesterName = "Alice",
        NotifyChannel = Channel.GitLab,
        ConversationId = "team/repo#5",
        Metadata = { [MattermostEventMapper.ChannelIdKey] = "origin-channel", [MattermostEventMapper.RootIdKey] = "root-1" },
    };

    [Fact]
    public void Channel_IsMattermost() => Assert.Equal(Channel.Mattermost, _mirror.Channel);

    [Fact]
    public void Enabled_FollowsTheOpsChannelSetting()
    {
        Assert.True(_mirror.Enabled);

        _options.CurrentValue = Options(o => o.OpsChannelId = "  ");
        Assert.False(_mirror.Enabled);
    }

    [Fact]
    public async Task MirrorAsync_PostsATopLevelPostInTheOpsChannel_WithTaskContextFirst()
    {
        await _mirror.MirrorAsync(Task_(), new TaskNotification("published:1", "Opened MR https://gitlab.test/mr/7\n\nVerified: yes", Terminal: true), TestContext.Current.CancellationToken);

        await _client.Received(1).CreatePostAsync(
            Arg.Is("ops-channel"),
            Arg.Is<string>(m => m.StartsWith("**Task #12** · [team/repo#5](https://gitlab.test/team/repo/-/issues/5) · Add caching · requested by Alice\n", StringComparison.Ordinal)
                                && m.EndsWith("Verified: yes", StringComparison.Ordinal)),
            Arg.Is<string?>(root => root == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MirrorAsync_Disabled_PostsNothing()
    {
        _options.CurrentValue = Options(o => o.OpsChannelId = string.Empty);

        await _mirror.MirrorAsync(Task_(), new TaskNotification("failed:1", "Task failed", Terminal: true), TestContext.Current.CancellationToken);

        await _client.DidNotReceiveWithAnyArgs().CreatePostAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task MirrorAsync_LongMarkdown_IsSplitIntoSeveralPosts()
    {
        _options.CurrentValue = Options(o =>
        {
            o.OpsChannelId = "ops-channel";
            o.MaxPostLength = 120;
        });

        await _mirror.MirrorAsync(Task_(), new TaskNotification("failed:1", new string('x', 300), Terminal: true), TestContext.Current.CancellationToken);

        Assert.True(_client.ReceivedCalls().Count() >= 3);
    }

    [Fact]
    public void Header_WithoutUrlOrTitle_StillNamesTheTicket()
    {
        var header = MattermostTaskMirror.Header(new AgentTask { Id = 3, SourceRef = "PROJ-9", RequesterName = "Bob" });

        Assert.Equal("**Task #3** · `PROJ-9` · requested by Bob", header);
    }
}
