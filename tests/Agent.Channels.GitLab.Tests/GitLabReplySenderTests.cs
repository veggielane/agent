using Agent.Channels.GitLab;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Events;
using Agent.Core.Replies;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Agent.Channels.GitLab.Tests;

public sealed class GitLabReplySenderTests
{
    private readonly IGitLabClient _client = Substitute.For<IGitLabClient>();
    private readonly GitLabOptions _options = TestOptions.Default();

    private GitLabReplySender CreateSender() => new(_client, TestOptions.Monitor(_options), Loggers.For<GitLabReplySender>());

    private static InboundEvent Event(string conversationId, Dictionary<string, string>? metadata) => new()
    {
        Channel = Channel.GitLab,
        EventId = "todo:1",
        Caller = new CallerIdentity(Channel.GitLab, "5", "alice"),
        ConversationId = conversationId,
        Text = "hi",
        Metadata = metadata ?? new Dictionary<string, string>(),
    };

    private static Dictionary<string, string> Meta(string targetType, string? noteId = "555", string? discussionId = null)
    {
        var m = new Dictionary<string, string> { ["project_id"] = "42", ["project_path"] = "team/repo", ["iid"] = "12", ["target_type"] = targetType };
        if (noteId is not null)
        {
            m["note_id"] = noteId;
        }

        if (discussionId is not null)
        {
            m["discussion_id"] = discussionId;
        }

        return m;
    }

    [Fact]
    public void Channel_IsGitLab() => Assert.Equal(Channel.GitLab, CreateSender().Channel);

    [Fact]
    public async Task SendAsync_OnIssue_CreatesIssueNoteUsingNumericProjectId()
    {
        await CreateSender().SendAsync(Event("team/repo#12", Meta("Issue")), "answer", TestContext.Current.CancellationToken);

        await _client.Received(1).CreateIssueNoteAsync("42", 12, "answer", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_OnMergeRequestWithoutDiscussion_CreatesMergeRequestNote()
    {
        await CreateSender().SendAsync(Event("team/repo!12", Meta("MergeRequest")), "answer", TestContext.Current.CancellationToken);

        await _client.Received(1).CreateMergeRequestNoteAsync("42", 12, "answer", Arg.Any<CancellationToken>());
        await _client.DidNotReceive().CreateDiscussionReplyAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_OnMergeRequestDiscussion_RepliesInThread()
    {
        await CreateSender().SendAsync(Event("team/repo!12", Meta("MergeRequest", discussionId: "abc123")), "answer", TestContext.Current.CancellationToken);

        await _client.Received(1).CreateDiscussionReplyAsync("42", 12, "abc123", "answer", Arg.Any<CancellationToken>());
        await _client.DidNotReceive().CreateMergeRequestNoteAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WithoutMetadata_FallsBackToConversationId()
    {
        await CreateSender().SendAsync(Event("team/repo!7", null), "answer", TestContext.Current.CancellationToken);

        await _client.Received(1).CreateMergeRequestNoteAsync("team/repo", 7, "answer", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WithoutAnyTarget_Throws()
        => await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSender().SendAsync(Event("PROJ-1", null), "answer", TestContext.Current.CancellationToken));

    [Fact]
    public async Task SendAsync_EmptyText_DoesNothing()
    {
        await CreateSender().SendAsync(Event("team/repo#12", Meta("Issue")), "  ", TestContext.Current.CancellationToken);

        await _client.DidNotReceive().CreateIssueNoteAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcknowledgeAsync_WorkingWithNoteId_AwardsAckEmojiOnNote()
    {
        await CreateSender().AcknowledgeAsync(Event("team/repo#12", Meta("Issue")), AckState.Working, null, TestContext.Current.CancellationToken);

        await _client.Received(1).AwardEmojiOnNoteAsync("42", GitLabNoteableType.Issue, 12, 555, "eyes", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcknowledgeAsync_DoneWithoutNoteId_AwardsDoneEmojiOnMergeRequest()
    {
        await CreateSender().AcknowledgeAsync(Event("team/repo!12", Meta("MergeRequest", noteId: null)), AckState.Done, null, TestContext.Current.CancellationToken);

        await _client.Received(1).AwardEmojiAsync("42", GitLabNoteableType.MergeRequest, 12, "white_check_mark", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcknowledgeAsync_Failed_AwardsFailedEmoji()
    {
        _options.FailedEmoji = "boom";

        await CreateSender().AcknowledgeAsync(Event("team/repo#12", Meta("Issue")), AckState.Failed, "oops", TestContext.Current.CancellationToken);

        await _client.Received(1).AwardEmojiOnNoteAsync("42", GitLabNoteableType.Issue, 12, 555, "boom", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcknowledgeAsync_EmptyEmojiConfigured_DoesNothing()
    {
        _options.AckEmoji = "";

        await CreateSender().AcknowledgeAsync(Event("team/repo#12", Meta("Issue")), AckState.Working, null, TestContext.Current.CancellationToken);

        await _client.DidNotReceive().AwardEmojiOnNoteAsync(Arg.Any<string>(), Arg.Any<GitLabNoteableType>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcknowledgeAsync_ApiFailure_IsSwallowed()
    {
        _client.AwardEmojiOnNoteAsync(Arg.Any<string>(), Arg.Any<GitLabNoteableType>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new GitLabApiException(System.Net.HttpStatusCode.NotFound, "already awarded", null));

        await CreateSender().AcknowledgeAsync(Event("team/repo#12", Meta("Issue")), AckState.Working, null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AcknowledgeAsync_WithoutTarget_DoesNothing()
    {
        await CreateSender().AcknowledgeAsync(Event("PROJ-1", null), AckState.Working, null, TestContext.Current.CancellationToken);

        await _client.DidNotReceive().AwardEmojiAsync(Arg.Any<string>(), Arg.Any<GitLabNoteableType>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
