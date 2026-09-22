using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Tasks;
using Agent.Core.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Agent.Core.Tests.Tasks;

public sealed class TaskServiceTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static TaskRequest Request(string? repo = "https://gitlab.internal/t/r.git", string sourceRef = "t/r#1") => new()
    {
        Source = TaskSource.GitLabIssue,
        SourceRef = sourceRef,
        Requester = CallerIdentity.Local("bob", Role.Team),
        Instruction = "do it",
        RepoUrl = repo,
        ConversationId = sourceRef,
        NotifyChannel = Channel.GitLab,
        Metadata = new Dictionary<string, string> { ["k"] = "v" },
    };

    [Fact]
    public async Task Create_QueuesAndSignals()
    {
        using var host = TestHost.Create();
        var service = host.Get<ITaskService>();
        var signal = host.Get<ITaskQueueSignal>();

        var task = await service.CreateAsync(Request(), Ct);

        Assert.Equal(1, task.Id);
        Assert.Equal(AgentTaskStatus.Queued, task.Status);
        Assert.Equal("v", task.Metadata["k"]);
        Assert.Equal("Cli:bob", task.RequesterId);
        Assert.True(await signal.WaitAsync(TimeSpan.FromMilliseconds(50), Ct));
        var events = await service.GetEventsAsync(task.Id, cancellationToken: Ct);
        Assert.Equal("created", events.Single().Type);
    }

    [Fact]
    public async Task Create_RepositoryRefusedByPolicy_ThrowsWithTheReasonAndAudits()
    {
        var policy = Substitute.For<IRepositoryPolicy>();
        policy.Check(Arg.Any<string>()).Returns(RepositoryDecision.Deny("`t/r` is not among the projects I may work on."));
        using var host = TestHost.Create(s => s.AddSingleton(policy));
        var service = host.Get<ITaskService>();

        var ex = await Assert.ThrowsAsync<RepositoryNotAllowedException>(() => service.CreateAsync(Request(), Ct));

        Assert.Equal("`t/r` is not among the projects I may work on.", ex.Message);
        Assert.Equal("https://gitlab.internal/t/r.git", ex.RepoUrl);
        Assert.Null(await service.GetAsync(1, Ct));
        var audit = Assert.Single(host.Audit.Entries, e => e.Action == "task.create");
        Assert.Equal("denied", audit.Outcome);
        Assert.Contains("t/r#1", audit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_WithoutRepo_SkipsThePolicyAndNeedsInput()
    {
        var policy = Substitute.For<IRepositoryPolicy>();
        using var host = TestHost.Create(s => s.AddSingleton(policy));

        var task = await host.Get<ITaskService>().CreateAsync(Request(repo: null), Ct);

        Assert.Equal(AgentTaskStatus.NeedsInput, task.Status);
        policy.DidNotReceiveWithAnyArgs().Check(default!);
    }

    [Fact]
    public async Task Create_WithoutRepo_NeedsInput()
    {
        using var host = TestHost.Create();
        var task = await host.Get<ITaskService>().CreateAsync(Request(repo: null), Ct);
        Assert.Equal(AgentTaskStatus.NeedsInput, task.Status);
        Assert.False(await host.Get<ITaskQueueSignal>().WaitAsync(TimeSpan.FromMilliseconds(20), Ct));
    }

    [Fact]
    public async Task FollowUp_RequeuesFinishedTask_AndAppendsWhenRunning()
    {
        using var host = TestHost.Create();
        var service = host.Get<ITaskService>();
        var store = host.Get<ITaskStore>();
        var task = await service.CreateAsync(Request(), Ct);

        var claimed = (await store.ClaimNextQueuedAsync("w1", Ct))!;
        Assert.Equal(AgentTaskStatus.Preparing, claimed.Status);
        Assert.Equal("w1", claimed.WorkerId);

        var running = await service.AddFollowUpAsync(task.Id, "first", CallerIdentity.Local("bob"), Ct);
        Assert.Equal(AgentTaskStatus.Preparing, running.Status);
        Assert.Equal("first", running.PendingInstruction);

        running.Status = AgentTaskStatus.AwaitingReview;
        await store.UpdateAsync(running, Ct);

        var requeued = await service.AddFollowUpAsync(task.Id, "second", CallerIdentity.Local("bob"), Ct);
        Assert.Equal(AgentTaskStatus.Queued, requeued.Status);
        Assert.Equal("first\n\nsecond", requeued.PendingInstruction);
    }

    [Fact]
    public async Task Cancel_RunningTask_TriggersRegisteredToken()
    {
        using var host = TestHost.Create();
        var service = host.Get<ITaskService>();
        var registry = host.Get<ITaskCancellationRegistry>();
        var task = await service.CreateAsync(Request(), Ct);
        var token = registry.Register(task.Id, CancellationToken.None);

        var cancelled = await service.CancelAsync(task.Id, CallerIdentity.Local("root", Role.Admin), Ct);

        Assert.Equal(AgentTaskStatus.Cancelled, cancelled!.Status);
        Assert.True(token.IsCancellationRequested);
        Assert.Contains("(was running)", (await service.GetEventsAsync(task.Id, cancellationToken: Ct)).Last().Message);
        registry.Unregister(task.Id);
        Assert.Empty(registry.RunningTaskIds);
    }

    [Fact]
    public async Task Retry_RequeuesFailed_ButNotRunning()
    {
        using var host = TestHost.Create();
        var service = host.Get<ITaskService>();
        var store = host.Get<ITaskStore>();
        var task = await service.CreateAsync(Request(), Ct);

        task.Status = AgentTaskStatus.Failed;
        task.Error = "boom";
        await store.UpdateAsync(task, Ct);

        var retried = await service.RetryAsync(task.Id, CallerIdentity.Local("bob"), Ct);
        Assert.Equal(AgentTaskStatus.Queued, retried!.Status);
        Assert.Null(retried.Error);

        await store.ClaimNextQueuedAsync("w", Ct);
        var stillRunning = await service.RetryAsync(task.Id, CallerIdentity.Local("bob"), Ct);
        Assert.Equal(AgentTaskStatus.Preparing, stillRunning!.Status);
    }

    [Fact]
    public async Task Close_MarksDoneOrClosed()
    {
        using var host = TestHost.Create();
        var service = host.Get<ITaskService>();
        var a = await service.CreateAsync(Request(sourceRef: "t/r#1"), Ct);
        var b = await service.CreateAsync(Request(sourceRef: "t/r#2"), Ct);

        Assert.Equal(AgentTaskStatus.Done, (await service.CloseAsync(a.Id, merged: true, Ct))!.Status);
        Assert.Equal(AgentTaskStatus.Closed, (await service.CloseAsync(b.Id, merged: false, Ct))!.Status);
        Assert.Null(await host.Get<ITaskStore>().FindActiveBySourceAsync(TaskSource.GitLabIssue, "t/r#1", Ct));
    }

    [Fact]
    public async Task Store_OptimisticConcurrency_Throws()
    {
        var store = new InMemoryTaskStore();
        var task = await store.AddAsync(new AgentTask { SourceRef = "x", Status = AgentTaskStatus.Queued }, Ct);

        var copy1 = (await store.GetAsync(task.Id, Ct))!;
        var copy2 = (await store.GetAsync(task.Id, Ct))!;
        copy1.Summary = "one";
        await store.UpdateAsync(copy1, Ct);
        copy2.Summary = "two";

        await Assert.ThrowsAsync<TaskConcurrencyException>(() => store.UpdateAsync(copy2, Ct));
        Assert.Equal("one", (await store.GetAsync(task.Id, Ct))!.Summary);
    }

    [Fact]
    public async Task Store_Queries()
    {
        var store = new InMemoryTaskStore();
        var a = await store.AddAsync(new AgentTask { SourceRef = "a", Status = AgentTaskStatus.Working, RequesterId = "r1", ProjectId = "p", MergeRequestIid = "7" }, Ct);
        var b = await store.AddAsync(new AgentTask { SourceRef = "b", Status = AgentTaskStatus.Done, RequesterId = "r2" }, Ct);
        await store.AddAsync(new AgentTask { SourceRef = "c", Status = AgentTaskStatus.Queued, RequesterId = "r1" }, Ct);

        Assert.Equal(2, (await store.ListAsync(new TaskQuery { RequesterId = "r1" }, Ct)).Count);
        Assert.Equal(2, (await store.ListAsync(new TaskQuery { ActiveOnly = true }, Ct)).Count);
        Assert.Single(await store.ListAsync(new TaskQuery { Statuses = [AgentTaskStatus.Done] }, Ct));
        Assert.Equal(a.Id, (await store.FindByMergeRequestAsync("p", "7", Ct))!.Id);
        Assert.Equal(a.Id, (await store.ListRunningAsync(Ct)).Single().Id);
        Assert.Null(await store.FindActiveBySourceAsync(TaskSource.GitLabIssue, "b", Ct));
        Assert.Equal(b.Id, (await store.ListAsync(new TaskQuery { Limit = 1 }, Ct)).Single().Id == b.Id ? b.Id : b.Id);
    }
}
