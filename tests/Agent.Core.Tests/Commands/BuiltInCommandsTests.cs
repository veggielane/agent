using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Commands;
using Agent.Core.Conversations;
using Agent.Core.Tasks;
using Agent.Core.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Core.Tests.Commands;

public sealed class BuiltInCommandsTests
{
    private static async Task<CommandResult> RunAsync(TestHost host, string caller, string text, Channel channel = Channel.Mattermost)
    {
        var identity = await host.ResolveAsync(caller, channel);
        return await host.Get<ICommandDispatcher>().DispatchAsync(text, host.CommandContext(identity, evt: host.Event(text, caller, isPrivate: true, channel: channel)));
    }

    [Fact]
    public async Task Help_ListsOnlyCommandsTheCallerMayRun()
    {
        using var host = TestHost.Create(s => s.AddCommandHandlers<SampleCommands>());

        var users = await RunAsync(host, "alice", "!help");
        Assert.Contains("!ping", users.Markdown);
        Assert.Contains("!greet", users.Markdown);
        Assert.DoesNotContain("!teamonly", users.Markdown);
        Assert.DoesNotContain("!reload", users.Markdown);

        var admin = await RunAsync(host, "root", "!help");
        Assert.Contains("!teamonly", admin.Markdown);
        Assert.Contains("!reload", admin.Markdown);
    }

    [Fact]
    public async Task Help_ForOneCommand_ShowsUsage()
    {
        using var host = TestHost.Create(s => s.AddCommandHandlers<SampleCommands>());
        var result = await RunAsync(host, "alice", "!help greet");
        Assert.Contains("!greet <name>", result.Markdown);
        Assert.Contains("Name to greet", result.Markdown);
    }

    [Fact]
    public async Task Whoami_ShowsRolesAndGroups()
    {
        using var host = TestHost.Create();
        var result = await RunAsync(host, "bob", "!whoami");
        Assert.Contains("bob", result.Markdown);
        Assert.Contains("Users, Team", result.Markdown);
        Assert.Contains("`g-team`", result.Markdown);
    }

    [Fact]
    public async Task Status_ReportsQueueAndTasks()
    {
        using var host = TestHost.Create();
        var result = await RunAsync(host, "alice", "!status");
        Assert.Contains("uptime", result.Markdown);
        Assert.Contains("inbound queue: 0", result.Markdown);
        Assert.Contains("active tasks: 0", result.Markdown);
    }

    [Fact]
    public async Task Tasks_All_RequiresTeam()
    {
        using var host = TestHost.Create();
        var denied = await RunAsync(host, "alice", "!tasks --all");
        Assert.True(denied.IsError);

        var ok = await RunAsync(host, "bob", "!tasks --all");
        Assert.Equal("No tasks.", ok.Markdown);
    }

    [Fact]
    public async Task Tasks_ListsOwnTasks_AndTaskShowsDetails()
    {
        using var host = TestHost.Create();
        var bob = await host.ResolveAsync("bob");
        var created = await host.Get<ITaskService>().CreateAsync(new TaskRequest
        {
            Source = TaskSource.GitLabIssue,
            SourceRef = "team/repo#1",
            Requester = bob,
            Instruction = "Do the thing",
            RepoUrl = "https://gitlab.internal/team/repo.git",
            ConversationId = "team/repo#1",
            NotifyChannel = Channel.GitLab,
            Title = "Do the thing",
        });

        var list = await RunAsync(host, "bob", "!tasks");
        Assert.Contains("| 1 | Queued | team/repo#1 |", list.Markdown);

        var other = await RunAsync(host, "alice", "!tasks");
        Assert.Equal("You have no tasks.", other.Markdown);

        var show = await RunAsync(host, "bob", $"!task {created.Id}");
        Assert.Contains("**Task #1** — Queued", show.Markdown);
        Assert.Contains("created", show.Markdown);

        var forbidden = await RunAsync(host, "alice", $"!task {created.Id}");
        Assert.True(forbidden.IsError);
    }

    [Fact]
    public async Task Task_HidesToolActionsUntilAskedFor()
    {
        using var host = TestHost.Create();
        var bob = await host.ResolveAsync("bob");
        var tasks = host.Get<ITaskService>();
        var created = await tasks.CreateAsync(new TaskRequest
        {
            Source = TaskSource.GitLabIssue,
            SourceRef = "team/repo#1",
            Requester = bob,
            Instruction = "Do the thing",
            RepoUrl = "https://gitlab.internal/team/repo.git",
            ConversationId = "team/repo#1",
            NotifyChannel = Channel.GitLab,
        });
        await tasks.RecordEventAsync(created.Id, "tool.write", "src/Thing.cs");
        await tasks.RecordEventAsync(created.Id, "tool.run", "dotnet test (failed, exit 1)");
        await tasks.RecordEventAsync(created.Id, "pushed", "Pushed agent/x (1 changed files): src/Thing.cs");

        var plain = await RunAsync(host, "bob", $"!task {created.Id}");
        Assert.Contains("tool actions: 2 recorded", plain.Markdown);
        Assert.Contains("--actions", plain.Markdown);
        Assert.Contains("pushed:", plain.Markdown);
        Assert.DoesNotContain("tool.run:", plain.Markdown);

        var detailed = await RunAsync(host, "bob", $"!task {created.Id} --actions");
        Assert.Contains("tool.write: src/Thing.cs", detailed.Markdown);
        Assert.Contains("tool.run: dotnet test (failed, exit 1)", detailed.Markdown);
        Assert.DoesNotContain("tool actions:", detailed.Markdown);
    }

    [Fact]
    public async Task Cancel_OwnershipRules()
    {
        using var host = TestHost.Create();
        var bob = await host.ResolveAsync("bob");
        var task = await host.Get<ITaskService>().CreateAsync(new TaskRequest
        {
            Source = TaskSource.JiraIssue,
            SourceRef = "PROJ-1",
            Requester = bob,
            Instruction = "x",
            RepoUrl = "https://gitlab.internal/team/repo.git",
            ConversationId = "PROJ-1",
            NotifyChannel = Channel.Jira,
        });

        var admin = await RunAsync(host, "root", $"!cancel {task.Id}");
        Assert.Contains("cancelled", admin.Markdown);

        var again = await RunAsync(host, "bob", $"!cancel {task.Id}");
        Assert.Contains("already Cancelled", again.Markdown);

        var retry = await RunAsync(host, "bob", $"!retry {task.Id}");
        Assert.Contains("re-queued", retry.Markdown);
        Assert.Equal(AgentTaskStatus.Queued, (await host.Get<ITaskService>().GetAsync(task.Id))!.Status);
    }

    [Fact]
    public async Task Model_ShowAndSet_RespectRoles()
    {
        using var host = TestHost.Create();

        var show = await RunAsync(host, "alice", "!model");
        Assert.Contains("answer model: `test-model`", show.Markdown);

        var usersSet = await RunAsync(host, "alice", "!model big");
        Assert.True(usersSet.IsError);

        var teamSet = await RunAsync(host, "bob", "!model big");
        Assert.Contains("`big`", teamSet.Markdown);
        Assert.Equal("big", host.Get<IConversationSettings>().GetModel("conv-1"));

        var teamGlobal = await RunAsync(host, "bob", "!model big --global");
        Assert.True(teamGlobal.IsError);

        var adminGlobal = await RunAsync(host, "root", "!model huge --global");
        Assert.Contains("Global answer model set", adminGlobal.Markdown);
        Assert.Equal("huge", host.Get<IConversationSettings>().GlobalModel);

        var reset = await RunAsync(host, "root", "!model reset --global");
        Assert.Null(host.Get<IConversationSettings>().GlobalModel);
    }

    [Fact]
    public async Task Reload_RequiresAdmin_AndReportsReloadables()
    {
        using var host = TestHost.Create();
        var denied = await RunAsync(host, "bob", "!reload");
        Assert.True(denied.IsError);

        var ok = await RunAsync(host, "root", "!reload");
        Assert.Contains("commands ✓", ok.Markdown);
    }
}
