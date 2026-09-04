using Agent.Channels.GitLab;
using Agent.Core.Tasks;
using NSubstitute;
using Xunit;

namespace Agent.Channels.GitLab.Tests;

public sealed class GitLabMergeRequestPublisherTests
{
    private readonly IGitLabClient _client = Substitute.For<IGitLabClient>();
    private readonly GitLabOptions _options = TestOptions.Default();

    private GitLabMergeRequestPublisher CreatePublisher() => new(_client, TestOptions.Monitor(_options), Loggers.For<GitLabMergeRequestPublisher>());

    private static GitLabMergeRequest Mr(long iid, string title = "Add feature", string state = "opened", string source = "agent/12-fix", string target = "main") => new()
    {
        Iid = iid,
        Title = title,
        State = state,
        SourceBranch = source,
        TargetBranch = target,
        WebUrl = $"https://gitlab.test/team/repo/-/merge_requests/{iid}",
    };

    [Fact]
    public async Task EnsureMergeRequestAsync_NoOpenMr_CreatesWithReviewerAndDefaultLabels()
    {
        _client.ListMergeRequestsAsync("42", "agent/12-fix", "opened", Arg.Any<CancellationToken>()).Returns(Array.Empty<GitLabMergeRequest>());
        _client.GetUserByUsernameAsync("alice", Arg.Any<CancellationToken>()).Returns(new GitLabUser { Id = 5, Username = "alice" });
        CreateMergeRequestRequest? sent = null;
        _client.CreateMergeRequestAsync("42", Arg.Do<CreateMergeRequestRequest>(r => sent = r), Arg.Any<CancellationToken>()).Returns(Mr(8));

        var info = await CreatePublisher().EnsureMergeRequestAsync(new MergeRequestSpec("42", "agent/12-fix", "main", "Add feature", "Closes #12", false, "alice"), TestContext.Current.CancellationToken);

        Assert.Equal(new MergeRequestInfo("8", "https://gitlab.test/team/repo/-/merge_requests/8", "opened", "agent/12-fix"), info);
        Assert.NotNull(sent);
        Assert.Equal("agent/12-fix", sent.SourceBranch);
        Assert.Equal("main", sent.TargetBranch);
        Assert.Equal("Add feature", sent.Title);
        Assert.Equal("Closes #12", sent.Description);
        Assert.Equal([5L], sent.ReviewerIds!);
        Assert.Equal(["agent"], sent.Labels!);
        Assert.True(sent.RemoveSourceBranch);
        await _client.DidNotReceive().UpdateMergeRequestAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<UpdateMergeRequestRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureMergeRequestAsync_Draft_PrefixesTitle_AndUsesSpecLabels()
    {
        _client.ListMergeRequestsAsync("42", "agent/12-fix", "opened", Arg.Any<CancellationToken>()).Returns(Array.Empty<GitLabMergeRequest>());
        CreateMergeRequestRequest? sent = null;
        _client.CreateMergeRequestAsync("42", Arg.Do<CreateMergeRequestRequest>(r => sent = r), Arg.Any<CancellationToken>()).Returns(Mr(8, "Draft: Add feature"));

        await CreatePublisher().EnsureMergeRequestAsync(new MergeRequestSpec("42", "agent/12-fix", "main", "Add feature", "wip", true, null, ["custom"]), TestContext.Current.CancellationToken);

        Assert.NotNull(sent);
        Assert.Equal("Draft: Add feature", sent.Title);
        Assert.Equal(["custom"], sent.Labels!);
        Assert.Null(sent.ReviewerIds);
        await _client.DidNotReceive().GetUserByUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureMergeRequestAsync_UnknownReviewer_CreatesWithoutReviewer()
    {
        _client.ListMergeRequestsAsync("42", "agent/12-fix", "opened", Arg.Any<CancellationToken>()).Returns(Array.Empty<GitLabMergeRequest>());
        _client.GetUserByUsernameAsync("ghost", Arg.Any<CancellationToken>()).Returns((GitLabUser?)null);
        CreateMergeRequestRequest? sent = null;
        _client.CreateMergeRequestAsync("42", Arg.Do<CreateMergeRequestRequest>(r => sent = r), Arg.Any<CancellationToken>()).Returns(Mr(8));

        await CreatePublisher().EnsureMergeRequestAsync(new MergeRequestSpec("42", "agent/12-fix", "main", "Add feature", "d", false, "ghost"), TestContext.Current.CancellationToken);

        Assert.Null(sent!.ReviewerIds);
    }

    [Fact]
    public async Task EnsureMergeRequestAsync_OpenMrExists_UpdatesItInstead()
    {
        _client.ListMergeRequestsAsync("42", "agent/12-fix", "opened", Arg.Any<CancellationToken>()).Returns(new[] { Mr(8, "Old title") });
        UpdateMergeRequestRequest? sent = null;
        _client.UpdateMergeRequestAsync("42", 8, Arg.Do<UpdateMergeRequestRequest>(r => sent = r), Arg.Any<CancellationToken>()).Returns(Mr(8, "New title"));

        var info = await CreatePublisher().EnsureMergeRequestAsync(new MergeRequestSpec("42", "agent/12-fix", "main", "New title", "Updated body", false, "alice"), TestContext.Current.CancellationToken);

        Assert.Equal("8", info.Iid);
        Assert.NotNull(sent);
        Assert.Equal("New title", sent.Title);
        Assert.Equal("Updated body", sent.Description);
        Assert.Equal(["agent"], sent.Labels!);
        await _client.DidNotReceive().CreateMergeRequestAsync(Arg.Any<string>(), Arg.Any<CreateMergeRequestRequest>(), Arg.Any<CancellationToken>());
        await _client.DidNotReceive().GetUserByUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureMergeRequestAsync_UsesConfiguredMrLabels()
    {
        _options.MrLabels = ["bot", "auto"];
        _client.ListMergeRequestsAsync("42", "agent/12-fix", "opened", Arg.Any<CancellationToken>()).Returns(Array.Empty<GitLabMergeRequest>());
        CreateMergeRequestRequest? sent = null;
        _client.CreateMergeRequestAsync("42", Arg.Do<CreateMergeRequestRequest>(r => sent = r), Arg.Any<CancellationToken>()).Returns(Mr(8));

        await CreatePublisher().EnsureMergeRequestAsync(new MergeRequestSpec("42", "agent/12-fix", "main", "T", "D", false, null), TestContext.Current.CancellationToken);

        Assert.Equal(["bot", "auto"], sent!.Labels!);
    }

    [Fact]
    public async Task GetMergeRequestAsync_Found_MapsInfo()
    {
        _client.GetMergeRequestAsync("42", 8, Arg.Any<CancellationToken>()).Returns(Mr(8, state: "merged"));

        var info = await CreatePublisher().GetMergeRequestAsync("42", "8", TestContext.Current.CancellationToken);

        Assert.Equal(new MergeRequestInfo("8", "https://gitlab.test/team/repo/-/merge_requests/8", "merged", "agent/12-fix"), info);
    }

    [Fact]
    public async Task GetMergeRequestAsync_NotFound_ReturnsNull()
    {
        _client.GetMergeRequestAsync("42", 8, Arg.Any<CancellationToken>()).Returns((GitLabMergeRequest?)null);

        Assert.Null(await CreatePublisher().GetMergeRequestAsync("42", "8", TestContext.Current.CancellationToken));
        Assert.Null(await CreatePublisher().GetMergeRequestAsync("42", "not-a-number", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CommentAsync_PostsMergeRequestNote()
    {
        _client.CreateMergeRequestNoteAsync("42", 8, "Done", Arg.Any<CancellationToken>()).Returns(new GitLabNote { Id = 1 });

        await CreatePublisher().CommentAsync("42", "8", "Done", TestContext.Current.CancellationToken);

        await _client.Received(1).CreateMergeRequestNoteAsync("42", 8, "Done", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Add feature", false, "Add feature")]
    [InlineData("Add feature", true, "Draft: Add feature")]
    [InlineData("Draft: Add feature", true, "Draft: Add feature")]
    public void ApplyDraft_PrefixesOnce(string title, bool draft, string expected)
        => Assert.Equal(expected, GitLabMergeRequestPublisher.ApplyDraft(title, draft));
}
