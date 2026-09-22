using Agent.Core.Channels;
using Agent.Core.Events;
using Agent.Core.Pipeline;
using Agent.Core.Replies;
using Agent.Core.Tasks;
using Agent.Core.Tests.Support;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Agent.Core.Tests.Pipeline;

public sealed class InboundProcessorTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static TaskContext GitLabTask(string sourceRef = "team/repo#5") => new()
    {
        Source = TaskSource.GitLabIssue,
        SourceRef = sourceRef,
        SourceUrl = "https://gitlab.internal/team/repo/-/issues/5",
        RepoUrl = "https://gitlab.internal/team/repo.git",
        ProjectId = "42",
        BaseBranch = "main",
        Title = "Add caching",
    };

    [Fact]
    public async Task Question_IsAnswered_WithHistory_AndAcked()
    {
        using var host = TestHost.Create();
        host.Context.History.Add(new ChatMessage(ChatRole.User, "earlier") { AuthorName = "alice" });
        host.Llm.Client.Reply("hi there");

        await host.Get<IInboundProcessor>().ProcessAsync(host.Event("hello"), Ct);

        Assert.Equal("hi there", host.Replies.Last);
        Assert.Equal([AckState.Working, AckState.Done], host.Replies.Acks.Select(a => a.State));
        var messages = host.Llm.Client.Calls.Single().Messages;
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal("earlier", messages[1].Text);
        Assert.Equal("hello", messages[^1].Text);
    }

    [Fact]
    public async Task DuplicateEvent_IsProcessedOnce()
    {
        using var host = TestHost.Create();
        host.Llm.Client.Reply("once");
        var evt = host.Event("hello", eventId: "same");

        await host.Get<IInboundProcessor>().ProcessAsync(evt, Ct);
        await host.Get<IInboundProcessor>().ProcessAsync(evt, Ct);

        Assert.Single(host.Replies.Sent);
    }

    [Fact]
    public async Task UnauthorizedInPublicChannel_IsSilent()
    {
        using var host = TestHost.Create();
        await host.Get<IInboundProcessor>().ProcessAsync(host.Event("hello", "nobody"), Ct);
        Assert.Empty(host.Replies.Sent);
        Assert.Contains(host.Audit.Entries, e => e.Outcome == "deny" && e.Action == "ask");
    }

    [Fact]
    public async Task UnauthorizedInPrivate_GetsRefusal()
    {
        using var host = TestHost.Create();
        await host.Get<IInboundProcessor>().ProcessAsync(host.Event("hello", "nobody", isPrivate: true), Ct);
        Assert.Contains("not authorised", host.Replies.Last);
    }

    [Fact]
    public async Task Command_IsDispatched()
    {
        using var host = TestHost.Create();
        await host.Get<IInboundProcessor>().ProcessAsync(host.Event("!ping"), Ct);
        Assert.Equal("pong", host.Replies.Last);
        Assert.Empty(host.Llm.Client.Calls);
    }

    [Fact]
    public async Task EmptyQuestion_GetsGreeting()
    {
        using var host = TestHost.Create();
        await host.Get<IInboundProcessor>().ProcessAsync(host.Event("   "), Ct);
        Assert.Contains("!help", host.Replies.Last);
    }

    [Fact]
    public async Task TaskRequest_ByTeam_CreatesQueuedTask()
    {
        using var host = TestHost.Create(channel: Channel.GitLab);
        var evt = host.Event("Add caching\n\nPlease add a cache.", "bob", InboundKind.TaskRequest, task: GitLabTask(), metadata: new Dictionary<string, string> { ["project_id"] = "42" });

        await host.Get<IInboundProcessor>().ProcessAsync(evt, Ct);

        Assert.Contains("task #1", host.Replies.Last);
        var task = await host.Get<ITaskService>().GetAsync(1, Ct);
        Assert.NotNull(task);
        Assert.Equal(AgentTaskStatus.Queued, task.Status);
        Assert.Equal("GitLab:bob", task.RequesterId);
        Assert.Equal("42", task.Metadata["project_id"]);
        Assert.Equal(AckState.Working, host.Replies.Acks.Single().State);
    }

    [Fact]
    public async Task TaskRequest_ByUsersRole_IsDenied()
    {
        using var host = TestHost.Create(channel: Channel.GitLab);
        await host.Get<IInboundProcessor>().ProcessAsync(host.Event("do it", "alice", InboundKind.TaskRequest, task: GitLabTask()), Ct);

        Assert.Empty(host.Replies.Sent);
        Assert.Null(await host.Get<ITaskService>().GetAsync(1, Ct));
    }

    [Fact]
    public async Task TaskRequest_Duplicate_PointsAtExistingTask()
    {
        using var host = TestHost.Create(channel: Channel.GitLab);
        var processor = host.Get<IInboundProcessor>();
        await processor.ProcessAsync(host.Event("do it", "bob", InboundKind.TaskRequest, task: GitLabTask()), Ct);
        await processor.ProcessAsync(host.Event("do it again", "bob", InboundKind.TaskRequest, task: GitLabTask()), Ct);

        Assert.Contains("already Queued", host.Replies.Last);
        Assert.Null(await host.Get<ITaskService>().GetAsync(2, Ct));
    }

    [Fact]
    public async Task TaskRequest_WithoutRepo_UsesResolver()
    {
        var resolver = Substitute.For<IRepositoryResolver>();
        resolver.ResolveAsync(Arg.Any<InboundEvent>(), Arg.Any<CancellationToken>()).Returns(new RepoTarget("https://gitlab.internal/x/y.git", "x/y", "develop"));
        using var host = TestHost.Create(s => s.AddSingleton(resolver), channel: Channel.Jira);

        var task = new TaskContext { Source = TaskSource.JiraIssue, SourceRef = "PROJ-9", Title = "Thing" };
        await host.Get<IInboundProcessor>().ProcessAsync(host.Event("Thing\n\nDo it", "bob", InboundKind.TaskRequest, task: task), Ct);

        var created = await host.Get<ITaskService>().GetAsync(1, Ct);
        Assert.NotNull(created);
        Assert.Equal("https://gitlab.internal/x/y.git", created.RepoUrl);
        Assert.Equal("develop", created.BaseBranch);
        Assert.Equal(AgentTaskStatus.Queued, created.Status);
    }

    [Fact]
    public async Task TaskRequest_Unresolvable_BecomesNeedsInput()
    {
        using var host = TestHost.Create(channel: Channel.Jira);
        var task = new TaskContext { Source = TaskSource.JiraIssue, SourceRef = "PROJ-9" };
        await host.Get<IInboundProcessor>().ProcessAsync(host.Event("Do it", "bob", InboundKind.TaskRequest, task: task), Ct);

        Assert.Contains("could not resolve a repository", host.Replies.Last);
        Assert.Equal(AgentTaskStatus.NeedsInput, (await host.Get<ITaskService>().GetAsync(1, Ct))!.Status);
    }

    [Fact]
    public async Task TaskRequest_RepositoryRefusedByPolicy_RepliesWithTheReasonAndCreatesNothing()
    {
        var policy = Substitute.For<IRepositoryPolicy>();
        policy.Check("https://gitlab.internal/team/repo.git").Returns(RepositoryDecision.Deny("`team/repo` is not among the projects I may work on."));
        using var host = TestHost.Create(s => s.AddSingleton(policy), channel: Channel.GitLab);

        await host.Get<IInboundProcessor>().ProcessAsync(host.Event("do it", "bob", InboundKind.TaskRequest, task: GitLabTask()), Ct);

        Assert.Equal("`team/repo` is not among the projects I may work on.", host.Replies.Last);
        Assert.Null(await host.Get<ITaskService>().GetAsync(1, Ct));
        Assert.Equal(AckState.Failed, host.Replies.Acks.Last().State);
    }

    [Fact]
    public async Task FollowUp_NamingARefusedRepository_RepliesWithTheReasonAndLeavesTheTaskWaiting()
    {
        var policy = Substitute.For<IRepositoryPolicy>();
        policy.Check(Arg.Any<string>()).Returns(RepositoryDecision.Deny("I only work on repositories hosted on `gitlab.internal`, not `github.com`."));
        using var host = TestHost.Create(s => s.AddSingleton(policy), channel: Channel.Jira);
        var processor = host.Get<IInboundProcessor>();
        await processor.ProcessAsync(host.Event("Do it", "bob", InboundKind.TaskRequest, task: new TaskContext { Source = TaskSource.JiraIssue, SourceRef = "PROJ-9" }), Ct);

        await processor.ProcessAsync(host.Event("use https://github.com/team/repo.git please", "bob", InboundKind.FollowUp, task: new TaskContext { Source = TaskSource.JiraIssue, SourceRef = "PROJ-9" }), Ct);

        Assert.Equal("I only work on repositories hosted on `gitlab.internal`, not `github.com`.", host.Replies.Last);
        var task = (await host.Get<ITaskStore>().GetAsync(1, Ct))!;
        Assert.Null(task.RepoUrl);
        Assert.Equal(AgentTaskStatus.NeedsInput, task.Status);
        Assert.Null(task.PendingInstruction);
    }

    [Fact]
    public async Task FollowUp_OnKnownTask_QueuesInstruction()
    {
        using var host = TestHost.Create(channel: Channel.GitLab);
        var processor = host.Get<IInboundProcessor>();
        await processor.ProcessAsync(host.Event("do it", "bob", InboundKind.TaskRequest, task: GitLabTask()), Ct);

        var store = host.Get<ITaskStore>();
        var task = (await store.GetAsync(1, Ct))!;
        task.Status = AgentTaskStatus.AwaitingReview;
        await store.UpdateAsync(task, Ct);

        var follow = new TaskContext { Source = TaskSource.MergeRequest, SourceRef = "team/repo!3", ExistingTaskId = 1 };
        await processor.ProcessAsync(host.Event("also add tests", "bob", InboundKind.FollowUp, task: follow), Ct);

        Assert.Contains("queued", host.Replies.Last);
        var updated = (await store.GetAsync(1, Ct))!;
        Assert.Equal(AgentTaskStatus.Queued, updated.Status);
        Assert.Equal("also add tests", updated.PendingInstruction);
    }

    [Fact]
    public async Task FollowUp_WithRepoUrl_FillsNeedsInputTask()
    {
        using var host = TestHost.Create(channel: Channel.Jira);
        var processor = host.Get<IInboundProcessor>();
        await processor.ProcessAsync(host.Event("Do it", "bob", InboundKind.TaskRequest, task: new TaskContext { Source = TaskSource.JiraIssue, SourceRef = "PROJ-9" }), Ct);

        var follow = new TaskContext { Source = TaskSource.JiraIssue, SourceRef = "PROJ-9" };
        await processor.ProcessAsync(host.Event("use https://gitlab.internal/team/repo.git please", "bob", InboundKind.FollowUp, task: follow), Ct);

        var task = (await host.Get<ITaskStore>().GetAsync(1, Ct))!;
        Assert.Equal("https://gitlab.internal/team/repo.git", task.RepoUrl);
        Assert.Equal(AgentTaskStatus.Queued, task.Status);
    }

    [Fact]
    public async Task FollowUp_OnUntrackedMergeRequest_StartsTaskOnThatBranch()
    {
        using var host = TestHost.Create(channel: Channel.GitLab);
        var follow = new TaskContext
        {
            Source = TaskSource.MergeRequest,
            SourceRef = "team/repo!12",
            ProjectId = "42",
            RepoUrl = "https://gitlab.internal/team/repo.git",
            BaseBranch = "main",
            ExistingBranch = "feature/x",
            MergeRequestIid = "12",
            MergeRequestUrl = "https://gitlab.internal/team/repo/-/merge_requests/12",
        };

        await host.Get<IInboundProcessor>().ProcessAsync(host.Event("please fix the failing test", "bob", InboundKind.FollowUp, task: follow), Ct);

        Assert.Contains("task #1", host.Replies.Last);
        var task = (await host.Get<ITaskStore>().GetAsync(1, Ct))!;
        Assert.Equal(AgentTaskStatus.Queued, task.Status);
        Assert.Equal("feature/x", task.WorkBranch);
        Assert.Equal("12", task.MergeRequestIid);
        Assert.Equal(TaskSource.MergeRequest, task.Source);
    }

    [Fact]
    public async Task FollowUp_WithoutTask_FallsBackToAnswer()
    {
        using var host = TestHost.Create(channel: Channel.GitLab);
        host.Llm.Client.Reply("answer instead");
        var follow = new TaskContext { Source = TaskSource.MergeRequest, SourceRef = "team/repo!99" };

        await host.Get<IInboundProcessor>().ProcessAsync(host.Event("what is this?", "bob", InboundKind.FollowUp, task: follow), Ct);

        Assert.Equal("answer instead", host.Replies.Last);
    }

    [Fact]
    public async Task LlmFailure_ReportsReference_AndFailedAck()
    {
        using var host = TestHost.Create();
        host.Llm.Client.Reply((_, _) => throw new HttpRequestException("llm down"));

        await host.Get<IInboundProcessor>().ProcessAsync(host.Event("hello"), Ct);

        Assert.Matches(@"ref [0-9a-f]{6}", host.Replies.Last);
        Assert.Contains(host.Replies.Acks, a => a.State == AckState.Failed);
    }

    [Theory]
    [InlineData("see https://gitlab.internal/team/repo.git thanks", "https://gitlab.internal/team/repo.git")]
    [InlineData("<https://gitlab.internal/team/repo>", "https://gitlab.internal/team/repo")]
    [InlineData("no url here", null)]
    [InlineData("https://gitlab.internal/", null)]
    public void TryExtractRepoUrl_FindsRepositoryLinks(string text, string? expected)
    {
        Assert.Equal(expected, InboundProcessor.TryExtractRepoUrl(text));
    }
}
