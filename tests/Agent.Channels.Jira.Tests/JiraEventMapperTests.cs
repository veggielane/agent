using Agent.Channels.Jira;
using Agent.Core.Channels;
using Agent.Core.Events;
using Agent.Core.Tasks;

namespace Agent.Channels.Jira.Tests;

public sealed class JiraEventMapperTests
{
    private static readonly DateTimeOffset Cutoff = Fx.At(Fx.T1010);
    private readonly JiraEventMapper _mapper = new(TestOptions.Plain());

    [Fact]
    public void Map_MentionWithoutTask_IsMessageWithStrippedText()
    {
        var issue = Fx.Parse(Fx.Issue("PROJ-1", "Login broken", comments:
        [
            Fx.Comment("501", "bob", "[~agent-bot] why does   login fail?", Fx.T1017),
        ]));

        var evt = Assert.Single(_mapper.Map(issue, Cutoff, activeTask: null, repoUrl: null));

        Assert.Equal(Channel.Jira, evt.Channel);
        Assert.Equal(InboundKind.Message, evt.Kind);
        Assert.Equal("comment:501", evt.EventId);
        Assert.Equal("why does login fail?", evt.Text);
        Assert.Equal("PROJ-1", evt.ConversationId);
        Assert.Equal(Fx.At(Fx.T1017), evt.Timestamp);
        Assert.Null(evt.Task);
        Assert.False(evt.IsPrivate);
        Assert.Equal(Channel.Jira, evt.Caller.Channel);
        Assert.Equal("bob", evt.Caller.ChannelUserId);
        Assert.Equal("bob", evt.Caller.Username);
        Assert.Equal("bob@corp.local", evt.Caller.Email);
        Assert.Equal("PROJ-1", evt.Meta("issue"));
        Assert.Equal("501", evt.Meta("comment_id"));
        Assert.Equal("PROJ", evt.Meta("project"));
    }

    [Fact]
    public void Map_MentionWithActiveTask_IsFollowUpPointingAtTask()
    {
        var issue = Fx.Parse(Fx.Issue("PROJ-1", "Login broken", comments:
        [
            Fx.Comment("502", "bob", "[~agent-bot] also add tests", Fx.T1017),
        ]));
        var task = new AgentTask { Id = 42, Source = TaskSource.JiraIssue, SourceRef = "PROJ-1", RepoUrl = "https://gitlab.corp.local/t/r", ProjectId = "t/r", WorkBranch = "agent/PROJ-1-login", MergeRequestIid = "5", MergeRequestUrl = "https://gitlab.corp.local/t/r/-/merge_requests/5", Status = AgentTaskStatus.AwaitingReview };

        var evt = Assert.Single(_mapper.Map(issue, Cutoff, task, repoUrl: null));

        Assert.Equal(InboundKind.FollowUp, evt.Kind);
        Assert.Equal("also add tests", evt.Text);
        Assert.NotNull(evt.Task);
        Assert.Equal(42, evt.Task.ExistingTaskId);
        Assert.Equal(TaskSource.JiraIssue, evt.Task.Source);
        Assert.Equal("PROJ-1", evt.Task.SourceRef);
        Assert.Equal("https://jira.test/browse/PROJ-1", evt.Task.SourceUrl);
        Assert.Equal("agent/PROJ-1-login", evt.Task.ExistingBranch);
        Assert.Equal("5", evt.Task.MergeRequestIid);
        Assert.Equal("https://gitlab.corp.local/t/r", evt.Task.RepoUrl);
    }

    [Fact]
    public void Map_TaskLabelWithoutTask_IsTaskRequestFromReporter()
    {
        var issue = Fx.Parse(Fx.Issue("PROJ-2", "Add export", description: "Export as CSV.", labels: ["Agent"], reporter: "alice", assignee: "carol"));

        var evt = Assert.Single(_mapper.Map(issue, Cutoff, activeTask: null, repoUrl: "https://gitlab.corp.local/team/repo.git"));

        Assert.Equal(InboundKind.TaskRequest, evt.Kind);
        Assert.Equal("label:PROJ-2:agent", evt.EventId);
        Assert.Equal("Add export\n\nExport as CSV.", evt.Text);
        Assert.Equal("alice", evt.Caller.ChannelUserId);
        Assert.Equal("PROJ-2", evt.ConversationId);
        Assert.NotNull(evt.Task);
        Assert.Equal(TaskSource.JiraIssue, evt.Task.Source);
        Assert.Equal("PROJ-2", evt.Task.SourceRef);
        Assert.Equal("https://jira.test/browse/PROJ-2", evt.Task.SourceUrl);
        Assert.Equal("Add export", evt.Task.Title);
        Assert.Equal("https://gitlab.corp.local/team/repo.git", evt.Task.RepoUrl);
        Assert.Equal("team/repo", evt.Task.ProjectId);
        Assert.Equal("PROJ-2", evt.Meta("issue"));
        Assert.Null(evt.Meta("comment_id"));
    }

    [Fact]
    public void Map_TaskLabelWithoutReporter_FallsBackToAssignee()
    {
        var issue = Fx.Parse(Fx.Issue("PROJ-2", "Add export", labels: ["agent"], reporter: null, assignee: "carol"));

        var evt = Assert.Single(_mapper.Map(issue, Cutoff, null, null));

        Assert.Equal("carol", evt.Caller.ChannelUserId);
        Assert.Equal("Add export", evt.Text);
        Assert.Null(evt.Task!.RepoUrl);
    }

    [Fact]
    public void Map_TaskLabelWithActiveTask_ProducesNoTaskRequest()
    {
        var issue = Fx.Parse(Fx.Issue("PROJ-2", "Add export", labels: ["agent"]));
        var task = new AgentTask { Id = 1, Source = TaskSource.JiraIssue, SourceRef = "PROJ-2", Status = AgentTaskStatus.Working };

        Assert.Empty(_mapper.Map(issue, Cutoff, task, null));
    }

    [Fact]
    public void Map_BotOwnComment_IsIgnored()
    {
        var issue = Fx.Parse(Fx.Issue("PROJ-1", "x", comments:
        [
            Fx.Comment("601", "agent-bot", "[~agent-bot] I am talking to myself", Fx.T1017),
            Fx.Comment("602", "Agent-Bot", "[~agent-bot] different casing", Fx.T1018),
        ]));

        Assert.Empty(_mapper.Map(issue, Cutoff, null, null));
        Assert.False(_mapper.IsRelevant(issue, Cutoff));
    }

    [Fact]
    public void Map_CommentBeforeCutoff_IsIgnored()
    {
        var issue = Fx.Parse(Fx.Issue("PROJ-1", "x", comments:
        [
            Fx.Comment("701", "bob", "[~agent-bot] old", Fx.T1005),
            Fx.Comment("702", "bob", "[~agent-bot] exactly at cutoff", Fx.T1010),
            Fx.Comment("703", "bob", "[~agent-bot] new", Fx.T1017),
        ]));

        var evt = Assert.Single(_mapper.Map(issue, Cutoff, null, null));

        Assert.Equal("comment:703", evt.EventId);
    }

    [Fact]
    public void Map_CommentWithoutMention_IsIgnored()
    {
        var issue = Fx.Parse(Fx.Issue("PROJ-1", "x", comments:
        [
            Fx.Comment("801", "bob", "no mention here [~someone-else]", Fx.T1017),
        ]));

        Assert.Empty(_mapper.Map(issue, Cutoff, null, null));
        Assert.False(_mapper.IsRelevant(issue, Cutoff));
    }

    [Fact]
    public void Map_MentionIsCaseInsensitiveAndOrderedByCreated()
    {
        var issue = Fx.Parse(Fx.Issue("PROJ-1", "x", comments:
        [
            Fx.Comment("902", "bob", "second [~AGENT-BOT]", Fx.T1018),
            Fx.Comment("901", "bob", "[~Agent-Bot] first", Fx.T1017),
        ]));

        var events = _mapper.Map(issue, Cutoff, null, null);

        Assert.Equal(["comment:901", "comment:902"], events.Select(e => e.EventId).ToArray());
        Assert.Equal(["first", "second"], events.Select(e => e.Text).ToArray());
    }

    [Fact]
    public void Map_LabelAndMention_ProducesTaskRequestThenMessage()
    {
        var issue = Fx.Parse(Fx.Issue("PROJ-3", "Do it", labels: ["agent"], comments:
        [
            Fx.Comment("1001", "bob", "[~agent-bot] and hurry", Fx.T1017),
        ]));

        var events = _mapper.Map(issue, Cutoff, null, null);

        Assert.Equal([InboundKind.TaskRequest, InboundKind.Message], events.Select(e => e.Kind).ToArray());
    }

    [Fact]
    public void Map_BotUsernameDefaultsToUsername()
    {
        var mapper = new JiraEventMapper(TestOptions.Plain(o =>
        {
            o.Username = "svc-agent";
            o.BotUsername = null;
        }));
        var issue = Fx.Parse(Fx.Issue("PROJ-1", "x", comments: [Fx.Comment("1", "bob", "[~svc-agent] hi", Fx.T1017)]));

        Assert.Equal("hi", Assert.Single(mapper.Map(issue, Cutoff, null, null)).Text);
    }

    [Theory]
    [InlineData("[~agent-bot] hello", "hello")]
    [InlineData("hello [~agent-bot]", "hello")]
    [InlineData("hello [~agent-bot], how are you?", "hello , how are you?")]
    [InlineData("line one [~agent-bot]\nline two", "line one\nline two")]
    [InlineData("[~agent-bot]", "")]
    public void StripMention_RemovesMentionAndTidiesWhitespace(string body, string expected)
    {
        Assert.Equal(expected, _mapper.StripMention(body));
    }

    [Fact]
    public void ToCaller_UsesNameThenKey()
    {
        var byName = JiraEventMapper.ToCaller(new JiraUser { Name = "bob", Key = "JIRAUSER1", EmailAddress = "" });
        var byKey = JiraEventMapper.ToCaller(new JiraUser { Key = "JIRAUSER2" });

        Assert.Equal("bob", byName!.ChannelUserId);
        Assert.Null(byName.Email);
        Assert.Equal("JIRAUSER2", byKey!.ChannelUserId);
        Assert.Null(JiraEventMapper.ToCaller(null));
        Assert.Null(JiraEventMapper.ToCaller(new JiraUser()));
    }
}
