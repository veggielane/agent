using Agent.Channels.GitLab;
using Agent.Core.Channels;
using Agent.Core.Events;
using Agent.Core.Tasks;
using Xunit;

namespace Agent.Channels.GitLab.Tests;

public sealed class GitLabEventMapperTests
{
    private static readonly GitLabProjectRef ProjectRef = new() { Id = 42, PathWithNamespace = "team/repo", Name = "repo", Path = "repo" };
    private static readonly GitLabProject Project = new() { Id = 42, PathWithNamespace = "team/repo", DefaultBranch = "main", HttpUrlToRepo = "https://gitlab.test/team/repo.git", WebUrl = "https://gitlab.test/team/repo" };
    private static readonly GitLabUserRef Alice = new() { Id = 5, Username = "alice", Name = "Alice" };

    private static GitLabTodo IssueMention(string body = "@agent-bot why does login fail?", string? targetUrl = "https://gitlab.test/team/repo/-/issues/12#note_555", string action = "mentioned") => new()
    {
        Id = 101,
        Project = ProjectRef,
        Author = Alice,
        ActionName = action,
        TargetType = "Issue",
        Target = new GitLabTodoTarget { Id = 1, Iid = 12, ProjectId = 42, Title = "Fix login", Description = "Users cannot log in", WebUrl = "https://gitlab.test/team/repo/-/issues/12", Author = Alice },
        TargetUrl = targetUrl,
        Body = body,
        CreatedAt = new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero),
    };

    private static GitLabTodo MergeRequestMention(string body = "@agent-bot please add tests") => new()
    {
        Id = 202,
        Project = ProjectRef,
        Author = Alice,
        ActionName = "directly_addressed",
        TargetType = "MergeRequest",
        Target = new GitLabTodoTarget { Id = 2, Iid = 7, ProjectId = 42, Title = "Add feature", SourceBranch = "agent/12-fix", TargetBranch = "main", WebUrl = "https://gitlab.test/team/repo/-/merge_requests/7", Author = new GitLabUserRef { Id = 7, Username = "agent-bot" } },
        TargetUrl = "https://gitlab.test/team/repo/-/merge_requests/7#note_777",
        Body = body,
        CreatedAt = new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero),
    };

    private static GitLabTodo Assignment() => new()
    {
        Id = 303,
        Project = ProjectRef,
        Author = Alice,
        ActionName = "assigned",
        TargetType = "Issue",
        Target = new GitLabTodoTarget { Id = 1, Iid = 12, ProjectId = 42, Title = "Fix login", Description = "Users cannot log in", WebUrl = "https://gitlab.test/team/repo/-/issues/12", Author = Alice },
        TargetUrl = "https://gitlab.test/team/repo/-/issues/12",
        Body = "Fix login",
        CreatedAt = new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero),
    };

    private static GitLabEventMapper Mapper(Action<GitLabOptions>? configure = null)
    {
        var options = TestOptions.Default();
        configure?.Invoke(options);
        return new GitLabEventMapper(options);
    }

    [Fact]
    public void Classify_MentionOnIssue_IsMention()
        => Assert.Equal(TodoDisposition.Mention, Mapper().Classify(IssueMention()));

    [Fact]
    public void Classify_DirectlyAddressedOnMergeRequest_IsMention()
        => Assert.Equal(TodoDisposition.Mention, Mapper().Classify(MergeRequestMention()));

    [Fact]
    public void Classify_AssignedIssue_IsAssignment()
        => Assert.Equal(TodoDisposition.Assignment, Mapper().Classify(Assignment()));

    [Fact]
    public void Classify_AssignedIssueWithTaskOnAssignOff_IsIgnored()
        => Assert.Equal(TodoDisposition.Ignore, Mapper(o => o.TaskOnAssign = false).Classify(Assignment()));

    [Fact]
    public void Classify_AssignedMergeRequest_IsIgnored()
        => Assert.Equal(TodoDisposition.Ignore, Mapper().Classify(MergeRequestMention() with { ActionName = "assigned" }));

    [Fact]
    public void Classify_OtherActions_AreIgnored()
    {
        Assert.Equal(TodoDisposition.Ignore, Mapper().Classify(IssueMention(action: "marked")));
        Assert.Equal(TodoDisposition.Ignore, Mapper().Classify(IssueMention(action: "build_failed")));
        Assert.Equal(TodoDisposition.Ignore, Mapper().Classify(IssueMention(action: "review_requested")));
    }

    [Fact]
    public void Classify_BotOwnActivity_IsIgnored()
        => Assert.Equal(TodoDisposition.Ignore, Mapper().Classify(IssueMention() with { Author = new GitLabUserRef { Id = 7, Username = "Agent-Bot" } }));

    [Fact]
    public void Classify_UnsupportedTarget_IsIgnored()
        => Assert.Equal(TodoDisposition.Ignore, Mapper().Classify(IssueMention() with { TargetType = "DesignManagement::Design" }));

    [Fact]
    public void MapMention_OnIssueWithoutTask_IsMessageWithMetadata()
    {
        var evt = Mapper().MapMention(new MentionInput(IssueMention(), null, Project, null, null, null, null));

        Assert.Equal(Channel.GitLab, evt.Channel);
        Assert.Equal(InboundKind.Message, evt.Kind);
        Assert.Null(evt.Task);
        Assert.Equal("todo:101", evt.EventId);
        Assert.Equal("team/repo#12", evt.ConversationId);
        Assert.Equal("why does login fail?", evt.Text);
        Assert.False(evt.IsPrivate);
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero), evt.Timestamp);
        Assert.Equal("42", evt.Meta("project_id"));
        Assert.Equal("team/repo", evt.Meta("project_path"));
        Assert.Equal("12", evt.Meta("iid"));
        Assert.Equal("Issue", evt.Meta("target_type"));
        Assert.Equal("555", evt.Meta("note_id"));
        Assert.Equal("101", evt.Meta("todo_id"));
        Assert.Null(evt.Meta("discussion_id"));
    }

    [Fact]
    public void MapMention_OnIssueWithActiveTask_IsFollowUpWithExistingTaskId()
    {
        var task = new AgentTask { Id = 9, Source = TaskSource.GitLabIssue, SourceRef = "team/repo#12", ProjectId = "42", BaseBranch = "main", WorkBranch = "agent/12-fix", MergeRequestIid = "7", MergeRequestUrl = "https://gitlab.test/team/repo/-/merge_requests/7", RepoUrl = "https://gitlab.test/team/repo.git" };

        var evt = Mapper().MapMention(new MentionInput(IssueMention(), null, Project, task, null, null, null));

        Assert.Equal(InboundKind.FollowUp, evt.Kind);
        Assert.NotNull(evt.Task);
        Assert.Equal(9, evt.Task.ExistingTaskId);
        Assert.Equal(TaskSource.GitLabIssue, evt.Task.Source);
        Assert.Equal("team/repo#12", evt.Task.SourceRef);
        Assert.Equal("agent/12-fix", evt.Task.ExistingBranch);
        Assert.Equal("7", evt.Task.MergeRequestIid);
        Assert.Equal("42", evt.Task.ProjectId);
    }

    [Fact]
    public void MapMention_OnAgentsMergeRequest_IsFollowUpWithBranchContext()
    {
        var task = new AgentTask { Id = 9, Source = TaskSource.GitLabIssue, SourceRef = "team/repo#12", ProjectId = "42", MergeRequestIid = "7", RepoUrl = "https://gitlab.test/team/repo.git" };

        var evt = Mapper().MapMention(new MentionInput(MergeRequestMention(), null, Project, task, null, null, null));

        Assert.Equal(InboundKind.FollowUp, evt.Kind);
        Assert.Equal("team/repo!7", evt.ConversationId);
        Assert.Equal("please add tests", evt.Text);
        var ctx = Assert.IsType<TaskContext>(evt.Task);
        Assert.Equal(TaskSource.MergeRequest, ctx.Source);
        Assert.Equal("team/repo!7", ctx.SourceRef);
        Assert.Equal(9, ctx.ExistingTaskId);
        Assert.Equal("agent/12-fix", ctx.ExistingBranch);
        Assert.Equal("main", ctx.BaseBranch);
        Assert.Equal("42", ctx.ProjectId);
        Assert.Equal("7", ctx.MergeRequestIid);
        Assert.Equal("https://gitlab.test/team/repo/-/merge_requests/7", ctx.MergeRequestUrl);
        Assert.Equal("https://gitlab.test/team/repo.git", ctx.RepoUrl);
        Assert.Equal("MergeRequest", evt.Meta("target_type"));
        Assert.Equal("777", evt.Meta("note_id"));
    }

    [Fact]
    public void MapMention_OnForeignMergeRequestWithFlagOff_IsMessage()
    {
        var evt = Mapper(o => o.FollowUpsOnForeignMrs = false).MapMention(new MentionInput(MergeRequestMention(), null, Project, null, null, null, null));

        Assert.Equal(InboundKind.Message, evt.Kind);
        Assert.Null(evt.Task);
        Assert.Equal("team/repo!7", evt.ConversationId);
    }

    [Fact]
    public void MapMention_OnForeignMergeRequestWithFlagOn_IsFollowUpWithoutExistingTaskId()
    {
        var evt = Mapper(o => o.FollowUpsOnForeignMrs = true).MapMention(new MentionInput(MergeRequestMention(), null, Project, null, null, null, null));

        Assert.Equal(InboundKind.FollowUp, evt.Kind);
        var ctx = Assert.IsType<TaskContext>(evt.Task);
        Assert.Null(ctx.ExistingTaskId);
        Assert.Equal(TaskSource.MergeRequest, ctx.Source);
        Assert.Equal("agent/12-fix", ctx.ExistingBranch);
        Assert.Equal("main", ctx.BaseBranch);
        Assert.Equal("7", ctx.MergeRequestIid);
    }

    [Fact]
    public void MapMention_OnMergeRequestWithoutBranchesInTodo_FallsBackToFetchedMergeRequest()
    {
        var todo = MergeRequestMention() with { Target = MergeRequestMention().Target! with { SourceBranch = null, TargetBranch = null } };
        var mr = new GitLabMergeRequest { Iid = 7, SourceBranch = "feature/x", TargetBranch = "develop", WebUrl = "https://gitlab.test/team/repo/-/merge_requests/7" };

        var evt = Mapper(o => o.FollowUpsOnForeignMrs = true).MapMention(new MentionInput(todo, null, Project, null, mr, null, null));

        Assert.Equal("feature/x", evt.Task!.ExistingBranch);
        Assert.Equal("develop", evt.Task.BaseBranch);
    }

    [Fact]
    public void MapMention_WithDiffPosition_AppendsFileAndLine_AndDiscussionId()
    {
        var position = new GitLabNotePosition { NewPath = "src/Program.cs", NewLine = 17, OldPath = "src/Program.cs" };

        var evt = Mapper().MapMention(new MentionInput(MergeRequestMention(), null, Project, null, null, "abc123", position));

        Assert.EndsWith("\n\n(Comment on src/Program.cs line 17)", evt.Text, StringComparison.Ordinal);
        Assert.Equal("abc123", evt.Meta("discussion_id"));
    }

    [Fact]
    public void MapMention_StripsBotMentionAnywhere()
    {
        var evt = Mapper().MapMention(new MentionInput(IssueMention(body: "Hey @agent-bot, can you look? cc @agent-bot"), null, Project, null, null, null, null));

        Assert.Equal("Hey , can you look? cc", evt.Text);
    }

    [Fact]
    public void MapMention_DoesNotStripOtherUsersOrLongerNames()
    {
        var evt = Mapper().MapMention(new MentionInput(IssueMention(body: "@agent-bot ask @agent-bot-2 and @alice"), null, Project, null, null, null, null));

        Assert.Equal("ask @agent-bot-2 and @alice", evt.Text);
    }

    [Fact]
    public void MapMention_UsesAuthorDetailsForEmailAndLdapDn()
    {
        var details = new GitLabUser { Id = 5, Username = "alice", Email = "alice@example.test", Identities = [new GitLabIdentity { Provider = "ldapmain", ExternUid = "CN=alice,DC=corp" }] };

        var evt = Mapper().MapMention(new MentionInput(IssueMention(), details, Project, null, null, null, null));

        Assert.Equal(Channel.GitLab, evt.Caller.Channel);
        Assert.Equal("5", evt.Caller.ChannelUserId);
        Assert.Equal("alice", evt.Caller.Username);
        Assert.Equal("alice@example.test", evt.Caller.Email);
        Assert.Equal("CN=alice,DC=corp", evt.Caller.LdapDn);
    }

    [Fact]
    public void ToCaller_IgnoresNonLdapIdentities()
    {
        var details = new GitLabUser { Id = 5, Username = "alice", Identities = [new GitLabIdentity { Provider = "openid_connect", ExternUid = "sub-123" }] };

        var caller = GitLabEventMapper.ToCaller(Alice, details);

        Assert.Null(caller.LdapDn);
        Assert.Null(caller.Email);
    }

    [Fact]
    public void MapAssignment_IsTaskRequestWithRepoAndBranch()
    {
        var evt = Mapper().MapAssignment(new AssignmentInput(Assignment(), Project, null));

        Assert.Equal(InboundKind.TaskRequest, evt.Kind);
        Assert.Equal("todo:303", evt.EventId);
        Assert.Equal("team/repo#12", evt.ConversationId);
        Assert.Equal("Fix login\n\nUsers cannot log in", evt.Text);
        var ctx = Assert.IsType<TaskContext>(evt.Task);
        Assert.Equal(TaskSource.GitLabIssue, ctx.Source);
        Assert.Equal("team/repo#12", ctx.SourceRef);
        Assert.Equal("https://gitlab.test/team/repo/-/issues/12", ctx.SourceUrl);
        Assert.Equal("42", ctx.ProjectId);
        Assert.Equal("Fix login", ctx.Title);
        Assert.Equal("https://gitlab.test/team/repo.git", ctx.RepoUrl);
        Assert.Equal("main", ctx.BaseBranch);
        Assert.Equal("5", evt.Caller.ChannelUserId);
        Assert.Equal("Issue", evt.Meta("target_type"));
        Assert.Equal("303", evt.Meta("todo_id"));
    }

    [Fact]
    public void MapLabelledIssue_IsTaskRequestFromIssueAuthor()
    {
        var issue = new GitLabIssue { Id = 1, Iid = 13, ProjectId = 42, Title = "Add export", Description = "CSV please", Author = Alice, WebUrl = "https://gitlab.test/team/repo/-/issues/13", UpdatedAt = new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero) };

        var evt = Mapper().MapLabelledIssue(issue, Project, new GitLabUser { Id = 5, Username = "alice", Email = "alice@example.test" });

        Assert.Equal(InboundKind.TaskRequest, evt.Kind);
        Assert.Equal("label:42:13", evt.EventId);
        Assert.Equal("team/repo#13", evt.ConversationId);
        Assert.Equal("Add export\n\nCSV please", evt.Text);
        Assert.Equal("alice@example.test", evt.Caller.Email);
        Assert.Equal("main", evt.Task!.BaseBranch);
        Assert.Equal("https://gitlab.test/team/repo.git", evt.Task.RepoUrl);
        Assert.Equal(issue.UpdatedAt, evt.Timestamp);
        Assert.Null(evt.Meta("todo_id"));
    }

    [Theory]
    [InlineData("https://gitlab.test/team/repo/-/issues/12#note_555", 555L)]
    [InlineData("https://gitlab.test/team/repo/-/merge_requests/7#note_1234567", 1234567L)]
    [InlineData("https://gitlab.test/team/repo/-/issues/12", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseNoteId_ReadsAnchor(string? url, long? expected)
        => Assert.Equal(expected, GitLabEventMapper.ParseNoteId(url));

    [Theory]
    [InlineData("@agent-bot hello", "hello")]
    [InlineData("hello @Agent-Bot", "hello")]
    [InlineData("hello", "hello")]
    [InlineData("  spaced   out  @agent-bot  ", "spaced out")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void StripBotMention_RemovesMentionAndTrims(string? text, string expected)
        => Assert.Equal(expected, GitLabEventMapper.StripBotMention(text, "agent-bot"));

    [Fact]
    public void StripBotMention_WithEmptyBotName_OnlyTrims()
        => Assert.Equal("@agent-bot hello", GitLabEventMapper.StripBotMention("  @agent-bot hello ", ""));
}
