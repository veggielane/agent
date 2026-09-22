using System.Text.Json;
using Agent.Channels.GitLab;
using Agent.Core.Authorization;
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

    private string? _followUp;

    public GitLabPollerTests()
    {
        _tasks.ListAsync(Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<AgentTask>());
        _taskService.AddFollowUpAsync(Arg.Any<int>(), Arg.Do<string>(i => _followUp = i), Arg.Any<CallerIdentity>(), Arg.Any<CancellationToken>())
            .Returns(new AgentTask { Id = 9, Status = AgentTaskStatus.Queued });
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

    // ---- pipeline awareness -----------------------------------------------------------------------------------

    /// <summary>An MR of task #9 whose head pipeline is in <paramref name="status"/>, plus the note endpoint it replies on.</summary>
    private AgentTask AwaitingReviewWithPipeline(string status, params (string Key, string Value)[] metadata)
    {
        var task = new AgentTask
        {
            Id = 9,
            Source = TaskSource.GitLabIssue,
            SourceRef = "team/repo#12",
            Status = AgentTaskStatus.AwaitingReview,
            ProjectId = "42",
            MergeRequestIid = "7",
            WorkBranch = "agent/12-fix",
        };
        foreach (var (key, value) in metadata)
        {
            task.Metadata[key] = value;
        }

        _tasks.ListAsync(Arg.Is<TaskQuery>(q => q.Statuses!.Contains(AgentTaskStatus.AwaitingReview)), Arg.Any<CancellationToken>()).Returns(new[] { task });
        _gitlab.Get(
            "/api/v4/projects/42/merge_requests/7",
            Payloads.MergeRequest(7, 42, "team/repo", "Add feature", "opened", "agent/12-fix", "main", Payloads.UserRef(7, "agent-bot"), headPipeline: Payloads.Pipeline(900, status)));
        _gitlab.Post("/api/v4/projects/42/merge_requests/7/notes", Payloads.Note(500, "note", Payloads.UserRef(7, "agent-bot"), "2026-09-04T10:00:00.000Z"));
        return task;
    }

    private void FailingJobs()
    {
        _gitlab.Get("/api/v4/projects/42/pipelines/900/jobs", new[]
        {
            Payloads.Job(1, "build", "success", "build"),
            Payloads.Job(2, "test", "failed"),
            Payloads.Job(3, "style", "failed", allowFailure: true),
        });
        _gitlab.GetText("/api/v4/projects/42/jobs/2/trace", "Running tests\nLoginTests.Redirect FAILED\nassert 401 == 200\n");
    }

    [Fact]
    public async Task PollOnceAsync_FailedPipeline_AddsFollowUpPostsNoteAndRecordsTheAttempt()
    {
        var task = AwaitingReviewWithPipeline("failed");
        FailingJobs();

        var ok = await CreatePoller().PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(ok);
        await _taskService.Received(1).AddFollowUpAsync(9, Arg.Any<string>(), Arg.Any<CallerIdentity>(), Arg.Any<CancellationToken>());
        Assert.NotNull(_followUp);
        Assert.Contains("merge request !7", _followUp, StringComparison.Ordinal);
        Assert.Contains("agent/12-fix", _followUp, StringComparison.Ordinal);
        Assert.Contains("Failing jobs: test", _followUp, StringComparison.Ordinal);
        Assert.DoesNotContain("style", _followUp, StringComparison.Ordinal); // allow_failure is not the agent's problem
        Assert.Contains("LoginTests.Redirect FAILED", _followUp, StringComparison.Ordinal);

        var note = Assert.Single(_gitlab.Requests("POST", "/merge_requests/7/notes"));
        using var body = JsonDocument.Parse(note.Body!);
        var text = body.RootElement.GetProperty("body").GetString()!;
        Assert.Contains("[pipeline #900](https://gitlab.test/team/repo/-/pipelines/900)", text, StringComparison.Ordinal);
        Assert.Contains("attempt 1 of 2", text, StringComparison.Ordinal);

        await _tasks.Received(1).UpdateAsync(task, Arg.Any<CancellationToken>());
        Assert.Equal("1", task.Metadata[GitLabPoller.PipelineAttemptsKey]);
        Assert.Equal("900", task.Metadata[GitLabPoller.PipelineLastIdKey]);
        Assert.DoesNotContain(GitLabPoller.PipelineGaveUpKey, task.Metadata.Keys);
    }

    [Fact]
    public async Task PollOnceAsync_SameFailedPipelineTwice_IsHandledOnce()
    {
        AwaitingReviewWithPipeline("failed");
        FailingJobs();
        var poller = CreatePoller();

        await poller.PollOnceAsync(TestContext.Current.CancellationToken);
        await poller.PollOnceAsync(TestContext.Current.CancellationToken);

        await _taskService.Received(1).AddFollowUpAsync(9, Arg.Any<string>(), Arg.Any<CallerIdentity>(), Arg.Any<CancellationToken>());
        Assert.Single(_gitlab.Requests("POST", "/merge_requests/7/notes"));
        Assert.Contains("pipeline fixes 1", poller.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollOnceAsync_SecondFailedPipeline_UsesTheRemainingAttempt()
    {
        var task = AwaitingReviewWithPipeline("failed", (GitLabPoller.PipelineAttemptsKey, "1"), (GitLabPoller.PipelineLastIdKey, "899"));
        FailingJobs();

        await CreatePoller().PollOnceAsync(TestContext.Current.CancellationToken);

        await _taskService.Received(1).AddFollowUpAsync(9, Arg.Any<string>(), Arg.Any<CallerIdentity>(), Arg.Any<CancellationToken>());
        Assert.Equal("2", task.Metadata[GitLabPoller.PipelineAttemptsKey]);
        var note = Assert.Single(_gitlab.Requests("POST", "/merge_requests/7/notes"));
        Assert.Contains("attempt 2 of 2", JsonDocument.Parse(note.Body!).RootElement.GetProperty("body").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollOnceAsync_FixAttemptsExhausted_MirrorsTheGiveUpNoteOnce()
    {
        var task = AwaitingReviewWithPipeline("failed", (GitLabPoller.PipelineAttemptsKey, "2"), (GitLabPoller.PipelineLastIdKey, "899"));
        task.MergeRequestUrl = "https://gitlab.test/team/repo/-/merge_requests/7";
        FailingJobs();
        var router = Substitute.For<ITaskNotifierRouter>();
        var poller = new GitLabPoller(_gitlab.Client, _queue, _processed, _cursors, _tasks, _taskService, TestOptions.Monitor(_options), Loggers.For<GitLabPoller>(), _time, router);

        await poller.PollOnceAsync(TestContext.Current.CancellationToken);
        await poller.PollOnceAsync(TestContext.Current.CancellationToken);

        await router.Received(1).MirrorAsync(
            task,
            Arg.Is<TaskNotification>(n => n.Key == "pipeline-gave-up:900" && n.Terminal && n.Markdown.Contains("leaving this merge request for a human") && n.Markdown.Contains("merge_requests/7")),
            Arg.Any<CancellationToken>());
        await router.DidNotReceiveWithAnyArgs().NotifyAsync(default!, default(TaskNotification)!, default);
    }

    [Fact]
    public async Task PollOnceAsync_FixAttemptsExhausted_LeavesItForAHumanAndStops()
    {
        var task = AwaitingReviewWithPipeline("failed", (GitLabPoller.PipelineAttemptsKey, "2"), (GitLabPoller.PipelineLastIdKey, "899"));
        FailingJobs();
        var poller = CreatePoller();

        await poller.PollOnceAsync(TestContext.Current.CancellationToken);
        await poller.PollOnceAsync(TestContext.Current.CancellationToken);

        await _taskService.DidNotReceive().AddFollowUpAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CallerIdentity>(), Arg.Any<CancellationToken>());
        var note = Assert.Single(_gitlab.Requests("POST", "/merge_requests/7/notes"));
        var text = JsonDocument.Parse(note.Body!).RootElement.GetProperty("body").GetString()!;
        Assert.Contains("still failing after 2 attempts", text, StringComparison.Ordinal);
        Assert.Contains("leaving this merge request for a human", text, StringComparison.Ordinal);
        Assert.Equal("true", task.Metadata[GitLabPoller.PipelineGaveUpKey]);
        Assert.Equal("2", task.Metadata[GitLabPoller.PipelineAttemptsKey]);
        Assert.Empty(_gitlab.Requests("GET", "/pipelines/900/jobs"));
    }

    [Theory]
    [InlineData("running")]
    [InlineData("pending")]
    [InlineData("success")]
    [InlineData("canceled")]
    public async Task PollOnceAsync_PipelineNotFailed_DoesNothing(string status)
    {
        AwaitingReviewWithPipeline(status);
        FailingJobs();

        var ok = await CreatePoller().PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(ok);
        await _taskService.DidNotReceive().AddFollowUpAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CallerIdentity>(), Arg.Any<CancellationToken>());
        await _tasks.DidNotReceive().UpdateAsync(Arg.Any<AgentTask>(), Arg.Any<CancellationToken>());
        Assert.Empty(_gitlab.Requests("POST", "/merge_requests/7/notes"));
    }

    [Fact]
    public async Task PollOnceAsync_WatchPipelinesOff_IgnoresTheFailure()
    {
        _options.WatchPipelines = false;
        AwaitingReviewWithPipeline("failed");
        FailingJobs();

        await CreatePoller().PollOnceAsync(TestContext.Current.CancellationToken);

        await _taskService.DidNotReceive().AddFollowUpAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CallerIdentity>(), Arg.Any<CancellationToken>());
        Assert.Empty(_gitlab.Requests("POST", "/merge_requests/7/notes"));
    }

    [Fact]
    public async Task PollOnceAsync_MaxAttemptsZero_ReportsTheFailureWithoutRetrying()
    {
        _options.MaxPipelineFixAttempts = 0;
        AwaitingReviewWithPipeline("failed");
        FailingJobs();

        await CreatePoller().PollOnceAsync(TestContext.Current.CancellationToken);

        await _taskService.DidNotReceive().AddFollowUpAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CallerIdentity>(), Arg.Any<CancellationToken>());
        var note = Assert.Single(_gitlab.Requests("POST", "/merge_requests/7/notes"));
        Assert.Contains("switched off", JsonDocument.Parse(note.Body!).RootElement.GetProperty("body").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollOnceAsync_FailedPipelineWithoutFailingJobs_StillQueuesAFix()
    {
        AwaitingReviewWithPipeline("failed");
        _gitlab.Get("/api/v4/projects/42/pipelines/900/jobs", new[] { Payloads.Job(3, "style", "failed", allowFailure: true) });

        await CreatePoller().PollOnceAsync(TestContext.Current.CancellationToken);

        await _taskService.Received(1).AddFollowUpAsync(9, Arg.Any<string>(), Arg.Any<CallerIdentity>(), Arg.Any<CancellationToken>());
        Assert.Contains("no failing job", _followUp!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollOnceAsync_LongTrace_IsCutToTheConfiguredBudget()
    {
        _options.PipelineTraceChars = 200;
        AwaitingReviewWithPipeline("failed");
        _gitlab.Get("/api/v4/projects/42/pipelines/900/jobs", new[] { Payloads.Job(2, "test", "failed") });
        _gitlab.GetText("/api/v4/projects/42/jobs/2/trace", "restoring packages\n" + new string('y', 20_000) + "\nassert 401 == 200");

        await CreatePoller().PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(_followUp);
        Assert.DoesNotContain("restoring packages", _followUp, StringComparison.Ordinal);
        Assert.Contains("…", _followUp, StringComparison.Ordinal);
        Assert.Contains("assert 401 == 200", _followUp, StringComparison.Ordinal);
        Assert.True(_followUp.Length < 1000, $"instruction was {_followUp.Length} characters");
    }

    [Fact]
    public async Task PollOnceAsync_TraceUnavailable_StillQueuesAFixNamingTheJob()
    {
        AwaitingReviewWithPipeline("failed");
        _gitlab.Get("/api/v4/projects/42/pipelines/900/jobs", new[] { Payloads.Job(2, "test", "failed") });
        _gitlab.GetStatus("/api/v4/projects/42/jobs/2/trace", 500, "{\"message\":\"log gone\"}");

        var ok = await CreatePoller().PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(ok);
        Assert.Contains("Failing jobs: test", _followUp!, StringComparison.Ordinal);
        Assert.Contains("(no log available)", _followUp!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollOnceAsync_PipelineOutcomes_AreCountedOnTheTaskCounter()
    {
        AwaitingReviewWithPipeline("failed");
        FailingJobs();
        using var transitions = new TransitionRecorder();

        await CreatePoller().PollOnceAsync(TestContext.Current.CancellationToken);
        _options.MaxPipelineFixAttempts = 1; // the attempt just recorded is now the whole budget
        AwaitingReviewWithPipeline("failed", (GitLabPoller.PipelineAttemptsKey, "1"), (GitLabPoller.PipelineLastIdKey, "899"));
        await CreatePoller().PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Contains("pipeline-failed", transitions.Seen);
        Assert.Contains("pipeline-fix-exhausted", transitions.Seen);
    }

    /// <summary>Collects the <c>transition</c> tag of every <c>agent.tasks</c> measurement; the meter is process-wide.</summary>
    private sealed class TransitionRecorder : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener;

        public TransitionRecorder()
        {
            _listener = new System.Diagnostics.Metrics.MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Name == "agent.tasks")
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "transition" && tag.Value is string value)
                    {
                        Seen.Add(value);
                    }
                }
            });
            _listener.Start();
        }

        public System.Collections.Concurrent.ConcurrentBag<string> Seen { get; } = [];

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task PollOnceAsync_MergedMrWithFailedPipeline_ClosesTheTaskWithoutFixing()
    {
        _tasks.ListAsync(Arg.Is<TaskQuery>(q => q.Statuses!.Contains(AgentTaskStatus.AwaitingReview)), Arg.Any<CancellationToken>())
            .Returns(new[] { new AgentTask { Id = 9, Status = AgentTaskStatus.AwaitingReview, ProjectId = "42", MergeRequestIid = "7" } });
        _gitlab.Get(
            "/api/v4/projects/42/merge_requests/7",
            Payloads.MergeRequest(7, 42, "team/repo", "Add feature", "merged", "agent/12-fix", "main", Alice, headPipeline: Payloads.Pipeline(900, "failed")));

        await CreatePoller().PollOnceAsync(TestContext.Current.CancellationToken);

        await _taskService.Received(1).CloseAsync(9, true, Arg.Any<CancellationToken>());
        await _taskService.DidNotReceive().AddFollowUpAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CallerIdentity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollOnceAsync_OneUnreachableMergeRequest_DoesNotHideTheOthers()
    {
        _tasks.ListAsync(Arg.Is<TaskQuery>(q => q.Statuses!.Contains(AgentTaskStatus.AwaitingReview)), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new AgentTask { Id = 8, Status = AgentTaskStatus.AwaitingReview, ProjectId = "42", MergeRequestIid = "6" },
                new AgentTask { Id = 9, Status = AgentTaskStatus.AwaitingReview, ProjectId = "42", MergeRequestIid = "7" },
            });
        _gitlab.GetStatus("/api/v4/projects/42/merge_requests/6", 500, "{\"message\":\"boom\"}");
        _gitlab.Get("/api/v4/projects/42/merge_requests/7", Payloads.MergeRequest(7, 42, "team/repo", "B", "merged", "agent/2", "main", Alice));

        var poller = CreatePoller();
        var ok = await poller.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(ok);
        await _taskService.Received(1).CloseAsync(9, true, Arg.Any<CancellationToken>());
        Assert.Contains("task 8", poller.Status, StringComparison.Ordinal);
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
