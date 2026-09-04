using Agent.Channels.Jira;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Events;
using Agent.Core.Replies;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Agent.Channels.Jira.Tests;

public sealed class JiraReplySenderTests
{
    private readonly IJiraClient _client = Substitute.For<IJiraClient>();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static InboundEvent Event(string? issueMeta, string conversationId = "PROJ-1") => new()
    {
        Channel = Channel.Jira,
        EventId = "comment:1",
        Caller = new CallerIdentity(Channel.Jira, "bob"),
        ConversationId = conversationId,
        Text = "hi",
        Metadata = issueMeta is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["issue"] = issueMeta },
    };

    [Fact]
    public async Task SendAsync_CommentsOnTheIssueFromMetadata()
    {
        var sender = new JiraReplySender(_client, NullLogger<JiraReplySender>.Instance);

        await sender.SendAsync(Event("PROJ-9", conversationId: "other"), "h1. Answer", _ct);

        await _client.Received(1).AddCommentAsync("PROJ-9", "h1. Answer", _ct);
        Assert.Equal(Channel.Jira, sender.Channel);
    }

    [Fact]
    public async Task SendAsync_FallsBackToConversationId()
    {
        await new JiraReplySender(_client, NullLogger<JiraReplySender>.Instance).SendAsync(Event(null, "PROJ-2"), "text", _ct);

        await _client.Received(1).AddCommentAsync("PROJ-2", "text", _ct);
    }

    [Fact]
    public async Task SendAsync_EmptyText_IsSkipped()
    {
        await new JiraReplySender(_client, NullLogger<JiraReplySender>.Instance).SendAsync(Event("PROJ-1"), "  ", _ct);

        await _client.DidNotReceiveWithAnyArgs().AddCommentAsync(default!, default!, default);
    }

    [Fact]
    public async Task AcknowledgeAsync_IsANoOp()
    {
        var sender = new JiraReplySender(_client, NullLogger<JiraReplySender>.Instance);

        await sender.AcknowledgeAsync(Event("PROJ-1"), AckState.Working, null, _ct);
        await sender.AcknowledgeAsync(Event("PROJ-1"), AckState.Failed, "note", _ct);

        await _client.DidNotReceiveWithAnyArgs().AddCommentAsync(default!, default!, default);
    }
}
