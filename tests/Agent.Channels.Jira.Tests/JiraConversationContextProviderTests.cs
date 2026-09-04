using Agent.Channels.Jira;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Events;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Agent.Channels.Jira.Tests;

public sealed class JiraConversationContextProviderTests
{
    private readonly IJiraClient _client = Substitute.For<IJiraClient>();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private JiraConversationContextProvider CreateProvider(Action<JiraOptions>? configure = null)
        => new(_client, TestOptions.Monitor(TestOptions.Plain(configure)), NullLogger<JiraConversationContextProvider>.Instance);

    private static InboundEvent Event(string issue, string? commentId) => new()
    {
        Channel = Channel.Jira,
        EventId = commentId is null ? "label:x" : "comment:" + commentId,
        Caller = new CallerIdentity(Channel.Jira, "bob"),
        ConversationId = issue,
        Text = "question",
        Metadata = commentId is null
            ? new Dictionary<string, string> { ["issue"] = issue }
            : new Dictionary<string, string> { ["issue"] = issue, ["comment_id"] = commentId },
    };

    private void StubIssue(params object[] comments)
    {
        var issue = Fx.Parse(Fx.Issue("PROJ-1", "Login broken", description: "Users cannot log in.", reporter: "alice", comments: comments));
        _client.GetIssueAsync("PROJ-1", Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<CancellationToken>()).Returns(issue);
    }

    [Fact]
    public async Task GetHistoryAsync_DescriptionFirstThenCommentsWithRolesExcludingTrigger()
    {
        StubIssue(
            Fx.Comment("3", "bob", "[~agent-bot] what now?", Fx.T1010),
            Fx.Comment("1", "bob", "[~agent-bot] why?", Fx.T1000),
            Fx.Comment("2", "agent-bot", "Because of X.", Fx.T1005));

        var history = await CreateProvider().GetHistoryAsync(Event("PROJ-1", "3"), 40, _ct);

        Assert.Equal(3, history.Count);
        Assert.Equal(ChatRole.User, history[0].Role);
        Assert.Equal("alice", history[0].AuthorName);
        Assert.Equal("Summary: Login broken\n\nUsers cannot log in.", history[0].Text);
        Assert.Equal(ChatRole.User, history[1].Role);
        Assert.Equal("bob", history[1].AuthorName);
        Assert.Equal("[~agent-bot] why?", history[1].Text);
        Assert.Equal(ChatRole.Assistant, history[2].Role);
        Assert.Equal("Because of X.", history[2].Text);
        Assert.DoesNotContain(history, m => m.Text.Contains("what now?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetHistoryAsync_LimitKeepsDescriptionAndNewestComments()
    {
        StubIssue(
            Fx.Comment("1", "bob", "one", Fx.T1000),
            Fx.Comment("2", "bob", "two", Fx.T1005),
            Fx.Comment("3", "bob", "three", Fx.T1010),
            Fx.Comment("4", "bob", "trigger", Fx.T1017));

        var history = await CreateProvider().GetHistoryAsync(Event("PROJ-1", "4"), 3, _ct);

        Assert.Equal(["Summary: Login broken\n\nUsers cannot log in.", "two", "three"], history.Select(m => m.Text).ToArray());
    }

    [Fact]
    public async Task GetHistoryAsync_HistoryCommentsOptionCapsTheLimit()
    {
        StubIssue(
            Fx.Comment("1", "bob", "one", Fx.T1000),
            Fx.Comment("2", "bob", "two", Fx.T1005),
            Fx.Comment("3", "bob", "three", Fx.T1010));

        var history = await CreateProvider(o => o.HistoryComments = 2).GetHistoryAsync(Event("PROJ-1", null), 40, _ct);

        Assert.Equal(["Summary: Login broken\n\nUsers cannot log in.", "three"], history.Select(m => m.Text).ToArray());
    }

    [Fact]
    public async Task GetHistoryAsync_RequestsOnlyTheFieldsItNeeds()
    {
        StubIssue();

        await CreateProvider().GetHistoryAsync(Event("PROJ-1", null), 10, _ct);

        await _client.Received(1).GetIssueAsync("PROJ-1", Arg.Is<IReadOnlyCollection<string>?>(f => f != null && f.Contains("comment") && f.Contains("description")), _ct);
    }

    [Fact]
    public async Task GetHistoryAsync_IssueMissingOrJiraDown_ReturnsEmpty()
    {
        _client.GetIssueAsync("PROJ-1", Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<CancellationToken>()).Returns((JiraIssue?)null);
        _client.GetIssueAsync("PROJ-2", Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<CancellationToken>())
            .Returns<JiraIssue?>(_ => throw new JiraApiException(System.Net.HttpStatusCode.ServiceUnavailable, "down"));
        var provider = CreateProvider();

        Assert.Empty(await provider.GetHistoryAsync(Event("PROJ-1", null), 10, _ct));
        Assert.Empty(await provider.GetHistoryAsync(Event("PROJ-2", null), 10, _ct));
        Assert.Empty(await provider.GetHistoryAsync(Event("PROJ-1", null), 0, _ct));
        Assert.Equal(Channel.Jira, provider.Channel);
    }
}
