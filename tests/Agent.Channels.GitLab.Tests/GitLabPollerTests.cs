using Agent.Channels.GitLab;
using Agent.Core.Events;
using Agent.Core.Tasks;
using NSubstitute;
using Xunit;

namespace Agent.Channels.GitLab.Tests;

public sealed class GitLabPollerTests : IDisposable
{
    private static readonly object Alice = Payloads.UserRef(5, "alice");
    private static readonly object ProjectRef = Payloads.ProjectRef(42, "team/repo");

    private readonly GitLabTestServer _gitlab = new();
    private readonly InboundQueue _queue = new();
    private readonly InMemoryProcessedEventStore _processed = new();
    private readonly InMemoryCursorStore _cursors = new();
    private readonly ITaskStore _tasks = Substitute.For<ITaskStore>();
    private readonly ITaskService _taskService = Substitute.For<ITaskService>();
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero));
    private readonly GitLabOptions _options = TestOptions.Default();

    public GitLabPollerTests()
    {
        _tasks.ListAsync(Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<AgentTask>());
        _tasks.FindActiveBySourceAsync(Arg.Any<TaskSource>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((AgentTask?)null);
        _tasks.FindByMergeRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((AgentTask?)null);

        _gitlab.Get("/api/v4/todos", Array.Empty<object>());
        _gitlab.Get("/api/v4/users/5", Payloads.User(5, "alice", "alice@example.test", "CN=alice,DC=corp"));
        _gitlab.Get("/api/v4/projects/42", Payloads.Project(42, "team/repo"));
        _gitlab.Post("/api/v4/todos/101/mark_as_done", new { Id = 101, State = "done" }, 200);
        _gitlab.Post("/api/v4/todos/202/mark_as_done", new { Id = 202, State = "done" }, 200);
        _gitlab.Post("/api/v4/todos/303/mark_as_done", new { Id = 303, State = "done" }, 200);
        _gitlab.Post("/api/v4/todos/404/mark_as_done", new { Id = 404, State = "done" }, 200);
    }

    public void Dispose() => _gitlab.Dispose();

    private GitLabPoller CreatePoller() => new(_gitlab.Client, _queue, _processed, _cursors, _tasks, _taskService, TestOptions.Monitor(_options), Loggers.For<GitLabPoller>(), _time);

    private static object IssueMentionTodo(long id = 101, string body = "@agent-bot why does login fail?") => Payloads.Todo(
        id, "mentioned", "Issue", ProjectRef, Alice,
        Payloads.IssueTarget(12, 42, "team/repo", "Fix login", "Users cannot log in", Alice),
        "https://gitlab.test/team/repo/-/issues/12#note_555", body);

    [Fact]
    public async Task PollOnceAsync_MentionTodo_EnqueuesOnceAndMarksDone()
    {
        _gitlab.Get("/api/v4/todos", new[] { IssueMentionTodo() });
        var poller = CreatePoller();

        var ok = await poller.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(ok);
        var evt = Assert.Single(await _queue.DrainAsync());
        Assert.Equal(InboundKind.Message, evt.Kind);
        Assert.Equal("todo:101", evt.EventId);
        Assert.Equal("team/repo#12", evt.ConversationId);
        Assert.Equal("why does login fail?", evt.Text);
        Assert.Equal("alice@example.test", evt.Caller.Email);
        Assert.Equal("CN=alice,DC=corp", evt.Caller.LdapDn);
        Assert.Equal("555", evt.Meta("note_id"));
        Assert.Single(_gitlab.Requests("POST", "/todos/101/mark_as_done"));
        Assert.Contains("to-dos 1", poller.Status, StringComparison.Ordinal);
        Assert.Contains("last error: none", poller.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollOnceAsync_SamePendingTodoTwice_EnqueuesOnce()
    {
        _gitlab.Get("/api/v4/todos", new[] { IssueMentionTodo() });
        var poller = CreatePoller();

        await poller.PollOnceAsync(TestContext.Current.CancellationToken);
        await poller.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, _queue.Depth);
        Assert.Equal(2, _gitlab.Requests("POST", "/todos/101/mark_as_done").Count);
        Assert.Single(_gitlab.Requests("GET", "/api/v4/users/5"));
    }

    [Fact]
    public async Task PollOnceAsync_MentionOnIssueWithActiveTask_IsFollowUp()
    {
        _gitlab.Get("/api/v4/todos", new[] { IssueMentionTodo() });
        _tasks.FindActiveBySourceAsync(TaskSource.GitLabIssue, "team/repo#12", Arg.Any<CancellationToken>())
            .Returns(new AgentTask { Id = 3, Source = TaskSource.GitLabIssue, SourceRef = "team/repo#12", Status = AgentTaskStatus.Working, ProjectId = "42", WorkBranch = "agent/12-fix" });

        await CreatePoller().PollOnceAsync(TestContext.Current.CancellationToken);

        var evt = Assert.Single(await _queue.DrainAsync());
        Assert.Equal(InboundKind.FollowUp, evt.Kind);
        Assert.Equal(3, evt.Task!.ExistingTaskId);
    }

    [Fact]
    public async Task PollOnceAsync_MentionOnAgentsMergeRequestInDiffDiscussion_IsFollowUpWithPosition()
    {
        var todo = Payloads.Todo(202, "directly_addressed", "MergeRequest", ProjectRef, Alice,
            Payloads.MergeRequestTarget(7, 42, "team/repo", "Add feature", "agent/12-fix", "main", Payloads.UserRef(7, "agent-bot")),
            "https://gitlab.test/team/repo/-/merge_requests/7#note_777", "@agent-bot rename this variable");
        _gitlab.Get("/api/v4/todos", new[] { todo });
        _gitlab.Get("/api/v4/projects/42/merge_requests/7/discussions", new[]
        {
            Payloads.Discussion("abc123", false, Payloads.Note(777, "@agent-bot rename this variable", Alice, "2026-09-04T08:00:00.000Z", position: Payloads.Position("src/Program.cs", 17), type: "DiffNote")),
        });
        _tasks.FindByMergeRequestAsync("42", "7", Arg.Any<CancellationToken>())
            .Returns(new AgentTask { Id = 9, Source = TaskSource.GitLabIssue, SourceRef = "team/repo#12", Status = AgentTaskStatus.AwaitingReview, ProjectId = "42", MergeRequestIid = "7", WorkBranch = "agent/12-fix" });

        await CreatePoller().PollOnceAsync(TestContext.Current.CancellationToken);

        var evt = Assert.Single(await _queue.DrainAsync());
        Assert.Equal(InboundKind.FollowUp, evt.Kind);
        Assert.Equal("team/repo!7", evt.ConversationId);
        Assert.Equal("rename this variable\n\n(Comment on src/Program.cs line 17)", evt.Text);
        Assert.Equal(9, evt.Task!.ExistingTaskId);
        Assert.Equal("agent/12-fix", evt.Task.ExistingBranch);
        Assert.Equal("main", evt.Task.BaseBranch);
        Assert.Equal("abc123", evt.Meta("discussion_id"));
        Assert.Equal("777", evt.Meta("note_id"));
        Assert.Single(_gitlab.Requests("POST", "/todos/202/mark_as_done"));
    }

    [Fact]
    public async Task PollOnceAsync_MentionOnForeignMergeRequest_IsMessageByDefault()
    {
        var todo = Payloads.Todo(202, "mentioned", "MergeRequest", ProjectRef, Alice,
            Payloads.MergeRequestTarget(7, 42, "team/repo", "Someone's MR", "feature/x", "main", Alice),
            "https://gitlab.test/team/repo/-/merge_requests/7#note_778", "@agent-bot is this safe?");
        _gitlab.Get("/api/v4/todos", new[] { todo });
        _gitlab.Get("/api/v4/projects/42/merge_requests/7/discussions", Array.Empty<object>());

        await CreatePoller().PollOnceAsync(TestContext.Current.CancellationToken);

        var evt = Assert.Single(await _queue.DrainAsync());
        Assert.Equal(InboundKind.Message, evt.Kind);
        Assert.Equal("is this safe?", evt.Text);
    }

    [Fact]
    public async Task PollOnceAsync_AssignedIssue_EnqueuesTaskRequest()
    {
        var todo = Payloads.Todo(303, "assigned", "Issue", ProjectRef, Alice,
            Payloads.IssueTarget(12, 42, "team/repo", "Fix login", "Users cannot log in", Alice),
            "https://gitlab.test/team/repo/-/issues/12", "Fix login");
        _gitlab.Get("/api/v4/todos", new[] { todo });

        await CreatePoller().PollOnceAsync(TestContext.Current.CancellationToken);

        var evt = Assert.Single(await _queue.DrainAsync());
        Assert.Equal(InboundKind.TaskRequest, evt.Kind);
        Assert.Equal("Fix login\n\nUsers cannot log in", evt.Text);
        Assert.Equal("https://gitlab.test/team/repo.git", evt.Task!.RepoUrl);
        Assert.Equal("main", evt.Task.BaseBranch);
        Assert.Equal("42", evt.Task.ProjectId);
        Assert.Single(_gitlab.Requests("POST", "/todos/303/mark_as_done"));
    }

    [Fact]
    public async Task PollOnceAsync_AssignedIssueWithActiveTask_IsMarkedDoneWithoutEvent()
    {
        var todo = Payloads.Todo(303, "assigned", "Issue", ProjectRef, Alice,
            Payloads.IssueTarget(12, 42, "team/repo", "Fix login", null, Alice), "https://gitlab.test/team/repo/-/issues/12", "Fix login");
        _gitlab.Get("/api/v4/todos", new[] { todo });
        _tasks.FindActiveBySourceAsync(TaskSource.GitLabIssue, "team/repo#12", Arg.Any<CancellationToken>())
            .Returns(new AgentTask { Id = 3, Status = AgentTaskStatus.Queued });

        await CreatePoller().PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, _queue.Depth);
        Assert.Single(_gitlab.Requests("POST", "/todos/303/mark_as_done"));
    }

    [Fact]
    public async Task PollOnceAsync_IgnoredAction_IsMarkedDoneWithoutEvent()
    {
        var todo = Payloads.Todo(404, "build_failed", "MergeRequest", ProjectRef, Alice,
            Payloads.MergeRequestTarget(7, 42, "team/repo", "Add feature", "agent/12-fix", "main", Alice), "https://gitlab.test/team/repo/-/merge_requests/7", "Add feature");
        _gitlab.Get("/api/v4/todos", new[] { todo });

        await CreatePoller().PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, _queue.Depth);
        Assert.Single(_gitlab.Requests("POST", "/todos/404/mark_as_done"));
    }

    [Fact]
    public async Task PollOnceAsync_LabelledIssue_EnqueuesTaskRequestAndAdvancesWatermark()
    {
        _options.Groups = ["team"];
        _gitlab.Get("/api/v4/groups/team/issues", new[]
        {
            Payloads.Issue(13, 42, "team/repo", "Add export", "CSV please", Alice, "2026-09-04T09:59:00.000Z"),
        });
        var poller = CreatePoller();

        var ok = await poller.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(ok);
        var evt = Assert.Single(await _queue.DrainAsync());
        Assert.Equal(InboundKind.TaskRequest, evt.Kind);
        Assert.Equal("label:42:13", evt.EventId);
        Assert.Equal("team/repo#13", evt.Task!.SourceRef);
        Assert.Equal("https://gitlab.test/team/repo.git", evt.Task.RepoUrl);
        Assert.Equal("main", evt.Task.BaseBranch);
        Assert.Equal("alice", evt.Caller.Username);

        var cursor = await _cursors.GetAsync("gitlab:issues:team", TestContext.Current.CancellationToken);
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 9, 59, 0, TimeSpan.Zero), DateTimeOffset.Parse(cursor!, System.Globalization.CultureInfo.InvariantCulture));

        var query = Assert.Single(_gitlab.Requests("GET", "/groups/team/issues")).Query!;
        Assert.Equal("agent", query["labels"].Single());
        Assert.Equal("2026-09-04T09:56:00Z", query["updated_after"].Single()); // now - 2 x overlap on the first run
    }

    [Fact]
    public async Task PollOnceAsync_LabelledIssueSeenTwice_IsEnqueuedOnce_AndSecondQueryUsesWatermark()
    {
        _options.Groups = ["team"];
        _gitlab.Get("/api/v4/groups/team/issues", new[]
        {
            Payloads.Issue(13, 42, "team/repo", "Add export", "CSV please", Alice, "2026-09-04T09:59:00.000Z"),
        });
        var poller = CreatePoller();

        await poller.PollOnceAsync(TestContext.Current.CancellationToken);
        await poller.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, _queue.Depth);
        var requests = _gitlab.Requests("GET", "/groups/team/issues");
        Assert.Equal(2, requests.Count);
        Assert.Equal("2026-09-04T09:57:00Z", requests[1].Query!["updated_after"].Single()); // watermark (09:59) - overlap
    }

    [Fact]
    public async Task PollOnceAsync_LabelledIssueWithActiveTask_IsSkipped()
    {
        _options.Groups = ["team"];
        _gitlab.Get("/api/v4/groups/team/issues", new[] { Payloads.Issue(13, 42, "team/repo", "Add export", null, Alice, "2026-09-04T09:59:00.000Z") });
        _tasks.FindActiveBySourceAsync(TaskSource.GitLabIssue, "team/repo#13", Arg.Any<CancellationToken>()).Returns(new AgentTask { Id = 4, Status = AgentTaskStatus.Working });

        await CreatePoller().PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, _queue.Depth);
    }

    [Fact]
    public async Task PollOnceAsync_AwaitingReviewTaskWithMergedMr_ClosesTaskAsMerged()
    {
        _tasks.ListAsync(Arg.Is<TaskQuery>(q => q.Statuses!.Contains(AgentTaskStatus.AwaitingReview)), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new AgentTask { Id = 9, Status = AgentTaskStatus.AwaitingReview, ProjectId = "42", MergeRequestIid = "7" },
                new AgentTask { Id = 10, Status = AgentTaskStatus.AwaitingReview, ProjectId = "42", MergeRequestIid = "8" },
                new AgentTask { Id = 11, Status = AgentTaskStatus.AwaitingReview, ProjectId = "42", MergeRequestIid = "9" },
                new AgentTask { Id = 12, Status = AgentTaskStatus.AwaitingReview },
            });
        _gitlab.Get("/api/v4/projects/42/merge_requests/7", Payloads.MergeRequest(7, 42, "team/repo", "A", "merged", "agent/1", "main", Alice));
        _gitlab.Get("/api/v4/projects/42/merge_requests/8", Payloads.MergeRequest(8, 42, "team/repo", "B", "closed", "agent/2", "main", Alice));
        _gitlab.Get("/api/v4/projects/42/merge_requests/9", Payloads.MergeRequest(9, 42, "team/repo", "C", "opened", "agent/3", "main", Alice));

        var ok = await CreatePoller().PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(ok);
        await _taskService.Received(1).CloseAsync(9, true, Arg.Any<CancellationToken>());
        await _taskService.Received(1).CloseAsync(10, false, Arg.Any<CancellationToken>());
        await _taskService.DidNotReceive().CloseAsync(11, Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _taskService.DidNotReceive().CloseAsync(12, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollOnceAsync_TodoEndpointFails_ReturnsFalseAndKeepsError_ButStillPollsOtherSteps()
    {
        _gitlab.GetStatus("/api/v4/todos", 500, "{\"message\":\"upstream down\"}");
        _tasks.ListAsync(Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>()).Returns(new[] { new AgentTask { Id = 9, Status = AgentTaskStatus.AwaitingReview, ProjectId = "42", MergeRequestIid = "7" } });
        _gitlab.Get("/api/v4/projects/42/merge_requests/7", Payloads.MergeRequest(7, 42, "team/repo", "A", "merged", "agent/1", "main", Alice));
        var poller = CreatePoller();

        var ok = await poller.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(ok);
        Assert.Contains("upstream down", poller.Status, StringComparison.Ordinal);
        await _taskService.Received(1).CloseAsync(9, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollOnceAsync_OneBadTodo_DoesNotBlockOthers()
    {
        var broken = Payloads.Todo(303, "assigned", "Issue", Payloads.ProjectRef(99, "team/missing"), Alice,
            Payloads.IssueTarget(1, 99, "team/missing", "Broken", null, Alice), "https://gitlab.test/team/missing/-/issues/1", "Broken");
        _gitlab.GetStatus("/api/v4/projects/99", 500, "{\"message\":\"boom\"}");
        _gitlab.Get("/api/v4/todos", new object[] { broken, IssueMentionTodo() });
        var poller = CreatePoller();

        var ok = await poller.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(ok);
        var evt = Assert.Single(await _queue.DrainAsync());
        Assert.Equal("todo:101", evt.EventId);
        Assert.Empty(_gitlab.Requests("POST", "/todos/303/mark_as_done"));
        Assert.Contains("to-do 303", poller.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Backoff_GrowsExponentially_AndCapsAtFiveMinutes()
    {
        var interval = TimeSpan.FromSeconds(30);

        Assert.Equal(TimeSpan.FromSeconds(60), GitLabPoller.Backoff(interval, 1));
        Assert.Equal(TimeSpan.FromSeconds(120), GitLabPoller.Backoff(interval, 2));
        Assert.Equal(TimeSpan.FromMinutes(5), GitLabPoller.Backoff(interval, 10));
        Assert.Equal(TimeSpan.FromMinutes(5), GitLabPoller.Backoff(interval, 100));
    }

    [Fact]
    public async Task ExecuteAsync_RunsAPollThenWaitsForTheInterval()
    {
        _gitlab.Get("/api/v4/todos", new[] { IssueMentionTodo() });
        var poller = CreatePoller();

        await poller.StartAsync(TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_queue.Depth == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await poller.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, _queue.Depth);
        Assert.Equal("GitLab poller", poller.Name);
        Assert.Single(_gitlab.Requests("GET", "/api/v4/todos"));
    }
}
