using Agent.Core.Channels;
using Agent.Core.Tasks;
using Agent.Persistence.Stores;

namespace Agent.Infrastructure.Tests.Persistence;

public sealed class EfTaskStoreTests : IDisposable
{
    private readonly SqliteDatabase _db = new();
    private readonly ITaskStore _store;

    public EfTaskStoreTests()
    {
        _store = _db.Get<ITaskStore>();
        Assert.IsType<EfTaskStore>(_store);
    }

    [Fact]
    public async Task AddAsync_ThenGetAsync_RoundTripsEveryFieldIncludingMetadata()
    {
        var task = NewTask(t =>
        {
            t.SourceUrl = "https://jira/browse/PROJ-1";
            t.Title = "Fix the thing";
            t.RequesterUsername = "alice";
            t.RepoUrl = "https://gitlab/team/repo.git";
            t.ProjectId = "team/repo";
            t.BaseBranch = "main";
            t.WorkBranch = "agent/PROJ-1";
            t.MergeRequestUrl = "https://gitlab/team/repo/-/merge_requests/7";
            t.MergeRequestIid = "7";
            t.PendingInstruction = "also add tests";
            t.Summary = "summary";
            t.Error = null;
            t.Turns = 3;
            t.TokensUsed = 1234;
            t.Attempts = 1;
            t.WorkerId = "worker-a";
            t.Metadata["Repo"] = "team/repo";
            t.Metadata["ci"] = "passed";
        });

        var added = await _store.AddAsync(task);

        Assert.True(added.Id > 0);
        Assert.Equal(1, added.Version);
        Assert.Equal(_db.Time.Now, added.CreatedAt);
        Assert.Equal(_db.Time.Now, added.UpdatedAt);

        var loaded = await _store.GetAsync(added.Id);

        Assert.NotNull(loaded);
        Assert.Equal(added.Id, loaded.Id);
        Assert.Equal(TaskSource.JiraIssue, loaded.Source);
        Assert.Equal("PROJ-1", loaded.SourceRef);
        Assert.Equal("https://jira/browse/PROJ-1", loaded.SourceUrl);
        Assert.Equal("Fix the thing", loaded.Title);
        Assert.Equal("Jira:alice", loaded.RequesterId);
        Assert.Equal("Alice", loaded.RequesterName);
        Assert.Equal("alice", loaded.RequesterUsername);
        Assert.Equal(Channel.Jira, loaded.NotifyChannel);
        Assert.Equal("PROJ-1", loaded.ConversationId);
        Assert.Equal("https://gitlab/team/repo.git", loaded.RepoUrl);
        Assert.Equal("team/repo", loaded.ProjectId);
        Assert.Equal("main", loaded.BaseBranch);
        Assert.Equal("agent/PROJ-1", loaded.WorkBranch);
        Assert.Equal("https://gitlab/team/repo/-/merge_requests/7", loaded.MergeRequestUrl);
        Assert.Equal("7", loaded.MergeRequestIid);
        Assert.Equal(AgentTaskStatus.Queued, loaded.Status);
        Assert.Equal("do the thing", loaded.Instruction);
        Assert.Equal("also add tests", loaded.PendingInstruction);
        Assert.Equal("summary", loaded.Summary);
        Assert.Null(loaded.Error);
        Assert.Equal(3, loaded.Turns);
        Assert.Equal(1234, loaded.TokensUsed);
        Assert.Equal(1, loaded.Attempts);
        Assert.Equal("worker-a", loaded.WorkerId);
        Assert.Equal(_db.Time.Now, loaded.CreatedAt);
        Assert.Equal(_db.Time.Now, loaded.UpdatedAt);
        Assert.Equal(1, loaded.Version);
        Assert.Equal("team/repo", loaded.Metadata["repo"]);
        Assert.Equal("passed", loaded.Metadata["CI"]);
        Assert.Equal(2, loaded.Metadata.Count);
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        Assert.Null(await _store.GetAsync(999));
    }

    [Fact]
    public async Task UpdateAsync_BumpsVersionAndPersistsChanges()
    {
        var task = await _store.AddAsync(NewTask());
        _db.Time.Advance(TimeSpan.FromMinutes(1));

        task.Status = AgentTaskStatus.Working;
        task.Turns = 5;
        task.Metadata["step"] = "2";
        await _store.UpdateAsync(task);

        Assert.Equal(2, task.Version);
        Assert.Equal(_db.Time.Now, task.UpdatedAt);

        var loaded = await _store.GetAsync(task.Id);
        Assert.NotNull(loaded);
        Assert.Equal(AgentTaskStatus.Working, loaded.Status);
        Assert.Equal(5, loaded.Turns);
        Assert.Equal("2", loaded.Metadata["step"]);
        Assert.Equal(2, loaded.Version);
        Assert.Equal(_db.Time.Now, loaded.UpdatedAt);
    }

    [Fact]
    public async Task UpdateAsync_StaleVersion_ThrowsTaskConcurrencyException()
    {
        var task = await _store.AddAsync(NewTask());
        var stale = (await _store.GetAsync(task.Id))!;

        task.Status = AgentTaskStatus.Working;
        await _store.UpdateAsync(task);

        stale.Status = AgentTaskStatus.Failed;
        var ex = await Assert.ThrowsAsync<TaskConcurrencyException>(() => _store.UpdateAsync(stale));
        Assert.Contains($"#{task.Id}", ex.Message);

        var loaded = await _store.GetAsync(task.Id);
        Assert.Equal(AgentTaskStatus.Working, loaded!.Status);
    }

    [Fact]
    public async Task UpdateAsync_UnknownTask_ThrowsKeyNotFound()
    {
        var ghost = NewTask();
        ghost.Id = 4242;
        ghost.Version = 1;

        await Assert.ThrowsAsync<KeyNotFoundException>(() => _store.UpdateAsync(ghost));
    }

    [Fact]
    public async Task ClaimNextQueuedAsync_ClaimsOldestQueuedThenReturnsNullWhenEmpty()
    {
        var first = await _store.AddAsync(NewTask(t => t.SourceRef = "PROJ-1"));
        _db.Time.Advance(TimeSpan.FromSeconds(10));
        var second = await _store.AddAsync(NewTask(t => t.SourceRef = "PROJ-2"));
        _db.Time.Advance(TimeSpan.FromSeconds(10));
        await _store.AddAsync(NewTask(t =>
        {
            t.SourceRef = "PROJ-3";
            t.Status = AgentTaskStatus.Received;
        }));

        var claimed = await _store.ClaimNextQueuedAsync("worker-1");

        Assert.NotNull(claimed);
        Assert.Equal(first.Id, claimed.Id);
        Assert.Equal(AgentTaskStatus.Preparing, claimed.Status);
        Assert.Equal("worker-1", claimed.WorkerId);
        Assert.Equal(1, claimed.Attempts);
        Assert.Equal(2, claimed.Version);

        var persisted = await _store.GetAsync(first.Id);
        Assert.Equal(AgentTaskStatus.Preparing, persisted!.Status);
        Assert.Equal(2, persisted.Version);

        var next = await _store.ClaimNextQueuedAsync("worker-2");
        Assert.Equal(second.Id, next!.Id);

        Assert.Null(await _store.ClaimNextQueuedAsync("worker-3"));
    }

    [Fact]
    public async Task ClaimNextQueuedAsync_NothingQueued_ReturnsNull()
    {
        await _store.AddAsync(NewTask(t => t.Status = AgentTaskStatus.Done));

        Assert.Null(await _store.ClaimNextQueuedAsync("worker-1"));
    }

    [Fact]
    public async Task FindActiveBySourceAsync_IgnoresFinishedTasksAndMatchesCaseInsensitively()
    {
        await _store.AddAsync(NewTask(t => t.Status = AgentTaskStatus.Done));
        Assert.Null(await _store.FindActiveBySourceAsync(TaskSource.JiraIssue, "PROJ-1"));

        _db.Time.Advance(TimeSpan.FromSeconds(1));
        var active = await _store.AddAsync(NewTask(t => t.Status = AgentTaskStatus.AwaitingReview));

        var found = await _store.FindActiveBySourceAsync(TaskSource.JiraIssue, "proj-1");
        Assert.Equal(active.Id, found!.Id);

        Assert.Null(await _store.FindActiveBySourceAsync(TaskSource.GitLabIssue, "PROJ-1"));
    }

    [Fact]
    public async Task FindByMergeRequestAsync_FindsFinishedTaskToo()
    {
        var task = await _store.AddAsync(NewTask(t =>
        {
            t.ProjectId = "Team/Repo";
            t.MergeRequestIid = "12";
            t.Status = AgentTaskStatus.Done;
        }));

        var found = await _store.FindByMergeRequestAsync("team/repo", "12");
        Assert.Equal(task.Id, found!.Id);

        Assert.Null(await _store.FindByMergeRequestAsync("team/repo", "13"));
        Assert.Null(await _store.FindByMergeRequestAsync("other/repo", "12"));
    }

    [Fact]
    public async Task ListAsync_FiltersByRequesterStatusesActiveOnlyAndLimit_NewestFirst()
    {
        var a = await _store.AddAsync(NewTask(t => t.Status = AgentTaskStatus.Queued));
        _db.Time.Advance(TimeSpan.FromSeconds(1));
        var b = await _store.AddAsync(NewTask(t => t.Status = AgentTaskStatus.Working));
        _db.Time.Advance(TimeSpan.FromSeconds(1));
        var c = await _store.AddAsync(NewTask(t => t.Status = AgentTaskStatus.Done));
        _db.Time.Advance(TimeSpan.FromSeconds(1));
        var other = await _store.AddAsync(NewTask(t =>
        {
            t.RequesterId = "Mattermost:bob";
            t.Status = AgentTaskStatus.Queued;
        }));

        var all = await _store.ListAsync(new TaskQuery());
        Assert.Equal([other.Id, c.Id, b.Id, a.Id], all.Select(t => t.Id));

        var alice = await _store.ListAsync(new TaskQuery { RequesterId = "jira:ALICE" });
        Assert.Equal([c.Id, b.Id, a.Id], alice.Select(t => t.Id));

        var active = await _store.ListAsync(new TaskQuery { ActiveOnly = true });
        Assert.Equal([other.Id, b.Id, a.Id], active.Select(t => t.Id));

        var byStatus = await _store.ListAsync(new TaskQuery { Statuses = [AgentTaskStatus.Working, AgentTaskStatus.Done] });
        Assert.Equal([c.Id, b.Id], byStatus.Select(t => t.Id));

        var limited = await _store.ListAsync(new TaskQuery { Limit = 2 });
        Assert.Equal([other.Id, c.Id], limited.Select(t => t.Id));
    }

    [Fact]
    public async Task ListRunningAsync_ReturnsOnlyRunningStatuses()
    {
        var preparing = await _store.AddAsync(NewTask(t => t.Status = AgentTaskStatus.Preparing));
        var working = await _store.AddAsync(NewTask(t => t.Status = AgentTaskStatus.Working));
        var verifying = await _store.AddAsync(NewTask(t => t.Status = AgentTaskStatus.Verifying));
        var publishing = await _store.AddAsync(NewTask(t => t.Status = AgentTaskStatus.Publishing));
        await _store.AddAsync(NewTask(t => t.Status = AgentTaskStatus.Queued));
        await _store.AddAsync(NewTask(t => t.Status = AgentTaskStatus.AwaitingReview));
        await _store.AddAsync(NewTask(t => t.Status = AgentTaskStatus.Failed));

        var running = await _store.ListRunningAsync();

        Assert.Equal([preparing.Id, working.Id, verifying.Id, publishing.Id], running.Select(t => t.Id));
    }

    [Fact]
    public async Task GetEventsAsync_ReturnsTheLastMaxEventsInInsertionOrder()
    {
        var task = await _store.AddAsync(NewTask());
        var otherTask = await _store.AddAsync(NewTask());

        for (var i = 1; i <= 5; i++)
        {
            await _store.AddEventAsync(new TaskEvent(task.Id, _db.Time.Now.AddSeconds(i), "status", $"event {i}"));
        }

        await _store.AddEventAsync(new TaskEvent(otherTask.Id, _db.Time.Now, "status", "other"));

        var all = await _store.GetEventsAsync(task.Id);
        Assert.Equal(["event 1", "event 2", "event 3", "event 4", "event 5"], all.Select(e => e.Message));
        Assert.All(all, e => Assert.Equal(task.Id, e.TaskId));
        Assert.All(all, e => Assert.True(e.Id > 0));
        Assert.Equal(_db.Time.Now.AddSeconds(1), all[0].At);

        var lastTwo = await _store.GetEventsAsync(task.Id, max: 2);
        Assert.Equal(["event 4", "event 5"], lastTwo.Select(e => e.Message));

        Assert.Empty(await _store.GetEventsAsync(999));
    }

    private static AgentTask NewTask(Action<AgentTask>? configure = null)
    {
        var task = new AgentTask
        {
            Source = TaskSource.JiraIssue,
            SourceRef = "PROJ-1",
            RequesterId = "Jira:alice",
            RequesterName = "Alice",
            NotifyChannel = Channel.Jira,
            ConversationId = "PROJ-1",
            Instruction = "do the thing",
            Status = AgentTaskStatus.Queued,
        };
        configure?.Invoke(task);
        return task;
    }

    public void Dispose() => _db.Dispose();
}
