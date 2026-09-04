using Agent.Core.Channels;
using Agent.Core.Tasks;
using Agent.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Agent.Coding.Tests;

public sealed class TaskWorkerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly InMemoryTaskStore _store = new();
    private readonly TaskQueueSignal _signal = new();
    private readonly ITaskRunner _runner = Substitute.For<ITaskRunner>();
    private readonly IWorkspaceManager _workspaces = Substitute.For<IWorkspaceManager>();

    public void Dispose() => _dir.Dispose();

    private TaskWorker Create(int maxConcurrent = 2)
        => new(_store, _signal, _runner, _workspaces, TestOptions.Monitor(TestOptions.Coding(_dir.Path, o => o.MaxConcurrentTasks = maxConcurrent)), NullLogger<TaskWorker>.Instance);

    private Task<AgentTask> AddAsync(AgentTaskStatus status) => _store.AddAsync(new AgentTask
    {
        Source = TaskSource.Cli,
        SourceRef = "cli-1",
        RequesterId = "Cli:alice",
        RequesterName = "alice",
        NotifyChannel = Channel.Cli,
        ConversationId = "cli",
        RepoUrl = "https://example.invalid/repo.git",
        Instruction = "do it",
        Status = status,
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ExecuteAsync_QueuedTask_IsClaimedAndRun()
    {
        var task = await AddAsync(AgentTaskStatus.Queued);
        var ran = new TaskCompletionSource<AgentTask>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.RunAsync(Arg.Do<AgentTask>(t => ran.TrySetResult(t)), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var worker = Create();

        await worker.StartAsync(TestContext.Current.CancellationToken);
        var claimed = await ran.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(task.Id, claimed.Id);
        Assert.Equal(AgentTaskStatus.Preparing, claimed.Status);
        Assert.Equal(worker.WorkerId, claimed.WorkerId);
        Assert.Equal(1, claimed.Attempts);
    }

    [Fact]
    public async Task ExecuteAsync_TaskQueuedLater_IsPickedUpOnSignal()
    {
        var ran = new TaskCompletionSource<AgentTask>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.RunAsync(Arg.Do<AgentTask>(t => ran.TrySetResult(t)), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var worker = Create();
        await worker.StartAsync(TestContext.Current.CancellationToken);

        var task = await AddAsync(AgentTaskStatus.Queued);
        _signal.Signal();
        var claimed = await ran.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(task.Id, claimed.Id);
    }

    [Fact]
    public async Task ExecuteAsync_RunningTaskFromPreviousProcess_IsRequeuedAndRun()
    {
        var stuck = await AddAsync(AgentTaskStatus.Working);
        var ran = new TaskCompletionSource<AgentTask>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.RunAsync(Arg.Do<AgentTask>(t => ran.TrySetResult(t)), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var worker = Create();

        await worker.StartAsync(TestContext.Current.CancellationToken);
        var claimed = await ran.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(stuck.Id, claimed.Id);
        var events = await _store.GetEventsAsync(stuck.Id, 50, TestContext.Current.CancellationToken);
        Assert.Contains(events, e => e.Type == "interrupted");
    }

    [Fact]
    public async Task ExecuteAsync_RespectsMaxConcurrentTasks()
    {
        await AddAsync(AgentTaskStatus.Queued);
        await AddAsync(AgentTaskStatus.Queued);
        var started = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.RunAsync(Arg.Any<AgentTask>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            Interlocked.Increment(ref started);
            firstStarted.TrySetResult();
            await release.Task;
        });
        var worker = Create(maxConcurrent: 1);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _signal.Signal();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(1, started);
        Assert.Single(worker.RunningTaskIds);
        var status = await worker.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Contains("running 1/1", status, StringComparison.Ordinal);

        release.SetResult();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_RunnerThrows_WorkerKeepsGoing()
    {
        await AddAsync(AgentTaskStatus.Queued);
        var second = await AddAsync(AgentTaskStatus.Queued);
        var ran = new List<int>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.RunAsync(Arg.Any<AgentTask>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            lock (ran)
            {
                ran.Add(ci.Arg<AgentTask>().Id);
                if (ran.Count == 2)
                {
                    both.TrySetResult();
                }
            }

            return ci.Arg<AgentTask>().Id == second.Id ? Task.CompletedTask : Task.FromException(new InvalidOperationException("boom"));
        });
        var worker = Create(maxConcurrent: 1);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await both.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, ran.Count);
        Assert.True(worker.Completed >= 1);
    }
}
