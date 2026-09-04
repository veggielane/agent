using Agent.Channels.GitLab;
using Agent.Core.Channels;
using Agent.Core.Tasks;
using NSubstitute;
using Xunit;

namespace Agent.Channels.GitLab.Tests;

public sealed class GitLabTaskNotifierTests
{
    private readonly IGitLabClient _client = Substitute.For<IGitLabClient>();

    private GitLabTaskNotifier CreateNotifier() => new(_client, Loggers.For<GitLabTaskNotifier>());

    [Fact]
    public void Channel_IsGitLab() => Assert.Equal(Channel.GitLab, CreateNotifier().Channel);

    [Fact]
    public async Task NotifyAsync_IssueTaskWithMetadata_CommentsOnIssueByNumericProjectId()
    {
        var task = new AgentTask
        {
            Id = 9,
            Source = TaskSource.GitLabIssue,
            SourceRef = "team/repo#12",
            ConversationId = "team/repo#12",
            MergeRequestIid = "7",
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["project_id"] = "42", ["iid"] = "12", ["target_type"] = "Issue" },
        };

        await CreateNotifier().NotifyAsync(task, "Opened !7", TestContext.Current.CancellationToken);

        await _client.Received(1).CreateIssueNoteAsync("42", 12, "Opened !7", Arg.Any<CancellationToken>());
        await _client.DidNotReceive().CreateMergeRequestNoteAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyAsync_IssueTaskWithoutMetadata_ParsesSourceRef()
    {
        var task = new AgentTask { Id = 9, Source = TaskSource.GitLabIssue, SourceRef = "team/repo#12", ConversationId = "team/repo#12" };

        await CreateNotifier().NotifyAsync(task, "Working on it", TestContext.Current.CancellationToken);

        await _client.Received(1).CreateIssueNoteAsync("team/repo", 12, "Working on it", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyAsync_MergeRequestTask_CommentsOnMergeRequest()
    {
        var task = new AgentTask { Id = 9, Source = TaskSource.MergeRequest, SourceRef = "team/repo!7", ConversationId = "team/repo!7" };

        await CreateNotifier().NotifyAsync(task, "Pushed a fix", TestContext.Current.CancellationToken);

        await _client.Received(1).CreateMergeRequestNoteAsync("team/repo", 7, "Pushed a fix", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyAsync_UnparseableTask_DoesNothing()
    {
        var task = new AgentTask { Id = 9, Source = TaskSource.Cli, SourceRef = "cli-abc", ConversationId = "cli-abc" };

        await CreateNotifier().NotifyAsync(task, "hello", TestContext.Current.CancellationToken);

        await _client.DidNotReceive().CreateIssueNoteAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _client.DidNotReceive().CreateMergeRequestNoteAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
