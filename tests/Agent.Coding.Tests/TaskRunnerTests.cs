using Agent.Core.Channels;
using Agent.Core.Tasks;
using Agent.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Agent.Coding.Tests;

public sealed class TaskRunnerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly InMemoryTaskStore _store = new();
    private readonly TaskCancellationRegistry _cancellations = new();
    private readonly ITaskNotifierRouter _notifier = Substitute.For<ITaskNotifierRouter>();
    private readonly IMergeRequestPublisher _mergeRequests = Substitute.For<IMergeRequestPublisher>();
    private readonly List<string> _notifications = [];
    private readonly List<TaskNotification> _keyed = [];

    public TaskRunnerTests()
    {
        _notifier.NotifyAsync(Arg.Any<AgentTask>(), Arg.Do<string>(_notifications.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _notifier.NotifyAsync(Arg.Any<AgentTask>(), Arg.Do<TaskNotification>(n =>
        {
            _keyed.Add(n);
            _notifications.Add(n.Markdown);
        }), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _mergeRequests.EnsureMergeRequestAsync(Arg.Any<MergeRequestSpec>(), Arg.Any<CancellationToken>())
            .Returns(ci => new MergeRequestInfo("7", "https://gitlab.local/group/repo/-/merge_requests/7", "opened", ci.Arg<MergeRequestSpec>().SourceBranch));
    }

    public void Dispose() => _dir.Dispose();

    private string WorkspaceRoot => _dir.Combine("work");

    private (TaskRunner Runner, GitRunner Git) Create(ICodingEngine engine, bool withMergeRequests = true, ITaskPlanner? planner = null, Action<CodingOptions>? configure = null, IRepositoryPolicy? repositories = null)
    {
        var options = TestOptions.Coding(WorkspaceRoot, configure);
        var monitor = TestOptions.Monitor(options);
        var processes = new CliWrapProcessRunner(monitor, NullLogger<CliWrapProcessRunner>.Instance);
        var git = new GitRunner(processes, NullLogger<GitRunner>.Instance);
        var workspaces = new WorkspaceManager(git, monitor, NullLogger<WorkspaceManager>.Instance);
        var runner = new TaskRunner(_store, _cancellations, workspaces, engine, git, _notifier, monitor, NullLogger<TaskRunner>.Instance, null, withMergeRequests ? _mergeRequests : null, planner, repositories);
        return (runner, git);
    }

    /// <summary>A planner that records what it was asked and returns a canned plan.</summary>
    private sealed class FakePlanner(string? plan) : ITaskPlanner
    {
        public List<string> Instructions { get; } = [];

        public Task<string?> PlanAsync(Workspace workspace, string instruction, Core.Authorization.CallerIdentity requester, CancellationToken cancellationToken)
        {
            Instructions.Add(instruction);
            return Task.FromResult(plan);
        }
    }

    private async Task<AgentTask> QueueTaskAsync(string? repoUrl, string? projectId = "group/repo")
    {
        await _store.AddAsync(new AgentTask
        {
            Source = TaskSource.GitLabIssue,
            SourceRef = "group/repo#12",
            Title = "Add greeting",
            RequesterId = "GitLab:u1",
            RequesterName = "Alice",
            RequesterUsername = "alice",
            NotifyChannel = Channel.GitLab,
            ConversationId = "group/repo#12",
            RepoUrl = repoUrl,
            ProjectId = projectId,
            Instruction = "Add a greeting file.",
            Status = AgentTaskStatus.Queued,
        }, TestContext.Current.CancellationToken);

        return (await _store.ClaimNextQueuedAsync("test-worker", TestContext.Current.CancellationToken))!;
    }

    private static FakeCodingEngine EngineWriting(string file, string content, CodingStopReason stop = CodingStopReason.Done, bool? verified = true)
        => new(run =>
        {
            File.WriteAllText(Path.Combine(run.Workspace.RepoPath, file), content);
            return new CodingResult("Added " + file + "\n\nDetails here.", [file], ["dotnet build"], verified, "ok", 3, 1234, stop, null);
        });

    [Fact]
    public async Task RunAsync_PostsThePlanBeforeAnyCodeIsWritten()
    {
        GitTestHelper.SkipIfMissing();
        var remote = GitTestHelper.CreateBareRepoWithCommit(_dir.Combine("remote-plan"));
        var planner = new FakePlanner("**Understanding**\nAdd a greeting.\n\n**Plan**\n- write the file");
        var (runner, _) = Create(EngineWriting("hello.txt", "hi"), planner: planner);
        var task = await QueueTaskAsync(remote);

        await runner.RunAsync(task, TestContext.Current.CancellationToken);

        Assert.Equal(["Add a greeting file."], planner.Instructions);
        Assert.Contains(_notifications, n => n.Contains("**Understanding**", StringComparison.Ordinal));

        // It must arrive before the merge request, so a person can cancel while the branch is still empty.
        var planIndex = _notifications.FindIndex(n => n.Contains("**Understanding**", StringComparison.Ordinal));
        var mrIndex = _notifications.FindIndex(n => n.Contains("merge_requests/7", StringComparison.Ordinal));
        Assert.True(planIndex >= 0 && mrIndex > planIndex, "the plan must be posted before the merge request");
        Assert.Contains("!cancel", _notifications[planIndex], StringComparison.Ordinal);

        var events = await _store.GetEventsAsync(task.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(events, e => e.Type == "plan");
    }

    [Fact]
    public async Task RunAsync_PlanDisabledOrUnavailable_RunsAnyway()
    {
        GitTestHelper.SkipIfMissing();
        var remote = GitTestHelper.CreateBareRepoWithCommit(_dir.Combine("remote-noplan"));
        var planner = new FakePlanner(null);
        var (runner, _) = Create(EngineWriting("hello.txt", "hi"), planner: planner, configure: o => o.PostPlan = false);
        var task = await QueueTaskAsync(remote);

        await runner.RunAsync(task, TestContext.Current.CancellationToken);

        Assert.Empty(planner.Instructions);
        Assert.Equal(AgentTaskStatus.AwaitingReview, (await _store.GetAsync(task.Id, TestContext.Current.CancellationToken))!.Status);
    }

    [Theory]
    [InlineData("Fix the crash on login", "fix")]
    [InlineData("Add a caching layer", "feat")]
    [InlineData("Refactor the parser", "refactor")]
    [InlineData("Update the readme", "docs")]
    public void CommitMessage_UsesConventionalCommits(string instruction, string expectedType)
    {
        // The summary says "Added ..." for every kind of change, so the type must come from the request.
        var task = new AgentTask { Id = 12, SourceRef = "group/repo#12", Title = instruction, Instruction = instruction, RequesterName = "Alice" };
        var result = new CodingResult("Added a cache to the login path.", [], [], true, null, 1, 10, CodingStopReason.Done, null);

        var message = TaskRunner.CommitMessage(task, result, conventional: true);
        var subject = message.Split('\n')[0];

        Assert.StartsWith(expectedType + ": ", subject, StringComparison.Ordinal);
        Assert.True(subject.Length <= 72, $"subject was {subject.Length} characters: {subject}");
        Assert.DoesNotContain(".", subject[^1..], StringComparison.Ordinal);
        Assert.Contains("Requested-by: Alice", message, StringComparison.Ordinal);
        Assert.Contains("Refs: group/repo#12", message, StringComparison.Ordinal);
        Assert.Contains("Task: #12", message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommitMessage_RequestWithoutAnIntentWord_FallsBackToTheSummaryThenChore()
    {
        var task = new AgentTask { Id = 1, SourceRef = "r#1", Title = "Rotate the deployment keys", Instruction = "Rotate the deployment keys", RequesterName = "A" };

        // Neither the request nor the summary names a kind of change.
        var neutral = new CodingResult("Rotated the keys.", [], [], null, null, 1, 1, CodingStopReason.Done, null);
        Assert.StartsWith("chore: ", TaskRunner.CommitMessage(task, neutral, conventional: true), StringComparison.Ordinal);

        // The summary is the fallback when the request itself gives nothing away.
        var telling = new CodingResult("Fixed the expiry check.", [], [], null, null, 1, 1, CodingStopReason.Done, null);
        Assert.StartsWith("fix: ", TaskRunner.CommitMessage(task, telling, conventional: true), StringComparison.Ordinal);
    }

    [Fact]
    public void CommitMessage_ConventionalOff_KeepsTheSourceRefSubject()
    {
        var task = new AgentTask { Id = 3, SourceRef = "PROJ-9", Title = "t", Instruction = "fix it", RequesterName = "Bob" };
        var result = new CodingResult("Fixed the thing.", [], [], null, null, 1, 1, CodingStopReason.Done, null);

        var message = TaskRunner.CommitMessage(task, result, conventional: false);

        Assert.StartsWith("PROJ-9: Fixed the thing.", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_HappyPath_PushesBranchOpensMrAndNotifies()
    {
        GitTestHelper.SkipIfMissing();
        var url = GitTestHelper.CreateBareRepoWithCommit(_dir.Path, "App.sln", string.Empty);
        var task = await QueueTaskAsync(url);
        var (runner, _) = Create(EngineWriting("GREETING.md", "hello\n"));

        await runner.RunAsync(task, TestContext.Current.CancellationToken);

        var final = (await _store.GetAsync(task.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(AgentTaskStatus.AwaitingReview, final.Status);
        Assert.Equal("https://gitlab.local/group/repo/-/merge_requests/7", final.MergeRequestUrl);
        Assert.Equal("7", final.MergeRequestIid);
        Assert.Equal("agent/group-repo-12-add-greeting", final.WorkBranch);
        Assert.Equal("main", final.BaseBranch);
        Assert.StartsWith("Added GREETING.md", final.Summary, StringComparison.Ordinal);
        Assert.Equal(3, final.Turns);
        Assert.Equal(1234, final.TokensUsed);

        var bare = GitTestHelper.BarePath(_dir.Path);
        var (_, branches) = GitTestHelper.Run(_dir.Path, $"--git-dir={bare}", "branch", "--list", "agent/*");
        Assert.Contains("agent/group-repo-12-add-greeting", branches, StringComparison.Ordinal);
        var (_, log) = GitTestHelper.Run(_dir.Path, $"--git-dir={bare}", "log", "-1", "--format=%B", "agent/group-repo-12-add-greeting");
        Assert.StartsWith("feat: added GREETING.md", log, StringComparison.Ordinal);
        Assert.Contains("Refs: group/repo#12", log, StringComparison.Ordinal);
        Assert.Contains("Requested-by: Alice", log, StringComparison.Ordinal);
        Assert.Contains($"Task: #{task.Id}", log, StringComparison.Ordinal);

        await _mergeRequests.Received(1).EnsureMergeRequestAsync(
            Arg.Is<MergeRequestSpec>(s => s.ProjectId == "group/repo" && s.SourceBranch == "agent/group-repo-12-add-greeting" && s.TargetBranch == "main" && !s.Draft && s.ReviewerUsername == "alice" && s.Title == "group/repo#12: Add greeting" && s.Description.Contains("Closes group/repo#12")),
            Arg.Any<CancellationToken>());
        Assert.Contains(_notifications, n => n.StartsWith("Opened MR https://gitlab.local", StringComparison.Ordinal) && n.Contains("Verified: yes", StringComparison.Ordinal));

        var events = await _store.GetEventsAsync(task.Id, 50, TestContext.Current.CancellationToken);
        Assert.Contains(events, e => e.Type == "branch");
        Assert.Contains(events, e => e.Type == "pushed");
        Assert.Contains(events, e => e.Type == "published");
        Assert.Empty(Directory.EnumerateDirectories(WorkspaceRoot));
        Assert.Empty(_cancellations.RunningTaskIds);
    }

    [Fact]
    public async Task RunAsync_TerminalNotification_IsKeyedByAttempt_AndThePlanIsNot()
    {
        GitTestHelper.SkipIfMissing();
        var url = GitTestHelper.CreateBareRepoWithCommit(_dir.Path);
        var task = await QueueTaskAsync(url);
        var (runner, _) = Create(EngineWriting("GREETING.md", "hello\n"), planner: new FakePlanner("I will add a greeting."));

        await runner.RunAsync(task, TestContext.Current.CancellationToken);

        Assert.Equal(1, task.Attempts);
        var plan = Assert.Single(_keyed, n => n.Key == "plan");
        Assert.False(plan.Terminal);
        var published = Assert.Single(_keyed, n => n.Key == "published:1");
        Assert.True(published.Terminal);
        Assert.StartsWith("Opened MR", published.Markdown, StringComparison.Ordinal);
        Assert.Equal(2, _keyed.Count);
        await _notifier.DidNotReceiveWithAnyArgs().NotifyAsync(default!, default(string)!, default);
    }

    [Fact]
    public async Task RunAsync_Failure_IsAKeyedTerminalNotification()
    {
        GitTestHelper.SkipIfMissing();
        var url = GitTestHelper.CreateBareRepoWithCommit(_dir.Path);
        var task = await QueueTaskAsync(url);
        var (runner, _) = Create(new FakeCodingEngine((_, _) => throw new InvalidOperationException("engine exploded")));

        await runner.RunAsync(task, TestContext.Current.CancellationToken);

        var failed = Assert.Single(_keyed);
        Assert.Equal("failed:1", failed.Key);
        Assert.True(failed.Terminal);
        Assert.Contains("engine exploded", failed.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RecordsToolActionsAsEvents_AndNamesThePushedFiles()
    {
        GitTestHelper.SkipIfMissing();
        var url = GitTestHelper.CreateBareRepoWithCommit(_dir.Path);
        var task = await QueueTaskAsync(url);
        var engine = new FakeCodingEngine(run =>
        {
            run.Actions!.Report(new CodingAction(CodingActionKind.Run, "dotnet build (ok)"));
            run.Actions.Report(new CodingAction(CodingActionKind.Write, "GREETING.md"));
            run.Actions.Report(new CodingAction(CodingActionKind.Edit, "README.md"));
            File.WriteAllText(Path.Combine(run.Workspace.RepoPath, "GREETING.md"), "hello\n");
            File.AppendAllText(Path.Combine(run.Workspace.RepoPath, "README.md"), "more\n");
            return new CodingResult("Added a greeting.", ["GREETING.md", "README.md"], ["dotnet build"], true, "ok", 2, 100, CodingStopReason.Done, null);
        });
        var (runner, _) = Create(engine);

        await runner.RunAsync(task, TestContext.Current.CancellationToken);

        var events = await _store.GetEventsAsync(task.Id, 50, TestContext.Current.CancellationToken);
        var tools = events.Where(e => e.Type.StartsWith("tool.", StringComparison.Ordinal)).Select(e => $"{e.Type} {e.Message}").ToList();
        Assert.Equal(["tool.run dotnet build (ok)", "tool.write GREETING.md", "tool.edit README.md"], tools);
        var working = events.Single(e => e.Type == "working");
        Assert.All(events.Where(e => e.Type.StartsWith("tool.", StringComparison.Ordinal)), e => Assert.True(e.Id > working.Id));
        var pushed = events.Single(e => e.Type == "pushed");
        Assert.Equal("Pushed agent/group-repo-12-add-greeting (2 changed files): GREETING.md, README.md", pushed.Message);
        Assert.True(pushed.Id > events.Last(e => e.Type.StartsWith("tool.", StringComparison.Ordinal)).Id);
    }

    [Fact]
    public async Task RunAsync_RepositoryRefusedByPolicy_FailsWithTheReasonWithoutCloning()
    {
        var task = await QueueTaskAsync("https://gitlab.test/other/repo.git");
        var policy = Substitute.For<IRepositoryPolicy>();
        policy.Check("https://gitlab.test/other/repo.git").Returns(RepositoryDecision.Deny("`other/repo` is not among the projects I may work on."));
        var engine = new FakeCodingEngine(_ => throw new InvalidOperationException("must not run"));
        var (runner, _) = Create(engine, repositories: policy);

        await runner.RunAsync(task, TestContext.Current.CancellationToken);

        var final = (await _store.GetAsync(task.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(AgentTaskStatus.Failed, final.Status);
        Assert.Equal("`other/repo` is not among the projects I may work on.", final.Error);
        Assert.False(Directory.Exists(WorkspaceRoot));
        var denied = Assert.Single(_keyed);
        Assert.Equal("repository-denied:1", denied.Key);
        Assert.True(denied.Terminal);
        Assert.Contains("`other/repo` is not among the projects I may work on.", denied.Markdown, StringComparison.Ordinal);
        Assert.Contains((await _store.GetEventsAsync(task.Id, 50, TestContext.Current.CancellationToken)), e => e.Type == "failed" && e.Message.Contains("other/repo", StringComparison.Ordinal));
    }

    [Fact]
    public void PushedMessage_NamesTheFiles_AndCountsTheRestPastTheCap()
    {
        Assert.Equal("Pushed agent/x (0 changed files)", TaskRunner.PushedMessage("agent/x", []));
        Assert.Equal("Pushed agent/x (2 changed files): a.cs, b.cs", TaskRunner.PushedMessage("agent/x", ["a.cs", "b.cs"]));

        var many = Enumerable.Range(1, 45).Select(i => $"f{i}.cs").ToList();
        var message = TaskRunner.PushedMessage("agent/x", many);
        Assert.StartsWith("Pushed agent/x (45 changed files): f1.cs, ", message, StringComparison.Ordinal);
        Assert.EndsWith("f40.cs … (+5 more)", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_BudgetExhausted_OpensDraftAndSaysSo()
    {
        GitTestHelper.SkipIfMissing();
        var url = GitTestHelper.CreateBareRepoWithCommit(_dir.Path);
        var task = await QueueTaskAsync(url);
        var (runner, _) = Create(EngineWriting("partial.txt", "wip", CodingStopReason.BudgetTurns, verified: null));

        await runner.RunAsync(task, TestContext.Current.CancellationToken);

        var final = (await _store.GetAsync(task.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(AgentTaskStatus.AwaitingReview, final.Status);
        await _mergeRequests.Received(1).EnsureMergeRequestAsync(Arg.Is<MergeRequestSpec>(s => s.Draft), Arg.Any<CancellationToken>());
        Assert.Contains(_notifications, n => n.Contains("budget was exhausted", StringComparison.Ordinal) && n.Contains("Verified: n.a.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_NoChanges_EndsInNeedsInput()
    {
        GitTestHelper.SkipIfMissing();
        var url = GitTestHelper.CreateBareRepoWithCommit(_dir.Path);
        var task = await QueueTaskAsync(url);
        var engine = new FakeCodingEngine(_ => new CodingResult("Nothing to do here.", [], [], null, null, 1, 10, CodingStopReason.Done, null));
        var (runner, _) = Create(engine);

        await runner.RunAsync(task, TestContext.Current.CancellationToken);

        var final = (await _store.GetAsync(task.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(AgentTaskStatus.NeedsInput, final.Status);
        Assert.StartsWith("I made no changes:", final.Summary, StringComparison.Ordinal);
        await _mergeRequests.DidNotReceive().EnsureMergeRequestAsync(Arg.Any<MergeRequestSpec>(), Arg.Any<CancellationToken>());
        Assert.Contains(_notifications, n => n.StartsWith("I made no changes.", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateDirectories(WorkspaceRoot));
    }

    [Fact]
    public async Task RunAsync_EngineThrows_EndsInFailedAndKeepsWorkspace()
    {
        GitTestHelper.SkipIfMissing();
        var url = GitTestHelper.CreateBareRepoWithCommit(_dir.Path);
        var task = await QueueTaskAsync(url);
        var engine = new FakeCodingEngine(_ => throw new InvalidOperationException("engine exploded"));
        var (runner, _) = Create(engine);

        await runner.RunAsync(task, TestContext.Current.CancellationToken);

        var final = (await _store.GetAsync(task.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(AgentTaskStatus.Failed, final.Status);
        Assert.Equal("engine exploded", final.Error);
        Assert.Contains(_notifications, n => n.StartsWith("Task failed: engine exploded", StringComparison.Ordinal));
        Assert.Single(Directory.EnumerateDirectories(WorkspaceRoot));
        var events = await _store.GetEventsAsync(task.Id, 50, TestContext.Current.CancellationToken);
        Assert.Contains(events, e => e.Type == "failed");
    }

    [Fact]
    public async Task RunAsync_EngineReportsError_EndsInFailed()
    {
        GitTestHelper.SkipIfMissing();
        var url = GitTestHelper.CreateBareRepoWithCommit(_dir.Path);
        var task = await QueueTaskAsync(url);
        var engine = new FakeCodingEngine(_ => new CodingResult("x", [], [], null, null, 0, 0, CodingStopReason.Error, "model unavailable"));
        var (runner, _) = Create(engine);

        await runner.RunAsync(task, TestContext.Current.CancellationToken);

        var final = (await _store.GetAsync(task.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(AgentTaskStatus.Failed, final.Status);
        Assert.Equal("model unavailable", final.Error);
    }

    [Fact]
    public async Task RunAsync_NoRepoUrl_EndsInNeedsInputWithoutCloning()
    {
        var task = await QueueTaskAsync(repoUrl: null);
        var engine = new FakeCodingEngine(_ => throw new InvalidOperationException("must not run"));
        var (runner, _) = Create(engine);

        await runner.RunAsync(task, TestContext.Current.CancellationToken);

        var final = (await _store.GetAsync(task.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(AgentTaskStatus.NeedsInput, final.Status);
        Assert.Contains(_notifications, n => n.Contains("which repository", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_CancelledWhileWorking_EndsInCancelled()
    {
        GitTestHelper.SkipIfMissing();
        var url = GitTestHelper.CreateBareRepoWithCommit(_dir.Path);
        var task = await QueueTaskAsync(url);
        var engine = new FakeCodingEngine(async (run, ct) =>
        {
            _cancellations.RequestCancel(run.Workspace.TaskId);
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        var (runner, _) = Create(engine);

        await runner.RunAsync(task, TestContext.Current.CancellationToken);

        var final = (await _store.GetAsync(task.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(AgentTaskStatus.Cancelled, final.Status);
        Assert.Contains(_notifications, n => n.StartsWith("Task cancelled", StringComparison.Ordinal));
        Assert.Empty(_cancellations.RunningTaskIds);
    }

    [Fact]
    public async Task RunAsync_FollowUp_ContinuesOnExistingBranchAndUsesPendingInstruction()
    {
        GitTestHelper.SkipIfMissing();
        var url = GitTestHelper.CreateBareRepoWithCommit(_dir.Path);
        var task = await QueueTaskAsync(url);
        var (runner, _) = Create(EngineWriting("first.txt", "1"));
        await runner.RunAsync(task, TestContext.Current.CancellationToken);
        var afterFirst = (await _store.GetAsync(task.Id, TestContext.Current.CancellationToken))!;

        afterFirst.PendingInstruction = "Now add second.txt";
        afterFirst.Status = AgentTaskStatus.Queued;
        await _store.UpdateAsync(afterFirst, TestContext.Current.CancellationToken);
        var claimed = (await _store.ClaimNextQueuedAsync("test-worker", TestContext.Current.CancellationToken))!;

        CodingRun? seen = null;
        var engine = new FakeCodingEngine(run =>
        {
            seen = run;
            Assert.True(File.Exists(Path.Combine(run.Workspace.RepoPath, "first.txt")));
            File.WriteAllText(Path.Combine(run.Workspace.RepoPath, "second.txt"), "2");
            return new CodingResult("Added second.txt", ["second.txt"], [], null, null, 1, 10, CodingStopReason.Done, null);
        });
        var (runner2, _) = Create(engine);
        await runner2.RunAsync(claimed, TestContext.Current.CancellationToken);

        Assert.NotNull(seen);
        Assert.True(seen.IsFollowUp);
        Assert.Equal("Now add second.txt", seen.Instruction);
        Assert.StartsWith("Added first.txt", seen.PreviousSummary, StringComparison.Ordinal);
        var final = (await _store.GetAsync(task.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(AgentTaskStatus.AwaitingReview, final.Status);
        Assert.Null(final.PendingInstruction);
        Assert.Equal(afterFirst.WorkBranch, final.WorkBranch);

        var bare = GitTestHelper.BarePath(_dir.Path);
        var (_, log) = GitTestHelper.Run(_dir.Path, $"--git-dir={bare}", "log", "--format=%s", final.WorkBranch!);
        Assert.Contains("added second.txt", log, StringComparison.Ordinal);
        Assert.Contains("added first.txt", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WithoutMergeRequestPublisher_StillPushesAndAwaitsReview()
    {
        GitTestHelper.SkipIfMissing();
        var url = GitTestHelper.CreateBareRepoWithCommit(_dir.Path);
        var task = await QueueTaskAsync(url, projectId: null);
        var (runner, _) = Create(EngineWriting("x.txt", "x"), withMergeRequests: false);

        await runner.RunAsync(task, TestContext.Current.CancellationToken);

        var final = (await _store.GetAsync(task.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(AgentTaskStatus.AwaitingReview, final.Status);
        Assert.Null(final.MergeRequestUrl);
        Assert.Contains(_notifications, n => n.StartsWith("Pushed branch", StringComparison.Ordinal));
    }

    private sealed class FakeCodingEngine : ICodingEngine
    {
        private readonly Func<CodingRun, CancellationToken, Task<CodingResult>> _impl;

        public FakeCodingEngine(Func<CodingRun, CodingResult> impl)
            : this((run, _) => Task.FromResult(impl(run)))
        {
        }

        public FakeCodingEngine(Func<CodingRun, CancellationToken, Task<CodingResult>> impl)
        {
            _impl = impl;
        }

        public Task<CodingResult> RunAsync(CodingRun run, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            progress?.Report("working");
            return _impl(run, cancellationToken);
        }
    }
}
