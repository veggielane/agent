using Agent.Channels.GitLab;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Events;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace Agent.Channels.GitLab.Tests;

public sealed class GitLabConversationContextProviderTests
{
    private static readonly GitLabUserRef Alice = new() { Id = 5, Username = "alice" };
    private static readonly GitLabUserRef Bot = new() { Id = 7, Username = "agent-bot" };
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);

    private readonly IGitLabClient _client = Substitute.For<IGitLabClient>();
    private readonly GitLabOptions _options = TestOptions.Default();

    private GitLabConversationContextProvider CreateProvider() => new(_client, TestOptions.Monitor(_options), Loggers.For<GitLabConversationContextProvider>());

    private static InboundEvent Event(string targetType, string conversationId, string? noteId = "4") => new()
    {
        Channel = Channel.GitLab,
        EventId = "todo:1",
        Caller = new CallerIdentity(Channel.GitLab, "5", "alice"),
        ConversationId = conversationId,
        Text = "hi",
        Metadata = new Dictionary<string, string>
        {
            ["project_id"] = "42",
            ["project_path"] = "team/repo",
            ["iid"] = "12",
            ["target_type"] = targetType,
            ["note_id"] = noteId ?? string.Empty,
        },
    };

    private static GitLabNote Note(long id, string body, GitLabUserRef author, int minute, bool system = false)
        => new() { Id = id, Body = body, Author = author, CreatedAt = Start.AddMinutes(minute), System = system };

    [Fact]
    public void Channel_IsGitLab() => Assert.Equal(Channel.GitLab, CreateProvider().Channel);

    [Fact]
    public async Task GetHistoryAsync_Issue_DescriptionFirst_ThenHumanNotes_BotAsAssistant_TriggerExcluded()
    {
        _client.GetIssueAsync("42", 12, Arg.Any<CancellationToken>()).Returns(new GitLabIssue { Iid = 12, Title = "Fix login", Description = "Users cannot log in", Author = Alice });
        _client.GetIssueNotesAsync("42", 12, Arg.Any<CancellationToken>()).Returns(new[]
        {
            Note(1, "assigned to @agent-bot", Alice, 1, system: true),
            Note(2, "@agent-bot any idea?", Alice, 2),
            Note(3, "Check the token expiry.", Bot, 3),
            Note(4, "@agent-bot and now?", Alice, 4),
        });

        var history = await CreateProvider().GetHistoryAsync(Event("Issue", "team/repo#12"), 40, TestContext.Current.CancellationToken);

        Assert.Equal(3, history.Count);
        Assert.Equal(ChatRole.User, history[0].Role);
        Assert.Equal("alice", history[0].AuthorName);
        Assert.Equal("Fix login\n\nUsers cannot log in", history[0].Text);
        Assert.Equal(ChatRole.User, history[1].Role);
        Assert.Equal("@agent-bot any idea?", history[1].Text);
        Assert.Equal(ChatRole.Assistant, history[2].Role);
        Assert.Equal("Check the token expiry.", history[2].Text);
        Assert.Null(history[2].AuthorName);
    }

    [Fact]
    public async Task GetHistoryAsync_MergeRequest_UsesMergeRequestEndpoints()
    {
        _client.GetMergeRequestAsync("42", 12, Arg.Any<CancellationToken>()).Returns(new GitLabMergeRequest { Iid = 12, Title = "Add feature", Description = "Closes #3", Author = Bot });
        _client.GetMergeRequestNotesAsync("42", 12, Arg.Any<CancellationToken>()).Returns(new[] { Note(9, "Please add tests", Alice, 1) });

        var history = await CreateProvider().GetHistoryAsync(Event("MergeRequest", "team/repo!12", noteId: null), 40, TestContext.Current.CancellationToken);

        Assert.Equal(2, history.Count);
        Assert.Equal(ChatRole.Assistant, history[0].Role);
        Assert.Equal("Add feature\n\nCloses #3", history[0].Text);
        Assert.Equal(ChatRole.User, history[1].Role);
        Assert.Equal("Please add tests", history[1].Text);
        await _client.DidNotReceive().GetIssueAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetHistoryAsync_LimitKeepsDescriptionAndLatestNotes()
    {
        _client.GetIssueAsync("42", 12, Arg.Any<CancellationToken>()).Returns(new GitLabIssue { Iid = 12, Title = "T", Description = "D", Author = Alice });
        _client.GetIssueNotesAsync("42", 12, Arg.Any<CancellationToken>()).Returns(Enumerable.Range(1, 10).Select(i => Note(100 + i, $"n{i}", Alice, i)).ToArray());

        var history = await CreateProvider().GetHistoryAsync(Event("Issue", "team/repo#12", noteId: null), 4, TestContext.Current.CancellationToken);

        Assert.Equal(["T\n\nD", "n8", "n9", "n10"], history.Select(m => m.Text));
    }

    [Fact]
    public async Task GetHistoryAsync_HistoryNotesOptionCapsTheLimit()
    {
        _options.HistoryNotes = 2;
        _client.GetIssueAsync("42", 12, Arg.Any<CancellationToken>()).Returns(new GitLabIssue { Iid = 12, Title = "T", Author = Alice });
        _client.GetIssueNotesAsync("42", 12, Arg.Any<CancellationToken>()).Returns(new[] { Note(1, "a", Alice, 1), Note(2, "b", Alice, 2), Note(3, "c", Alice, 3) });

        var history = await CreateProvider().GetHistoryAsync(Event("Issue", "team/repo#12", noteId: null), 40, TestContext.Current.CancellationToken);

        Assert.Equal(["T", "c"], history.Select(m => m.Text));
    }

    [Fact]
    public async Task GetHistoryAsync_WithoutBotUsername_ResolvesItFromCurrentUserOnce()
    {
        _options.BotUsername = "";
        _client.GetCurrentUserAsync(Arg.Any<CancellationToken>()).Returns(new GitLabUser { Id = 7, Username = "agent-bot" });
        _client.GetIssueAsync("42", 12, Arg.Any<CancellationToken>()).Returns(new GitLabIssue { Iid = 12, Title = "T", Author = Alice });
        _client.GetIssueNotesAsync("42", 12, Arg.Any<CancellationToken>()).Returns(new[] { Note(1, "reply", Bot, 1) });
        var provider = CreateProvider();

        var history = await provider.GetHistoryAsync(Event("Issue", "team/repo#12", noteId: null), 40, TestContext.Current.CancellationToken);
        await provider.GetHistoryAsync(Event("Issue", "team/repo#12", noteId: null), 40, TestContext.Current.CancellationToken);

        Assert.Equal(ChatRole.Assistant, history[1].Role);
        await _client.Received(1).GetCurrentUserAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetHistoryAsync_UnresolvableTarget_ReturnsEmpty()
    {
        var evt = new InboundEvent { Channel = Channel.GitLab, EventId = "x", Caller = new CallerIdentity(Channel.GitLab, "5"), ConversationId = "PROJ-1", Text = "hi" };

        Assert.Empty(await CreateProvider().GetHistoryAsync(evt, 40, TestContext.Current.CancellationToken));
        Assert.Empty(await CreateProvider().GetHistoryAsync(Event("Issue", "team/repo#12"), 0, TestContext.Current.CancellationToken));
    }
}
