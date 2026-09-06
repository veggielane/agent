using Agent.Channels.GitLab;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Commands;
using Agent.Core.Events;
using Agent.Core.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Agent.Channels.GitLab.Tests;

public sealed class GitLabFixCommandTests : IDisposable
{
    private static readonly object Alice = Payloads.UserRef(5, "alice");

    private readonly GitLabTestServer _gitlab = new();
    private readonly ITaskService _taskService = Substitute.For<ITaskService>();
    private readonly ITaskStore _store = Substitute.For<ITaskStore>();
    private readonly GitLabOptions _options = TestOptions.Default();
    private readonly CommandDescriptor _command;
    private readonly IServiceProvider _services;

    private TaskRequest? _request;

    public GitLabFixCommandTests()
    {
        _store.FindActiveBySourceAsync(Arg.Any<TaskSource>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((AgentTask?)null);
        _taskService.CreateAsync(Arg.Any<TaskRequest>(), Arg.Any<CancellationToken>()).Returns(ci => Created(ci.Arg<TaskRequest>()));

        _services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IGitLabClient>(_gitlab.Client)
            .AddSingleton(_taskService)
            .AddSingleton(_store)
            .AddSingleton<IOptionsMonitor<GitLabOptions>>(TestOptions.Monitor(_options))
            .BuildServiceProvider();

        _command = AttributedCommandSource.FromType(typeof(GitLabFixCommand), _services).Single();

        _gitlab.Get("/api/v4/projects/team/repo", Payloads.Project(42, "team/repo"));
        _gitlab.Get("/api/v4/projects/42/issues/12", Payloads.Issue(12, 42, "team/repo", "Fix login", "Users cannot log in after the SSO change.", Alice, "2026-09-04T08:00:00.000Z"));
        _gitlab.Get("/api/v4/projects/42/merge_requests/7", Payloads.MergeRequest(7, 42, "team/repo", "Add feature", "opened", "agent/12-fix", "main", Alice, description: "Half done."));
    }

    public void Dispose() => _gitlab.Dispose();

    private AgentTask Created(TaskRequest request)
    {
        _request = request;
        return new AgentTask
        {
            Id = 12,
            Source = request.Source,
            SourceRef = request.SourceRef,
            Title = request.Title,
            Instruction = request.Instruction,
            ConversationId = request.ConversationId,
            NotifyChannel = request.NotifyChannel,
            Status = string.IsNullOrWhiteSpace(request.RepoUrl) && string.IsNullOrWhiteSpace(request.ProjectId)
                ? AgentTaskStatus.NeedsInput
                : AgentTaskStatus.Queued,
        };
    }

    private Task<CommandResult> RunAsync(string args, Channel channel = Channel.Mattermost, string conversation = "thread-99", InboundEvent? evt = null, Role role = Role.Team)
    {
        var ctx = new CommandContext
        {
            Caller = CallerIdentity.Local("alice", role),
            Channel = channel,
            ConversationId = conversation,
            Event = evt,
            Services = _services,
            CancellationToken = TestContext.Current.CancellationToken,
            RawArgs = args,
            CommandName = "fix",
        };
        return _command.Invoke(ctx, CommandBinder.Parse(_command, args));
    }

    private static InboundEvent Event(Channel channel, string conversation, TaskContext? task) => new()
    {
        Channel = channel,
        EventId = "evt-1",
        Caller = CallerIdentity.Local("alice", Role.Team),
        ConversationId = conversation,
        Text = "!fix",
        Task = task,
    };

    [Fact]
    public void Descriptor_IsFixWithImplementAlias_AndRequiresTeam()
    {
        Assert.Equal("fix", _command.Name);
        Assert.Equal(["implement"], _command.Aliases);
        Assert.Equal(Role.Team, _command.Role);
        Assert.Equal("!fix [target] [instruction]...", _command.Usage());
        Assert.Null(_command.Channels); // usable from Mattermost, which is the point of the command
    }

    [Fact]
    public async Task Fix_IssueReference_UsesTheIssuesOwnTitleAndDescription()
    {
        var result = await RunAsync("team/repo#12");

        Assert.False(result.IsError);
        Assert.NotNull(_request);
        Assert.Equal(TaskSource.GitLabIssue, _request.Source);
        Assert.Equal("team/repo#12", _request.SourceRef);
        Assert.Equal("https://gitlab.test/team/repo/-/issues/12", _request.SourceUrl);
        Assert.Equal("Fix login", _request.Title);
        Assert.Equal("Fix login\n\nUsers cannot log in after the SSO change.", _request.Instruction);
        Assert.Equal("https://gitlab.test/team/repo.git", _request.RepoUrl);
        Assert.Equal("42", _request.ProjectId);
        Assert.Equal("main", _request.BaseBranch);
        Assert.Equal("alice", _request.Requester.Username);
        Assert.Contains("**Task #12**", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("Fix login", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fix_IssueReferenceWithExtraInstruction_AppendsIt()
    {
        await RunAsync("team/repo#12 keep the API unchanged");

        Assert.Equal(
            "Fix login\n\nUsers cannot log in after the SSO change.\n\nAdditional instruction from alice: keep the API unchanged",
            _request!.Instruction);
    }

    [Fact]
    public async Task Fix_MergeRequestReference_ContinuesOnTheExistingBranch()
    {
        await RunAsync("team/repo!7 make the tests pass");

        Assert.Equal(TaskSource.MergeRequest, _request!.Source);
        Assert.Equal("team/repo!7", _request.SourceRef);
        Assert.Equal("agent/12-fix", _request.ExistingBranch);
        Assert.Equal("main", _request.BaseBranch);
        Assert.Equal("7", _request.MergeRequestIid);
        Assert.Equal("https://gitlab.test/team/repo/-/merge_requests/7", _request.MergeRequestUrl);
        Assert.Contains("Additional instruction from alice: make the tests pass", _request.Instruction, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://gitlab.test/team/repo/-/issues/12")]
    [InlineData("https://gitlab.test/team/repo/-/issues/12#note_44")]
    public async Task Fix_IssueUrl_ResolvesTheSameIssue(string url)
    {
        var result = await RunAsync(url);

        Assert.False(result.IsError);
        Assert.Equal("team/repo#12", _request!.SourceRef);
        Assert.Equal("Fix login", _request.Title);
    }

    [Fact]
    public async Task Fix_BareRepository_TakesTheConversationAsItsSource()
    {
        var result = await RunAsync("team/repo add a health endpoint", Channel.Mattermost, "thread-99");

        Assert.False(result.IsError);
        Assert.Equal(TaskSource.Mattermost, _request!.Source);
        Assert.Equal("thread-99", _request.SourceRef);
        Assert.Equal("thread-99", _request.ConversationId);
        Assert.Equal(Channel.Mattermost, _request.NotifyChannel);
        Assert.Equal("add a health endpoint", _request.Instruction);
        Assert.Equal("add a health endpoint", _request.Title);
        Assert.Equal("https://gitlab.test/team/repo.git", _request.RepoUrl);
        Assert.Equal("main", _request.BaseBranch);
    }

    [Fact]
    public async Task Fix_BareRepositoryWithoutInstruction_ExplainsWhatIsMissing()
    {
        var result = await RunAsync("team/repo");

        Assert.True(result.IsError);
        Assert.Contains("Say what you want done in `team/repo`", result.Markdown, StringComparison.Ordinal);
        await _taskService.DidNotReceive().CreateAsync(Arg.Any<TaskRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fix_OnAGitLabIssueWithoutATarget_UsesTheConversation()
    {
        var result = await RunAsync(string.Empty, Channel.GitLab, "team/repo#12");

        Assert.False(result.IsError);
        Assert.Equal("team/repo#12", _request!.SourceRef);
        Assert.Equal(Channel.GitLab, _request.NotifyChannel);
        Assert.Equal("team/repo#12", _request.ConversationId);
        Assert.Equal("Fix login\n\nUsers cannot log in after the SSO change.", _request.Instruction);
    }

    [Fact]
    public async Task Fix_OnAGitLabIssueWithWordsThatAreNotAReference_KeepsThemAsTheInstruction()
    {
        var result = await RunAsync("please make the login test pass", Channel.GitLab, "team/repo#12");

        Assert.False(result.IsError);
        Assert.Equal("team/repo#12", _request!.SourceRef);
        Assert.Contains("Additional instruction from alice: please make the login test pass", _request.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fix_OnAJiraEventWithTaskContext_UsesThatContext()
    {
        var context = new TaskContext
        {
            Source = TaskSource.JiraIssue,
            SourceRef = "PROJ-42",
            SourceUrl = "https://jira.test/browse/PROJ-42",
            RepoUrl = "https://gitlab.test/team/repo.git",
            ProjectId = "42",
            BaseBranch = "main",
            Title = "Login broken",
        };

        var result = await RunAsync("also add a regression test", Channel.Jira, "PROJ-42", Event(Channel.Jira, "PROJ-42", context));

        Assert.False(result.IsError);
        Assert.Equal(TaskSource.JiraIssue, _request!.Source);
        Assert.Equal("PROJ-42", _request.SourceRef);
        Assert.Equal("https://jira.test/browse/PROJ-42", _request.SourceUrl);
        Assert.Equal("Login broken", _request.Title);
        Assert.Equal("Login broken\n\nAdditional instruction from alice: also add a regression test", _request.Instruction);
        Assert.Equal(Channel.Jira, _request.NotifyChannel);
        Assert.Equal("https://gitlab.test/team/repo.git", _request.RepoUrl);
        Assert.Empty(_gitlab.Requests("GET", "/api/v4/projects/"));
    }

    [Fact]
    public async Task Fix_OnAGitLabMergeRequestFollowUp_ResolvesTheMergeRequest()
    {
        var context = new TaskContext
        {
            Source = TaskSource.MergeRequest,
            SourceRef = "team/repo!7",
            ProjectId = "42",
        };

        var result = await RunAsync("rerun the migration", Channel.GitLab, "team/repo!7", Event(Channel.GitLab, "team/repo!7", context));

        Assert.False(result.IsError);
        Assert.Equal(TaskSource.MergeRequest, _request!.Source);
        Assert.Equal("team/repo!7", _request.SourceRef);
        Assert.Equal("agent/12-fix", _request.ExistingBranch);
    }

    [Fact]
    public async Task Fix_OnAThreadThatAlreadyHasATask_PointsAtIt()
    {
        var context = new TaskContext { Source = TaskSource.MergeRequest, SourceRef = "team/repo!7", ProjectId = "42", ExistingTaskId = 4 };
        _store.GetAsync(4, Arg.Any<CancellationToken>()).Returns(new AgentTask { Id = 4, SourceRef = "team/repo#12", Status = AgentTaskStatus.AwaitingReview });

        var result = await RunAsync("also bump the version", Channel.GitLab, "team/repo!7", Event(Channel.GitLab, "team/repo!7", context));

        Assert.True(result.IsError);
        Assert.Contains("**#4**", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("team/repo#12", result.Markdown, StringComparison.Ordinal);
        await _taskService.DidNotReceive().CreateAsync(Arg.Any<TaskRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fix_OnAThreadWhoseTaskIsFinished_StartsANewOne()
    {
        var context = new TaskContext { Source = TaskSource.MergeRequest, SourceRef = "team/repo!7", ProjectId = "42", ExistingTaskId = 4 };
        _store.GetAsync(4, Arg.Any<CancellationToken>()).Returns(new AgentTask { Id = 4, SourceRef = "team/repo#12", Status = AgentTaskStatus.Done });

        var result = await RunAsync("reopen this", Channel.GitLab, "team/repo!7", Event(Channel.GitLab, "team/repo!7", context));

        Assert.False(result.IsError);
        Assert.Equal("team/repo!7", _request!.SourceRef);
    }

    [Fact]
    public async Task Fix_WithoutTargetOrContext_ListsTheAcceptedForms()
    {
        var result = await RunAsync("make the build green", Channel.Mattermost, "thread-99");

        Assert.True(result.IsError);
        Assert.Contains("group/repo#123", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("group/repo!45", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("issue or merge request URL", result.Markdown, StringComparison.Ordinal);
        await _taskService.DidNotReceive().CreateAsync(Arg.Any<TaskRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fix_WhenAnActiveTaskAlreadyExists_PointsAtItInsteadOfStartingAnother()
    {
        _store.FindActiveBySourceAsync(TaskSource.GitLabIssue, "team/repo#12", Arg.Any<CancellationToken>())
            .Returns(new AgentTask { Id = 3, SourceRef = "team/repo#12", Status = AgentTaskStatus.Working });

        var result = await RunAsync("team/repo#12");

        Assert.True(result.IsError);
        Assert.Contains("**#3**", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("team/repo#12", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("!cancel 3", result.Markdown, StringComparison.Ordinal);
        await _taskService.DidNotReceive().CreateAsync(Arg.Any<TaskRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fix_UnknownProject_SaysSo()
    {
        _gitlab.GetStatus("/api/v4/projects/team/gone", 404);

        var result = await RunAsync("team/gone#12");

        Assert.True(result.IsError);
        Assert.Contains("team/gone", result.Markdown, StringComparison.Ordinal);
        await _taskService.DidNotReceive().CreateAsync(Arg.Any<TaskRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fix_UnknownIssue_SaysSo()
    {
        _gitlab.GetStatus("/api/v4/projects/42/issues/99", 404);

        var result = await RunAsync("team/repo#99");

        Assert.True(result.IsError);
        Assert.Contains("team/repo#99", result.Markdown, StringComparison.Ordinal);
        await _taskService.DidNotReceive().CreateAsync(Arg.Any<TaskRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fix_ContextWithoutARepository_WarnsThatTheTaskNeedsOne()
    {
        var context = new TaskContext { Source = TaskSource.JiraIssue, SourceRef = "PROJ-7", Title = "Something" };

        var result = await RunAsync(string.Empty, Channel.Jira, "PROJ-7", Event(Channel.Jira, "PROJ-7", context));

        Assert.False(result.IsError);
        Assert.Contains("could not work out which repository", result.Markdown, StringComparison.Ordinal);
    }
}
